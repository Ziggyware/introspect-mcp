using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace IntrospectMcp.Extended;

public enum ShapeKind { Scalar, Sequence, Dictionary, Dto, Polymorphic, Handle, Unsupported }
public sealed record TypeShape(Type Type, ShapeKind Kind, string? Reason = null)
{
    public bool Literal => Kind is ShapeKind.Scalar or ShapeKind.Sequence or ShapeKind.Dictionary or ShapeKind.Dto or ShapeKind.Polymorphic;
}
public sealed record DataMember(string Name, Type Type, bool Nullable, PropertyInfo? Property, FieldInfo? Field)
{
    public string ClrName => Property?.Name ?? Field!.Name;
    public bool CanWrite => Property is null ? !Field!.IsInitOnly : Property.SetMethod?.IsPublic == true;
    public object? Get(object instance) => Property is null ? Field!.GetValue(instance) : Property.GetValue(instance);
    public void Set(object instance, object? value) { if (Property is null) Field!.SetValue(instance, value); else Property.SetValue(instance, value); }
}

public sealed class TypeModel
{
    private readonly Dictionary<Type, TypeShape> _cache = new();
    private readonly Dictionary<Type, string> _demoted = new();
    private readonly NullabilityInfoContext _nullability = new();
    public IReadOnlyDictionary<Type, string> Demotions => _demoted;
    public static Type? Generic(Type t, Type definition) => t.IsGenericType && t.GetGenericTypeDefinition() == definition ? t :
        t.GetInterfaces().FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == definition);
    public static Type UnwrapReturn(Type t)
    {
        if (t == typeof(Task) || t == typeof(ValueTask)) return typeof(void);
        return t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(Task<>) || t.GetGenericTypeDefinition() == typeof(ValueTask<>)) ? t.GetGenericArguments()[0] : t;
    }
    public static bool IsScalar(Type t) => t.IsEnum || t.IsPrimitive && t != typeof(IntPtr) && t != typeof(UIntPtr) ||
        t == typeof(string) || t == typeof(decimal) || t == typeof(Guid) || t == typeof(Uri) || t == typeof(DateTime) ||
        t == typeof(DateTimeOffset) || t == typeof(DateOnly) || t == typeof(TimeOnly) || t == typeof(TimeSpan) ||
        t == typeof(byte[]) || t == typeof(Encoding) || t == typeof(CultureInfo);
    public bool Nullable(ParameterInfo p) => p.HasDefaultValue && p.DefaultValue is null || System.Nullable.GetUnderlyingType(JsonSupport.ValueType(p.ParameterType)) is not null ||
        (!p.ParameterType.IsValueType && NullState(() => _nullability.Create(p).ReadState));
    private bool NullState(Func<NullabilityState> get) { lock (_nullability) { try { return get() == NullabilityState.Nullable; } catch { return false; } } }
    public IReadOnlyList<DataMember> Members(Type t) => t.GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod?.IsPublic == true && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
        .Select(p => new DataMember(p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name, p.PropertyType,
            System.Nullable.GetUnderlyingType(p.PropertyType) is not null || NullState(() => _nullability.Create(p).ReadState), p, null))
        .Concat(t.GetFields(BindingFlags.Instance | BindingFlags.Public).Where(f => f.GetCustomAttribute<JsonIgnoreAttribute>() is null)
        .Select(f => new DataMember(f.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? f.Name, f.FieldType,
            System.Nullable.GetUnderlyingType(f.FieldType) is not null || NullState(() => _nullability.Create(f).WriteState), null, f))).ToArray();
    public ConstructorInfo? DtoConstructor(Type type)
    {
        var constructors = type.GetConstructors();
        var marked = constructors.Where(c => c.GetCustomAttribute<JsonConstructorAttribute>() is not null).ToArray();
        if (marked.Length == 1) return marked[0];
        if (marked.Length > 1) return null;
        return type.GetConstructor(Type.EmptyTypes) ?? (constructors.Length == 1 ? constructors[0] : null);
    }
    public bool OptionalMember(Type type, DataMember member) => member.Nullable || DtoConstructor(type)?.GetParameters().Any(p => p.HasDefaultValue && p.Name?.Equals(member.ClrName, StringComparison.OrdinalIgnoreCase) == true) == true;
    public TypeShape Classify(Type type)
    {
        type = JsonSupport.ValueType(type);
        lock (_cache)
        {
            if (_demoted.TryGetValue(type, out string? why)) return new(type, ShapeKind.Handle, why);
            if (_cache.TryGetValue(type, out var shape)) return shape;
            return _cache[type] = Classify(type, new HashSet<Type>());
        }
    }
    private TypeShape Classify(Type t, HashSet<Type> path)
    {
        if (t.IsPointer || t.IsFunctionPointer || t.IsByRefLike || t == typeof(TypedReference) || t == typeof(ArgIterator) || t == typeof(RuntimeArgumentHandle) || t == typeof(IntPtr) || t == typeof(UIntPtr))
            return new(t, ShapeKind.Unsupported, "pointer_or_stack_only");
        if (t == typeof(void) || IsScalar(t)) return new(t, ShapeKind.Scalar);
        if (System.Nullable.GetUnderlyingType(t) is Type inner) return Classify(inner, path) with { Type = t };
        if (!path.Add(t)) return new(t, ShapeKind.Handle, "cyclic_type");
        try
        {
            if (t.IsArray && t.GetArrayRank() != 1) return new(t, ShapeKind.Handle, "multidimensional_array");
            Type? dict = Generic(t, typeof(IDictionary<,>)) ?? Generic(t, typeof(IReadOnlyDictionary<,>));
            Type? sequence = t.IsArray ? t : Generic(t, typeof(IEnumerable<>));
            if (dict is not null || sequence is not null)
            {
                var children = dict?.GetGenericArguments() ?? new[] { t.IsArray ? t.GetElementType()! : sequence!.GetGenericArguments()[0] };
                foreach (var child in children)
                {
                    var c = Classify(child, path);
                    if (c.Kind == ShapeKind.Unsupported || c.Reason == "cyclic_type") return c with { Type = t };
                }
                if (dict is not null && !IsScalar(children[0])) return new(t, ShapeKind.Handle, "non_scalar_dictionary_key");
                return new(t, dict is not null ? ShapeKind.Dictionary : ShapeKind.Sequence);
            }
            if (Generic(t, typeof(IAsyncEnumerable<>)) is not null) return new(t, ShapeKind.Handle, "async_sequence");
            if (typeof(Stream).IsAssignableFrom(t) || typeof(Delegate).IsAssignableFrom(t) || typeof(IDisposable).IsAssignableFrom(t) || typeof(IAsyncDisposable).IsAssignableFrom(t))
                return new(t, ShapeKind.Handle, "live_resource");
            if (t == typeof(object) || t.IsInterface || t.IsAbstract || t.IsGenericParameter) return new(t, ShapeKind.Polymorphic);
            if (t.ContainsGenericParameters) return new(t, ShapeKind.Handle, "open_generic_type");
            var members = Members(t);
            var ctor = DtoConstructor(t);
            var parameters = ctor?.GetParameters() ?? Array.Empty<ParameterInfo>();
            bool readOnlyData = members.Any(m => !m.CanWrite && !parameters.Any(p => p.Name?.Equals(m.ClrName, StringComparison.OrdinalIgnoreCase) == true && p.ParameterType == m.Type));
            bool unmatchedParameter = parameters.Any(p => !members.Any(m => p.Name?.Equals(m.ClrName, StringComparison.OrdinalIgnoreCase) == true && p.ParameterType == m.Type));
            if (readOnlyData || unmatchedParameter || members.Count == 0 || !t.IsValueType && ctor is null || members.Select(m => m.Name).Distinct().Count() != members.Count)
                return new(t, ShapeKind.Handle, "not_roundtrippable_dto");
            foreach (var member in members)
            {
                var child = Classify(member.Type, path);
                if (child.Kind == ShapeKind.Unsupported || child.Reason == "cyclic_type") return child with { Type = t };
            }
            return new(t, ShapeKind.Dto);
        }
        finally { path.Remove(t); }
    }
    public void Demote(Type t, string reason) { lock (_cache) { _demoted[t] = reason; _cache.Remove(t); } }
    public JsonNode? Sample(Type t, int depth = 0)
    {
        if (depth > 8) throw new InvalidOperationException("Sample depth exceeded");
        if (System.Nullable.GetUnderlyingType(t) is not null) return null;
        if (t == typeof(string)) return JsonValue.Create("sample");
        if (t == typeof(bool)) return JsonValue.Create(true);
        if (t == typeof(char)) return JsonValue.Create("x");
        if (t == typeof(byte[])) return JsonValue.Create("AQID");
        if (t == typeof(Encoding)) return JsonValue.Create("utf-8");
        if (t == typeof(CultureInfo)) return JsonValue.Create("en-US");
        if (t == typeof(Guid)) return JsonValue.Create("00000000-0000-0000-0000-000000000001");
        if (t == typeof(Uri)) return JsonValue.Create("urn:example:sample");
        if (t == typeof(DateOnly)) return JsonValue.Create("2000-01-02");
        if (t == typeof(TimeOnly)) return JsonValue.Create("03:04:05");
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return JsonValue.Create("2000-01-02T03:04:05Z");
        if (t == typeof(TimeSpan)) return JsonValue.Create("00:00:01");
        if (t.IsEnum) return JsonValue.Create(Enum.GetNames(t).FirstOrDefault() ?? "0");
        if (IsScalar(t)) return JsonValue.Create(1);
        var shape = Classify(t);
        if (shape.Kind == ShapeKind.Sequence) return new JsonArray();
        if (shape.Kind == ShapeKind.Dictionary) return new JsonObject();
        if (shape.Kind == ShapeKind.Dto)
        {
            var result = new JsonObject();
            foreach (var m in Members(t)) result[JsonSupport.Key(m.Name)] = m.Nullable ? null : Sample(m.Type, depth + 1);
            return result;
        }
        if (t == typeof(object)) return new JsonObject();
        throw new InvalidOperationException($"{t} requires a live value");
    }
}
