using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace IntrospectMcp.Extended;

public sealed record SymbolicValue(Type Type);

public sealed class ValueCodec
{
    private readonly TypeModel _model;
    private readonly AssemblyCatalog _catalog;
    private readonly ServiceOptions _options;
    private static readonly JsonSerializerOptions ScalarOptions = new() { NumberHandling = JsonNumberHandling.AllowReadingFromString, Converters = { new JsonStringEnumConverter() } };
    public ValueCodec(TypeModel model, AssemblyCatalog catalog, ServiceOptions options) => (_model, _catalog, _options) = (model, catalog, options);
    public object? Bind(JsonNode? node, Type type, CallScope scope, string path = "$", bool nullable = false, int depth = 0)
    {
        scope.Cancellation.ThrowIfCancellationRequested();
        if (depth > _options.MaxDepth) throw new BindingException("max_depth", "Input nesting exceeds the configured limit.", path);
        type = JsonSupport.ValueType(type);
        Type referenceType = type;
        Type? underlying = Nullable.GetUnderlyingType(type);
        if (node is null)
        {
            if (nullable || underlying is not null) return null;
            throw new BindingException("null_not_allowed", $"{type} does not allow null here.", path);
        }
        type = underlying ?? type;
        if (_model.Classify(type).Kind == ShapeKind.Unsupported) throw new BindingException("unsupported_type", $"{type} cannot cross the reflection boundary.", path);
        if (node is JsonObject tagged)
        {
            if (new[] { "$handle", "$ref", "$step", "$method" }.Any(tagged.ContainsKey))
            {
                if (scope.ResolveReference is null) throw new BindingException("reference_unavailable", "This reference is unavailable in the current call.", path);
                var live = scope.ResolveReference(tagged, referenceType, path);
                if (live is null && !nullable && underlying is null) throw new BindingException("null_not_allowed", "Projected value is null.", path);
                return live;
            }
            if (tagged.ContainsKey("$expr")) throw new BindingException("expression_disabled", "Executable expressions are disabled. Bind a registered $method instead.", path);
            if (tagged["$type"] is not null)
            {
                var runtime = _catalog.ResolveType(JsonSupport.RequiredString(tagged, "$type"));
                if (!type.IsAssignableFrom(runtime)) throw Mismatch(type, runtime, path);
                if (((runtime.IsAbstract || runtime.IsInterface) && !TypeModel.IsScalar(runtime)) || runtime.ContainsGenericParameters) throw new BindingException("invalid_implementation", "Choose a concrete, closed implementation.", path);
                var payload = tagged.ContainsKey("$value") ? tagged["$value"] : WithoutTag(tagged);
                return Bind(payload, runtime, scope, path, nullable, depth + 1);
            }
        }
        try
        {
            if (type == typeof(object)) return Ordinary(node, scope, path, depth);
            if (TypeModel.IsScalar(type))
            {
                if (type == typeof(Uri)) return new Uri(node.GetValue<string>(), UriKind.RelativeOrAbsolute);
                if (type == typeof(Encoding)) return Encoding.GetEncoding(node.GetValue<string>());
                if (type == typeof(CultureInfo)) return CultureInfo.GetCultureInfo(node.GetValue<string>());
                return node.Deserialize(type, ScalarOptions);
            }
            if (typeof(Stream).IsAssignableFrom(type)) return BindStream(node, type, scope, path);
            var shape = _model.Classify(type);
            if (shape.Kind == ShapeKind.Dictionary)
            {
                var obj = JsonSupport.Object(node, path);
                if (obj.Count > _options.MaxItems) throw new BindingException("too_many_items", "Dictionary exceeds the item limit.", path);
                var args = (TypeModel.Generic(type, typeof(IDictionary<,>)) ?? TypeModel.Generic(type, typeof(IReadOnlyDictionary<,>)))!.GetGenericArguments();
                var dict = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(args))!;
                foreach (var item in obj)
                {
                    string key = item.Key.StartsWith("$$", StringComparison.Ordinal) ? item.Key[1..] : item.Key;
                    JsonNode keyNode = args[0] == typeof(bool) && bool.TryParse(key, out bool boolean) ? JsonValue.Create(boolean)! : JsonValue.Create(key)!;
                    var boundKey = Bind(keyNode, args[0], scope, path + ".<key>", false, depth + 1)!;
                    var value = Bind(item.Value, args[1], scope, path + "." + item.Key, !args[1].IsValueType, depth + 1);
                    if (!scope.DryRun) dict.Add(boundKey, value);
                }
                if (scope.DryRun) return new SymbolicValue(type);
                if (type.IsInstanceOfType(dict)) return dict;
                var ctor = type.GetConstructor(new[] { typeof(IDictionary<,>).MakeGenericType(args) });
                return ctor?.Invoke(new object[] { dict }) ?? throw new BindingException("collection_constructor", "Use a handle for this dictionary implementation.", path);
            }
            if (shape.Kind == ShapeKind.Sequence)
            {
                var array = node as JsonArray ?? throw new BindingException("invalid_argument", "Expected a JSON array or compatible handle.", path);
                if (array.Count > _options.MaxItems) throw new BindingException("too_many_items", "Array exceeds the item limit.", path);
                var element = type.IsArray ? type.GetElementType()! : TypeModel.Generic(type, typeof(IEnumerable<>))!.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
                foreach (var (value, i) in array.Select((n, i) => (n, i)))
                {
                    var bound = Bind(value, element, scope, $"{path}[{i}]", !element.IsValueType || Nullable.GetUnderlyingType(element) is not null, depth + 1);
                    if (!scope.DryRun) list.Add(bound);
                }
                if (scope.DryRun) return new SymbolicValue(type);
                if (type.IsArray) { var result = Array.CreateInstance(element, list.Count); list.CopyTo(result, 0); return result; }
                if (type.IsInstanceOfType(list)) return list;
                var setType = typeof(HashSet<>).MakeGenericType(element);
                if (type.IsAssignableFrom(setType)) return Activator.CreateInstance(setType, list)!;
                var ctor = type.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType.IsInstanceOfType(list));
                return ctor?.Invoke(new object[] { list }) ?? throw new BindingException("collection_constructor", "Use a handle for this collection implementation.", path);
            }
            if (shape.Kind == ShapeKind.Dto)
            {
                var obj = JsonSupport.Object(node, path);
                var members = _model.Members(type);
                var bound = new Dictionary<DataMember, object?>();
                foreach (var key in obj.Select(p => p.Key))
                    if (!members.Any(m => JsonSupport.Key(m.Name) == key)) throw new BindingException("unknown_property", $"Unknown property '{key}' on {type}. Dollar-prefixed data keys must be doubled.", path + "." + key);
                foreach (var member in members)
                {
                    var key = JsonSupport.Key(member.Name);
                    if (obj.ContainsKey(key)) bound[member] = Bind(obj[key], member.Type, scope, path + "." + key, member.Nullable, depth + 1);
                    else if (!_model.OptionalMember(type, member)) throw new BindingException("missing_property", $"Required property '{key}' is missing.", path + "." + key);
                }
                if (scope.DryRun) return new SymbolicValue(type);
                var ctor = _model.DtoConstructor(type);
                var consumed = new HashSet<DataMember>();
                var ctorValues = (ctor?.GetParameters() ?? Array.Empty<ParameterInfo>()).Select(p =>
                {
                    var member = members.Single(m => p.Name?.Equals(m.ClrName, StringComparison.OrdinalIgnoreCase) == true);
                    consumed.Add(member);
                    return bound.TryGetValue(member, out var value) ? value : p.HasDefaultValue ? p.DefaultValue : null;
                }).ToArray();
                var dto = ctor is null ? Activator.CreateInstance(type)! : ctor.Invoke(ctorValues);
                foreach (var pair in bound.Where(p => p.Key.CanWrite && !consumed.Contains(p.Key))) pair.Key.Set(dto, pair.Value);
                return dto;
            }
            throw new BindingException("handle_required", $"{type} needs an assignable $handle or a concrete $type. Use find_implementations or how_to_get.", path,
                new() { ["expectedType"] = JsonSupport.TypeName(type), ["reason"] = shape.Reason });
        }
        catch (BindingException) { throw; }
        catch (Exception e) when (e is not OperationCanceledException)
        { throw new BindingException("invalid_argument", $"Cannot bind {type}: {JsonSupport.Unwrap(e).Message}", path, new() { ["expectedType"] = JsonSupport.TypeName(type) }); }
    }
    private static JsonObject WithoutTag(JsonObject obj) { var copy = (JsonObject)obj.DeepClone(); copy.Remove("$type"); return copy; }
    private object? Ordinary(JsonNode node, CallScope scope, string path, int depth)
    {
        if (node is JsonObject obj) return obj.ToDictionary(p => p.Key.StartsWith("$$", StringComparison.Ordinal) ? p.Key[1..] : p.Key,
            p => Bind(p.Value, typeof(object), scope, path + "." + p.Key, true, depth + 1));
        if (node is JsonArray arr) return arr.Select((v, i) => Bind(v, typeof(object), scope, $"{path}[{i}]", true, depth + 1)).ToList();
        var json = JsonSerializer.SerializeToElement(node);
        return json.ValueKind switch { JsonValueKind.String => json.GetString(), JsonValueKind.True => true, JsonValueKind.False => false,
            JsonValueKind.Number => json.TryGetInt64(out long n) ? (object)n : json.GetDouble(), _ => null };
    }
    private object BindStream(JsonNode node, Type type, CallScope scope, string path)
    {
        var obj = JsonSupport.Object(node, path);
        if (obj.Count != 1) throw new BindingException("invalid_stream", "Use exactly one of base64, path, or $handle.", path);
        Stream stream;
        if (obj["base64"] is not null)
        {
            var data = Convert.FromBase64String(obj["base64"]!.GetValue<string>());
            if (scope.DryRun) return new SymbolicValue(type);
            stream = new MemoryStream(data, writable: false);
        }
        else
        {
            if (_options.FileRoot is null) throw new BindingException("file_access_disabled", "Stream path inputs require --file-root. Alternatively use {base64:...}.", path);
            string root = Path.GetFullPath(_options.FileRoot), file = Path.GetFullPath(JsonSupport.RequiredString(obj, "path"), root);
            if (!file.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new BindingException("file_outside_root", "Path is outside --file-root.", path);
            for (var part = new FileInfo(file) as FileSystemInfo; part is not null; part = part is FileInfo f ? f.Directory : ((DirectoryInfo)part).Parent)
                if (part.LinkTarget is not null) throw new BindingException("symlink_not_allowed", "Stream paths may not traverse symbolic links.", path);
            if (scope.DryRun) return new SymbolicValue(type);
            stream = File.OpenRead(file);
        }
        if (!type.IsInstanceOfType(stream)) { stream.Dispose(); throw new BindingException("stream_type", $"The supplied stream is not a {type}.", path); }
        scope.Owned.Add(stream);
        return stream;
    }
    public JsonNode? Encode(object? value, Type declared, CallScope scope, int depth = 0, HashSet<object>? seen = null)
    {
        if (value is null) return null;
        Type actual = value.GetType();
        if (_model.Classify(actual).Kind == ShapeKind.Unsupported) throw new BindingException("unsupported_return", $"Runtime value {actual} is pointer/stack-only and cannot be referenced.", "return");
        if (value is double d && !double.IsFinite(d) || value is float f && !float.IsFinite(f)) return Export(value, declared, scope);
        if (value is SymbolicValue symbol) return new JsonObject { ["$type"] = JsonSupport.TypeName(symbol.Type), ["dryRun"] = true };
        seen ??= new(ReferenceEqualityComparer.Instance);
        if (depth > _options.MaxDepth || !actual.IsValueType && !seen.Add(value)) return Export(value, declared, scope);
        try
        {
            JsonNode? result;
            if (TypeModel.IsScalar(actual) || value is Encoding or CultureInfo)
            {
                result = value switch { Encoding e => JsonValue.Create(e.WebName), CultureInfo c => JsonValue.Create(c.Name), _ => JsonSerializer.SerializeToNode(value, actual, ScalarOptions) };
                var wireType = value is Encoding ? typeof(Encoding) : value is CultureInfo ? typeof(CultureInfo) : actual;
                if (wireType != (Nullable.GetUnderlyingType(declared) ?? declared) && declared != typeof(void))
                    return new JsonObject { ["$type"] = JsonSupport.TypeName(wireType), ["$value"] = result };
                return result;
            }
            var shape = _model.Classify(actual);
            if (value is IDictionary dictionary)
            {
                var dict = new JsonObject();
                var generic = TypeModel.Generic(actual, typeof(IDictionary<,>));
                Type element = generic?.GetGenericArguments()[1] ?? typeof(object);
                foreach (DictionaryEntry pair in dictionary)
                {
                    if (dict.Count >= _options.MaxItems) return Export(value, declared, scope);
                    dict[JsonSupport.Key(Convert.ToString(pair.Key, CultureInfo.InvariantCulture)!)] = Encode(pair.Value, element, scope, depth + 1, seen);
                }
                return actual == declared ? dict : new JsonObject { ["$type"] = JsonSupport.TypeName(actual), ["$value"] = dict };
            }
            if (shape.Kind == ShapeKind.Dto)
            {
                var obj = new JsonObject();
                foreach (var member in _model.Members(actual)) obj[JsonSupport.Key(member.Name)] = Encode(member.Get(value), member.Type, scope, depth + 1, seen);
                if (actual != declared) obj["$type"] = JsonSupport.TypeName(actual);
                return obj;
            }
            // Sequence output is bounded; phase-two's return pager supplies live cursors.
            if (value is IEnumerable sequence && value is not string)
            {
                if (scope.ExportSequence is not null && (value is not ICollection known || known.Count > _options.PageSize))
                    return scope.ExportSequence(value, declared, depth);
                var items = new JsonArray();
                Type element = TypeModel.Generic(actual, typeof(IEnumerable<>))?.GetGenericArguments()[0] ?? typeof(object);
                foreach (var item in sequence)
                {
                    if (items.Count >= _options.PageSize) return Export(value, declared, scope);
                    items.Add(Encode(item, element, scope, depth + 1, seen));
                }
                return actual == declared ? items : new JsonObject { ["$type"] = JsonSupport.TypeName(actual), ["$value"] = items };
            }
            return Export(value, declared, scope);
        }
        finally { if (!actual.IsValueType) seen.Remove(value); }
    }
    public static BindingException Mismatch(Type expected, Type actual, string path) => new("type_mismatch", $"Expected {expected}; got {actual}. No implicit object-to-object conversion is performed.", path,
        new() { ["expectedType"] = JsonSupport.TypeName(expected), ["actualType"] = JsonSupport.TypeName(actual) });
    private static JsonNode Export(object value, Type declared, CallScope scope) => scope.ExportHandle?.Invoke(value, declared) ??
        throw new BindingException("handle_required", $"{value.GetType()} needs a live handle.", "return");
    public void ValidateDtos()
    {
        foreach (var type in _catalog.Types.Where(t => _model.Classify(t).Kind == ShapeKind.Dto))
        {
            try
            {
                using var scope = new SyncScope();
                var sample = _model.Sample(type);
                var first = Bind(sample, type, scope.Value);
                var json = Encode(first, type, scope.Value);
                var second = Encode(Bind(json, type, scope.Value), type, scope.Value);
                if (!JsonNode.DeepEquals(json, second)) throw new InvalidOperationException("Round-trip changed the synthetic DTO data");
            }
            catch (Exception e) { _model.Demote(type, "dto_self_test_failed: " + JsonSupport.Unwrap(e).Message); Console.Error.WriteLine($"[dto-demoted] {type}: {JsonSupport.Unwrap(e).Message}"); }
        }
    }
    private sealed class SyncScope : IDisposable { public CallScope Value { get; } = new(); public void Dispose() => Value.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}
