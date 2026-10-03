using System.Collections;
using ModelContextProtocol.Server;

namespace IntrospectMcp.Extended;

public sealed partial class InvocationEngine
{
    public HandleStore Handles { get; private set; } = null!;
    partial void InitializeLiveValues()
    {
        Handles = new(Options);
        BeforeBinding = AcquireReferences;
        ReturnEncoder = async (value, type, scope) => value is not null && IsSequence(value)
            ? await StartSequence(value, type, scope, 0) : Codec.Encode(value, type, scope);
        DisposeLiveValues = () => Handles.DisposeAsync();
    }
    partial void ConfigureScope(CallScope scope)
    {
        scope.ResolveReference = (obj, type, path) => ResolveReference(obj, type, path, scope);
        scope.ExportHandle = (value, type) => Export(value, type, scope);
        scope.ExportSequence = (value, type, depth) => StartSequence(value, type, scope, depth).GetAwaiter().GetResult()!;
        scope.Owned.Add(new DrainOnExit(Handles));
    }
    private sealed class DrainOnExit : IAsyncDisposable
    { private readonly HandleStore _store; public DrainOnExit(HandleStore store) => _store = store; public ValueTask DisposeAsync() => _store.DrainAsync(); }
    private static bool IsSequence(object value) => value is not string and not byte[] and not IDictionary && (value is IEnumerable || TypeModel.Generic(value.GetType(), typeof(IAsyncEnumerable<>)) is not null);
    private HandleEntry Hold(CallScope scope, HandleLease lease)
    {
        if (scope.Handles.TryGetValue(lease.Entry.Id, out var existing)) { lease.DisposeAsync().GetAwaiter().GetResult(); return existing.Entry; }
        scope.Handles.Add(lease.Entry.Id, lease); scope.Owned.Add(lease);
        return lease.Entry;
    }
    private JsonNode Export(object value, Type type, CallScope scope)
    {
        HandleEntry entry;
        try { entry = Hold(scope, Handles.Register(value, type, scope.Id)); }
        catch
        {
            if (Handles.IdOf(value) is null && (value is IDisposable or IAsyncDisposable) && !scope.Owned.Any(x => ReferenceEquals(x, value))) scope.Owned.Add(value);
            throw;
        }
        scope.Owned.RemoveAll(item => ReferenceEquals(item, value)); // Resource ownership transfers only after registration succeeds.
        if (value is Delegate d)
        {
            if (Handles.IdOf(d.Target) is string targetId) Handles.Depend(entry, targetId);
            // Factory-created closures may capture borrowed inputs behind a compiler-generated target.
            foreach (var borrowed in scope.Handles.Values.ToArray()) Handles.Depend(entry, borrowed.Entry.Id);
        }
        if (scope.RetainIn is CallScope parent && !parent.Handles.ContainsKey(entry.Id)) Hold(parent, Handles.Pin(entry.Id));
        return HandleStore.Describe(entry);
    }
    private async Task AcquireReferences(JsonObject args, CallScope scope)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Visit(JsonNode? node, int depth)
        {
            if (depth > Options.MaxDepth) throw new BindingException("max_depth", "Reference nesting exceeds the configured limit.");
            if (node is JsonObject obj)
            {
                if (obj.ContainsKey("$handle")) { ids.Add(JsonSupport.RequiredString(obj, "$handle")); return; }
                if (obj.ContainsKey("$ref")) { ids.Add(JsonSupport.RequiredString(obj, "$ref")); return; }
                if (obj.ContainsKey("$step") && scope.Steps.TryGetValue(JsonSupport.RequiredString(obj, "$step"), out var live))
                {
                    if (obj["output"] is not null && scope.StepOutputs.TryGetValue(JsonSupport.RequiredString(obj, "$step"), out var outputs)) outputs.TryGetValue(obj["output"]!.GetValue<string>(), out live);
                    if (Handles.IdOf(live) is string id) ids.Add(id);
                }
                foreach (var pair in obj) Visit(pair.Value, depth + 1);
            }
            else if (node is JsonArray array) foreach (var child in array) Visit(child, depth + 1);
        }
        Visit(args, 0);
        await Acquire(ids, scope);
    }
    private async Task Acquire(IEnumerable<string> ids, CallScope scope)
    {
        // Pin first, then lock all referenced handles in a stable order to avoid AB/BA deadlocks.
        var leases = new List<HandleLease>();
        foreach (string id in ids.Distinct().OrderBy(x => x, StringComparer.Ordinal))
        {
            if (scope.Handles.ContainsKey(id)) continue;
            var lease = Handles.Pin(id); Hold(scope, lease); leases.Add(lease);
        }
        foreach (var lease in leases) await lease.LockAsync(scope.Cancellation);
    }
    private object? ResolveReference(JsonObject obj, Type expected, string path, CallScope scope)
    {
        if (new[] { "$handle", "$ref", "$step", "$method" }.Count(obj.ContainsKey) != 1) throw new BindingException("ambiguous_reference", "Use exactly one reference marker.", path);
        if (obj.ContainsKey("$method")) return BindDelegate(obj, expected, scope, path);
        object? value;
        if (obj.ContainsKey("$step"))
        {
            string step = JsonSupport.RequiredString(obj, "$step");
            if (!scope.Steps.TryGetValue(step, out value)) throw new BindingException("unknown_step", $"Step '{step}' must exist earlier in the same pipeline.", path);
            if (obj["output"] is not null && (!scope.StepOutputs.TryGetValue(step, out var outputs) || !outputs.TryGetValue(obj["output"]!.GetValue<string>(), out value)))
                throw new BindingException("unknown_output", "The referenced step has no such ref/out output.", path);
        }
        else
        {
            string id = JsonSupport.RequiredString(obj, obj.ContainsKey("$ref") ? "$ref" : "$handle");
            var entry = scope.Handles.TryGetValue(id, out var lease) ? lease.Entry : Hold(scope, Handles.Pin(id));
            value = scope.DryRun ? new SymbolicValue(entry.Value.GetType()) : entry.Value;
        }
        if (obj["path"] is not null) value = Projection.Read(value, obj["path"]!.GetValue<string>(), scope.DryRun);
        if (value is null)
        {
            if (expected.IsValueType && Nullable.GetUnderlyingType(expected) is null) throw new BindingException("null_not_allowed", "Projected value is null.", path);
            return null;
        }
        Type actual = value is SymbolicValue symbolic ? symbolic.Type : value.GetType();
        if (!expected.IsAssignableFrom(actual))
        {
            var mismatch = ValueCodec.Mismatch(expected, actual, path);
            mismatch.Details["conversionCandidates"] = JsonSupport.Node(Catalog.Members.Where(m => m.Unsupported is null && m.Method.IsStatic && m.Method.GetParameters().Length == 1 &&
                m.Method.GetParameters()[0].ParameterType.IsAssignableFrom(actual) && expected.IsAssignableFrom(m.ReturnType)).Take(3).Select(m => m.Name));
            throw mismatch;
        }
        return value;
    }
    private object BindDelegate(JsonObject obj, Type expected, CallScope scope, string path)
    {
        if (!typeof(Delegate).IsAssignableFrom(expected) || expected == typeof(Delegate)) throw new BindingException("delegate_type", "$method requires a concrete delegate parameter type.", path);
        var member = Catalog.Find(JsonSupport.RequiredString(obj, "$method"));
        if (member.Unsupported is not null) throw new BindingException("unsupported_method", member.Unsupported, path);
        if (Catalog.Close(member, obj["typeArgs"] as JsonArray) is not MethodInfo method) throw new BindingException("delegate_method", "Constructors cannot be bound as delegates.", path);
        var invoke = expected.GetMethod("Invoke")!;
        var given = method.GetParameters(); var wanted = invoke.GetParameters();
        if (given.Length != wanted.Length || !invoke.ReturnType.IsAssignableFrom(method.ReturnType) || given.Where((p, i) => !p.ParameterType.IsAssignableFrom(wanted[i].ParameterType)).Any())
            throw new BindingException("delegate_signature", $"{member.Name} is not compatible with {expected}.", path);
        object? target = null;
        if (!method.IsStatic)
        {
            if (!obj.ContainsKey("target")) throw new BindingException("target_required", "An instance delegate needs target.", path);
            target = Codec.Bind(obj["target"], method.DeclaringType!, scope, path + ".target");
        }
        if (scope.DryRun) return new SymbolicValue(expected);
        return method.IsStatic ? method.CreateDelegate(expected) : method.CreateDelegate(expected, target);
    }
    private async Task<JsonNode?> StartSequence(object value, Type declared, CallScope scope, int depth, int? pageSize = null)
    {
        if (depth > Options.MaxDepth || !scope.ActiveSequences.Add(value)) return Export(value, declared, scope);
        try
        {
            var root = (JsonObject)Export(value, declared, scope);
            string sourceId = root["$handle"]!.GetValue<string>();
            var cursor = SequenceCursor.Create(value, sourceId, Handles.Lifetime);
            HandleEntry entry;
            try { entry = Hold(scope, Handles.Register(cursor, typeof(SequenceCursor), scope.Id)); }
            catch { await cursor.DisposeAsync(); throw; }
            Handles.Depend(entry, sourceId);
            if (scope.RetainIn is CallScope parent && !parent.Handles.ContainsKey(entry.Id)) Hold(parent, Handles.Pin(entry.Id));
            return await Page(entry, scope, pageSize ?? Options.PageSize, depth);
        }
        finally { scope.ActiveSequences.Remove(value); }
    }
    private async Task<JsonObject> Page(HandleEntry entry, CallScope scope, int size, int depth = 0)
    {
        var cursor = (SequenceCursor)entry.Value;
        try
        {
            var (items, more) = await cursor.Page(size, scope.Cancellation);
            var source = scope.Handles.TryGetValue(cursor.SourceId, out var held) ? held.Entry : Hold(scope, Handles.Pin(cursor.SourceId));
            var response = HandleStore.Describe(source);
            response["items"] = new JsonArray(items.Select(value => Codec.Encode(value, cursor.ElementType, scope, depth + 1)).ToArray());
            response["elementType"] = JsonSupport.TypeName(cursor.ElementType);
            response["cursor"] = more ? entry.Id : null;
            response["next"] = more ? new JsonObject { ["tool"] = "read_sequence", ["arguments"] = new JsonObject { ["cursor"] = entry.Id, ["pageSize"] = size } } : null;
            if (!more) Handles.RetireLeased(entry, "completed");
            return response;
        }
        catch { Handles.RetireLeased(entry, "enumeration_failed"); throw; }
    }
    partial void AddLiveTools(List<McpServerTool> tools)
    {
        tools.Add(Tool("list_handles", "List this session's live objects, producing calls, TTL/capacity and disposal diagnostics. Paginate with offset/limit.", new() { ["offset"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 }, ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 } }, async (a, s) => { await Handles.SweepAsync(); return Handles.List(JsonSupport.Integer(a, "offset", 0, 0, int.MaxValue), JsonSupport.Integer(a, "limit", 50, 1, 100)); }));
        tools.Add(Tool("release_handle", "Release/dispose a live object or cursor. Busy objects cannot be released while calls/dependent cursors use them.", new() { ["handle"] = JsonSupport.StringSchema() }, async (a, s) =>
        { await Handles.ReleaseAsync(JsonSupport.RequiredString(a, "handle")); return new JsonObject { ["released"] = true }; }, "handle"));
        tools.Add(Tool("read_sequence", "Read the next bounded sync/async sequence page. Supply cursor to continue, or handle to start a fresh enumeration.", new()
        { ["cursor"] = JsonSupport.StringSchema(), ["handle"] = JsonSupport.StringSchema(), ["pageSize"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 200 } }, async (a, s) =>
        {
            if (a.ContainsKey("cursor") == a.ContainsKey("handle")) throw new BindingException("invalid_argument", "Specify exactly one of cursor or handle.");
            int size = JsonSupport.Integer(a, "pageSize", Options.PageSize, 1, 200);
            string id = JsonSupport.RequiredString(a, a.ContainsKey("cursor") ? "cursor" : "handle");
            using var probe = new LeaseProbe(Handles.Pin(id));
            var ids = probe.Lease.Entry.Value is SequenceCursor cursor ? new[] { id, cursor.SourceId } : new[] { id };
            await Acquire(ids, s);
            var entry = s.Handles[id].Entry;
            return entry.Value is SequenceCursor ? await Page(entry, s, size) : await StartSequence(entry.Value, entry.DeclaredType, s, 0, size);
        }));
        tools.Add(Tool("read_stream", "Read up to 65536 bytes from a Stream handle at its current position. Returns base64 and EOF; read again to continue.", new()
        { ["handle"] = JsonSupport.StringSchema(), ["maxBytes"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 65536 } }, async (a, s) =>
        {
            string id = JsonSupport.RequiredString(a, "handle"); await Acquire(new[] { id }, s);
            if (s.Handles[id].Entry.Value is not Stream stream) throw new BindingException("not_a_stream", "The handle is not a Stream.");
            int count = JsonSupport.Integer(a, "maxBytes", 8192, 1, 65536); var buffer = new byte[count];
            int read = await stream.ReadAsync(buffer.AsMemory(), s.Cancellation);
            return new JsonObject { ["base64"] = Convert.ToBase64String(buffer, 0, read), ["bytesRead"] = read, ["eof"] = read == 0 || stream.CanSeek && stream.Position >= stream.Length,
                ["next"] = new JsonObject { ["tool"] = "read_stream", ["arguments"] = new JsonObject { ["handle"] = id, ["maxBytes"] = count } } };
        }, "handle"));
    }
    private sealed class LeaseProbe : IDisposable
    { public HandleLease Lease { get; } public LeaseProbe(HandleLease lease) => Lease = lease; public void Dispose() => Lease.DisposeAsync().GetAwaiter().GetResult(); }
}
