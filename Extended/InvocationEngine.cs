using System.Diagnostics;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace IntrospectMcp.Extended;

public sealed partial class InvocationEngine : IAsyncDisposable
{
    public ServiceOptions Options { get; }
    public TypeModel Model { get; } = new();
    public AssemblyCatalog Catalog { get; }
    public ValueCodec Codec { get; }
    public Schemas Schemas { get; }
    public InvocationEngine(Assembly assembly, ServiceOptions? options = null)
    {
        Options = options ?? new(); Options.Validate();
        Catalog = new(assembly, Model, Options);
        Codec = new(Model, Catalog, Options);
        Codec.ValidateDtos();
        Schemas = new(Model, Catalog);
        InitializeLiveValues();
    }
    partial void InitializeLiveValues();
    partial void ConfigureScope(CallScope scope);
    partial void AddLiveTools(List<McpServerTool> tools);
    partial void AddCompositionTools(List<McpServerTool> tools);
    public CallScope NewScope(CancellationToken cancellation = default)
    { var scope = new CallScope(cancellation); ConfigureScope(scope); return scope; }
    public async Task<JsonObject> ExecuteAsync(string name, JsonObject args, CancellationToken cancellation = default)
    {
        await using var scope = NewScope(cancellation);
        try { return await InvokeAsync(Catalog.Find(name), args, scope); }
        catch (Exception e) { return Error(e, scope.Id); }
    }
    public async Task<JsonObject> InvokeAsync(MemberDescriptor member, JsonObject args, CallScope scope)
    {
        scope.Cancellation.ThrowIfCancellationRequested();
        if (member.Unsupported is not null) throw new BindingException("unsupported_method", $"{member.Signature} is not invocable: {member.Unsupported}.", "method", new() { ["reason"] = member.Unsupported });
        if (args.ToJsonString().Length > 2_000_000) throw new BindingException("input_too_large", "Arguments exceed the 2 MB character limit.");
        var method = Catalog.Close(member, member.Method.IsGenericMethodDefinition ? args["typeArgs"] as JsonArray : null);
        var parameters = method.GetParameters();
        var allowed = parameters.Where(p => !CallScope.IsInjected(p.ParameterType) && !(p.IsOut && !p.IsIn)).Select(p => JsonSupport.ParameterName(p)).ToHashSet(StringComparer.Ordinal);
        if (member.NeedsTarget) allowed.Add("target");
        if (method.IsGenericMethod) allowed.Add("typeArgs");
        foreach (var key in args.Select(p => p.Key)) if (!allowed.Contains(key)) throw new BindingException("unknown_argument", $"Unknown argument '{key}'. Check describe_type for this overload's schema.", key);
        if (BeforeBinding is not null) await BeforeBinding(args, scope);
        object? target = null;
        if (member.NeedsTarget)
        {
            if (!args.ContainsKey("target")) throw new BindingException("target_required", "Instance calls require target: {$handle:...} or a literal DTO. No implicit instance is created.", "target");
            target = Codec.Bind(args["target"], method.DeclaringType!, scope, "target");
        }
        var values = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i]; var t = JsonSupport.ValueType(p.ParameterType);
            // Reflection supplies zero-initialized out storage. Activator would run user struct constructors even in dryRun.
            if (p.IsOut && !p.IsIn) values[i] = null;
            else if (CallScope.IsInjected(t)) values[i] = scope.DryRun ? null : scope.Inject(t);
            else if (args.ContainsKey(JsonSupport.ParameterName(p))) values[i] = Codec.Bind(args[JsonSupport.ParameterName(p)], t, scope, JsonSupport.ParameterName(p), Model.Nullable(p));
            else if (p.HasDefaultValue) values[i] = p.DefaultValue;
            else if (Model.Nullable(p)) values[i] = null;
            else throw new BindingException("missing_argument", $"Required argument '{p.Name}' is missing.", p.Name, new() { ["expectedType"] = JsonSupport.TypeName(t) });
        }
        var returnType = method is MethodInfo info ? TypeModel.UnwrapReturn(info.ReturnType) : method.DeclaringType!;
        if (scope.DryRun)
        {
            scope.LiveResult = returnType == typeof(void) ? null : new SymbolicValue(returnType);
            foreach (var p in parameters.Where(p => p.ParameterType.IsByRef && !p.IsIn)) scope.LiveOutputs[JsonSupport.ParameterName(p)] = new SymbolicValue(JsonSupport.ValueType(p.ParameterType));
            return new() { ["callId"] = scope.Id, ["ok"] = true, ["dryRun"] = true, ["result"] = new JsonObject { ["$type"] = JsonSupport.TypeName(returnType) } };
        }
        object? result = method is ConstructorInfo ctor ? ctor.Invoke(values) : ((MethodInfo)method).Invoke(target, values);
        result = await Await(result);
        if (returnType == typeof(void)) result = null;
        scope.LiveResult = result;
        var outputs = new JsonObject();
        foreach (var (p, i) in parameters.Select((p, i) => (p, i)).Where(x => x.p.ParameterType.IsByRef && !x.p.IsIn))
        {
            scope.LiveOutputs[JsonSupport.ParameterName(p)] = values[i];
            outputs[JsonSupport.ParameterName(p)] = Codec.Encode(values[i], JsonSupport.ValueType(p.ParameterType), scope);
        }
        return new JsonObject { ["callId"] = scope.Id, ["ok"] = true, ["result"] = method is ConstructorInfo && result is not null ? scope.ExportHandle!(result, returnType) : await EncodeReturn(result, returnType, scope), ["outputs"] = outputs,
            ["progress"] = new JsonArray(scope.Progress.Select(p => p?.DeepClone()).ToArray()) };
    }
    // Per-async-call live values are consumed by pipelines without a JSON round-trip.
    public Func<JsonObject, CallScope, Task>? BeforeBinding { get; set; }
    public Func<object?, Type, CallScope, Task<JsonNode?>>? ReturnEncoder { get; set; }
    private Task<JsonNode?> EncodeReturn(object? value, Type type, CallScope scope) => ReturnEncoder?.Invoke(value, type, scope) ?? Task.FromResult(Codec.Encode(value, type, scope));
    private static async Task<object?> Await(object? value)
    {
        if (value is ValueTask vt) { await vt; return null; }
        if (value?.GetType() is Type t && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ValueTask<>)) value = t.GetMethod("AsTask")!.Invoke(value, null);
        if (value is Task task) { await task.ConfigureAwait(false); return task.GetType().GetProperty("Result")?.GetValue(task); }
        return value;
    }
    public JsonObject Error(Exception error, string callId)
    {
        var e = JsonSupport.Unwrap(error); var b = e as BindingException;
        var frames = new StackTrace(e, true).GetFrames()?.Where(f => f.GetMethod()?.DeclaringType?.Assembly == Catalog.Assembly).Take(20)
            .Select(f => $"{f.GetMethod()!.DeclaringType}.{f.GetMethod()!.Name}:{f.GetFileLineNumber()}").ToArray() ?? Array.Empty<string>();
        JsonNode? bindingHints = null;
        if (b?.Details["expectedType"] is JsonValue expected && expected.TryGetValue<string>(out var expectedName))
        { try { bindingHints = Flow.Hints(Catalog.ResolveType(expectedName)); } catch (BindingException) { } }
        return new() { ["callId"] = callId, ["isError"] = true, ["error"] = new JsonObject
        { ["code"] = b?.Code ?? (e is OperationCanceledException ? "cancelled" : "invocation_failed"), ["message"] = e.Message[..Math.Min(e.Message.Length, 2000)],
          ["exceptionType"] = e.GetType().FullName, ["path"] = b?.Path, ["details"] = b?.Details.DeepClone(), ["bindingHints"] = bindingHints, ["targetStack"] = JsonSupport.Node(frames),
          ["hint"] = "Use describe_type for exact arguments; find_implementations for $type; how_to_get for live inputs. No implicit conversion between object types is performed." } };
    }
    public JsonObject Coverage()
    {
        var all = Catalog.Members;
        bool Literal(MemberDescriptor m) => (!m.NeedsTarget || Model.Classify(m.Method.DeclaringType!).Literal) && Model.Classify(m.ReturnType).Literal &&
            m.Method.GetParameters().Where(p => !CallScope.IsInjected(p.ParameterType)).All(p => Model.Classify(p.ParameterType).Literal);
        return new() { ["assembly"] = Catalog.Assembly.GetName().Name, ["complete"] = Catalog.LoadDiagnostics.Count == 0,
            ["scope"] = "Successfully enumerated public declared methods and constructors; special members are explicit exclusions.", ["total"] = all.Count,
            ["literal"] = all.Count(m => m.Unsupported is null && Literal(m)), ["handle"] = all.Count(m => m.Unsupported is null && !Literal(m)),
            ["unsupported"] = all.Count(m => m.Unsupported is not null), ["unsupportedByReason"] = JsonSupport.Node(all.Where(m => m.Unsupported is not null).GroupBy(m => m.Unsupported!).ToDictionary(g => g.Key, g => g.Count())),
            ["dtoDemotions"] = JsonSupport.Node(Model.Demotions.ToDictionary(p => JsonSupport.TypeName(p.Key), p => p.Value)), ["loadDiagnostics"] = JsonSupport.Node(Catalog.LoadDiagnostics) };
    }
    public JsonObject Describe(Type type, int offset = 0, int limit = 20) => new() { ["type"] = JsonSupport.TypeName(type), ["shape"] = Model.Classify(type).Kind.ToString(), ["reason"] = Model.Classify(type).Reason,
        ["inputSchema"] = Schemas.For(type), ["totalMethods"] = Catalog.Members.Count(m => m.Method.DeclaringType == type),
        ["nextOffset"] = (long)offset + limit < Catalog.Members.Count(m => m.Method.DeclaringType == type) ? offset + limit : null, ["typeFlow"] = Flow.Hints(type), ["methods"] = new JsonArray(Catalog.Members.Where(m => m.Method.DeclaringType == type).Skip(offset).Take(limit).Select(m => (JsonNode)new JsonObject
        { ["name"] = m.Name, ["signature"] = m.Signature, ["description"] = m.Description, ["supported"] = m.Unsupported is null, ["reason"] = m.Unsupported,
          ["inputSchema"] = m.Unsupported is null ? Schemas.Inputs(m) : null, ["returnType"] = JsonSupport.TypeName(m.ReturnType), ["inputs"] = new JsonObject(TypeFlow.Inputs(m).Select(p => KeyValuePair.Create<string, JsonNode?>(p.Name, Flow.Hints(p.Type)))) }).ToArray()) };
    public List<McpServerTool> Tools()
    {
        var supported = Catalog.Members.Where(m => m.Unsupported is null).ToArray();
        var tools = new List<McpServerTool>();
        CompactMode = supported.Length > Options.ToolBudget;
        long tokens = 0;
        if (!CompactMode)
        {
            foreach (var member in supported)
            {
                var schema = Schemas.Inputs(member);
                tokens += (schema.ToJsonString().Length + member.Description.Length) / 4;
                if (tokens > Options.SchemaTokenBudget) { CompactMode = true; tools.Clear(); break; }
                tools.Add(new ExplicitTool(member.Name, member.Description + " Inputs accept literal JSON or recursive $handle/$ref references. Returns include callId, result and ref/out outputs.",
                    schema, async (a, s) => await InvokeAsync(member, a, s), this));
            }
        }
        tools.Add(Tool("coverage", "DLL coverage, unsupported reasons, and DTO round-trip self-test failures.", new(), async (a, s) => { await Task.CompletedTask; return Coverage(); }));
        tools.Add(Tool("list_types", "Search public types, including shapes; page with offset/limit, then use describe_type.", new() { ["query"] = JsonSupport.StringSchema(), ["offset"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 }, ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 } }, (a, s) =>
        {
            var found = Catalog.Types.Where(t => t.FullName!.Contains(a["query"]?.GetValue<string>() ?? "", StringComparison.OrdinalIgnoreCase)).OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
            int offset = JsonSupport.Integer(a, "offset", 0, 0, int.MaxValue), limit = JsonSupport.Integer(a, "limit", 30, 1, 100);
            return Task.FromResult<JsonNode?>(new JsonObject { ["total"] = found.Length, ["items"] = JsonSupport.Node(found.Skip(offset).Take(limit).Select(t => new { type = JsonSupport.TypeName(t), shape = Model.Classify(t).Kind.ToString(), reason = Model.Classify(t).Reason })), ["nextOffset"] = (long)offset + limit < found.Length ? offset + limit : null });
        }));
        tools.Add(Tool("describe_type", "Exact methods, constructors, schemas and machine-readable exclusions for a CLR type.", new() { ["type"] = JsonSupport.StringSchema(), ["offset"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 }, ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 } }, (a, s) => Task.FromResult<JsonNode?>(Describe(Catalog.ResolveType(JsonSupport.RequiredString(a, "type")), JsonSupport.Integer(a, "offset", 0, 0, int.MaxValue), JsonSupport.Integer(a, "limit", 20, 1, 100))), "type"));
        tools.Add(Tool("find_implementations", "Assignable concrete types; literal-capable types accept {$type:fullName,...properties}.", new() { ["type"] = JsonSupport.StringSchema(), ["offset"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 } }, (a, s) =>
        {
            var found = Catalog.Implementations(Catalog.ResolveType(JsonSupport.RequiredString(a, "type"))); int offset = JsonSupport.Integer(a, "offset", 0, 0, int.MaxValue);
            return Task.FromResult<JsonNode?>(new JsonObject { ["total"] = found.Length, ["items"] = JsonSupport.Node(found.Skip(offset).Take(50).Select(t => new { type = JsonSupport.TypeName(t), literal = Model.Classify(t).Literal })), ["nextOffset"] = (long)offset + 50 < found.Length ? offset + 50 : null });
        }, "type"));
        tools.Add(Tool("invoke_method", "Invoke by the exact name returned by describe_type. arguments contains the method inputs including target/typeArgs where applicable.", new() { ["method"] = JsonSupport.StringSchema(), ["arguments"] = new JsonObject { ["type"] = "object" } },
            async (a, s) => await InvokeAsync(Catalog.Find(JsonSupport.RequiredString(a, "method")), a.ContainsKey("arguments") ? JsonSupport.Object(a["arguments"], "arguments") : new(), s), "method"));
        AddLiveTools(tools); AddCompositionTools(tools);
        return tools;
    }
    public ExplicitTool Tool(string name, string description, JsonObject props, Func<JsonObject, CallScope, Task<JsonNode?>> action, params string[] required) => new(name, description, JsonSupport.ObjectSchema(props, required), action, this);
    public async ValueTask DisposeAsync() { await DisposeLiveValues(); }
    private Func<ValueTask> DisposeLiveValues { get; set; } = () => ValueTask.CompletedTask;
}

public sealed class ExplicitTool : McpServerTool
{
    private readonly Func<JsonObject, CallScope, Task<JsonNode?>> _action;
    private readonly InvocationEngine _engine;
    public override Tool ProtocolTool { get; }
    public override IReadOnlyList<object> Metadata { get; } = Array.Empty<object>();
    public ExplicitTool(string name, string description, JsonObject schema, Func<JsonObject, CallScope, Task<JsonNode?>> action, InvocationEngine engine)
    {
        (_action, _engine) = (action, engine);
        ProtocolTool = new Tool { Name = name, Description = description, InputSchema = JsonSerializer.SerializeToElement(schema) };
    }
    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        await using var scope = _engine.NewScope(cancellationToken);
        JsonObject result;
        try
        {
            var args = new JsonObject(request.Params.Arguments?.Select(p => KeyValuePair.Create(p.Key, JsonNode.Parse(p.Value.GetRawText()))) ?? Array.Empty<KeyValuePair<string, JsonNode?>>());
            var properties = ProtocolTool.InputSchema.GetProperty("properties");
            foreach (var pair in args) if (!properties.TryGetProperty(pair.Key, out _)) throw new BindingException("unknown_argument", $"Unknown argument '{pair.Key}' for {ProtocolTool.Name}.", pair.Key);
            var payload = await _action(args, scope);
            result = payload is JsonObject obj && obj.ContainsKey("callId") ? obj : new JsonObject { ["callId"] = scope.Id, ["ok"] = true, ["result"] = payload };
        }
        catch (Exception e) { result = _engine.Error(e, scope.Id); }
        return new CallToolResult { IsError = result["isError"]?.GetValue<bool>() == true, StructuredContent = JsonSerializer.SerializeToElement(result), Content = new List<ContentBlock> { new TextContentBlock { Text = result.ToJsonString() } } };
    }
}
