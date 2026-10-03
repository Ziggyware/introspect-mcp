using System.Text;
using System.Globalization;

namespace IntrospectMcp.Extended;

public sealed class Schemas
{
    private readonly TypeModel _model;
    private readonly AssemblyCatalog _catalog;
    public Schemas(TypeModel model, AssemblyCatalog catalog) => (_model, _catalog) = (model, catalog);
    public JsonObject For(Type type, bool nullable = false, int depth = 0)
    {
        type = JsonSupport.ValueType(type);
        Type effective = Nullable.GetUnderlyingType(type) ?? type;
        var alternatives = new JsonArray();
        var shape = _model.Classify(effective);
        if (shape.Kind == ShapeKind.Unsupported) return new JsonObject { ["not"] = new JsonObject(), ["x-clr-type"] = JsonSupport.TypeName(type), ["x-unsupported-reason"] = shape.Reason };
        if (nullable || Nullable.GetUnderlyingType(type) is not null) alternatives.Add(new JsonObject { ["type"] = "null" });
        JsonObject? literal = null;
        if (depth < 8)
        {
            if (effective.IsEnum) literal = new() { ["oneOf"] = new JsonArray(new JsonObject { ["type"] = "string", ["description"] = "Enum names (flags may be comma separated): " + string.Join(", ", Enum.GetNames(effective)) }, new JsonObject { ["type"] = "integer" }) };
            else if (effective == typeof(bool)) literal = new() { ["type"] = "boolean" };
            else if (effective == typeof(float) || effective == typeof(double) || effective == typeof(decimal)) literal = new() { ["type"] = "number" };
            else if (effective.IsPrimitive && effective != typeof(char)) literal = new() { ["type"] = "integer" };
            else if (TypeModel.IsScalar(effective))
            {
                literal = new() { ["type"] = "string" };
                if (effective == typeof(byte[])) literal["contentEncoding"] = "base64";
                if (effective == typeof(char)) { literal["minLength"] = 1; literal["maxLength"] = 1; }
            }
            else if (shape.Kind == ShapeKind.Sequence)
            {
                var element = effective.IsArray ? effective.GetElementType()! : TypeModel.Generic(effective, typeof(IEnumerable<>))!.GetGenericArguments()[0];
                literal = new() { ["type"] = "array", ["items"] = For(element, !element.IsValueType, depth + 1), ["maxItems"] = 10000 };
            }
            else if (shape.Kind == ShapeKind.Dictionary)
            {
                var element = (TypeModel.Generic(effective, typeof(IDictionary<,>)) ?? TypeModel.Generic(effective, typeof(IReadOnlyDictionary<,>)))!.GetGenericArguments()[1];
                literal = new() { ["type"] = "object", ["additionalProperties"] = For(element, !element.IsValueType, depth + 1), ["not"] = Reserved() };
            }
            else if (shape.Kind == ShapeKind.Dto)
            {
                var members = _model.Members(effective);
                literal = JsonSupport.ObjectSchema(new JsonObject(members.Select(m => KeyValuePair.Create<string, JsonNode?>(JsonSupport.Key(m.Name), For(m.Type, m.Nullable, depth + 1)))),
                    members.Where(m => !_model.OptionalMember(effective, m)).Select(m => JsonSupport.Key(m.Name)).ToArray());
            }
            else if (effective == typeof(object) || effective.IsGenericParameter)
                literal = new() { ["not"] = new JsonObject { ["anyOf"] = new JsonArray(Reserved(), new JsonObject { ["type"] = "null" }) }, ["description"] = "Ordinary JSON becomes CLR dictionaries, lists and scalar values, not JsonElement. Use $type/$value for a specific scalar type." };
            if (typeof(Stream).IsAssignableFrom(effective))
            {
                alternatives.Add(JsonSupport.ObjectSchema(new() { ["base64"] = new JsonObject { ["type"] = "string", ["contentEncoding"] = "base64" } }, "base64"));
                alternatives.Add(JsonSupport.ObjectSchema(new() { ["path"] = JsonSupport.StringSchema("Read-only path beneath --file-root (disabled without that option).") }, "path"));
            }
        }
        if (literal is not null) alternatives.Add(literal);
        alternatives.Add(Handle());
        alternatives.Add(JsonSupport.ObjectSchema(new() { ["$ref"] = JsonSupport.StringSchema("Handle ID"), ["path"] = JsonSupport.StringSchema("Property/field/index path; never method calls.") }, "$ref"));
        alternatives.Add(JsonSupport.ObjectSchema(new() { ["$step"] = JsonSupport.StringSchema("Earlier pipeline step ID"), ["path"] = JsonSupport.StringSchema("Optional property/index projection"), ["output"] = JsonSupport.StringSchema("Optional ref/out parameter name") }, "$step"));
        if (!effective.IsSealed || effective.IsInterface || effective.IsAbstract || effective == typeof(object))
        {
            var choices = _catalog.Implementations(effective).Where(t => _model.Classify(t).Literal).Take(8).Select(JsonSupport.TypeName).ToArray();
            alternatives.Add(new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["$type"] = JsonSupport.StringSchema("Assignable concrete CLR type. Examples: " + string.Join(", ", choices)) }, ["required"] = JsonSupport.Node(new[] { "$type" }), ["description"] = "Use find_implementations for a complete paginated list; remaining keys are the concrete DTO's fields, or $value for a scalar." });
        }
        if (typeof(Delegate).IsAssignableFrom(effective))
            alternatives.Add(JsonSupport.ObjectSchema(new() { ["$method"] = JsonSupport.StringSchema("Exact compatible method name from describe_type"), ["target"] = Handle(), ["typeArgs"] = new JsonObject { ["type"] = "array", ["items"] = JsonSupport.StringSchema() } }, "$method"));
        return new() { ["oneOf"] = alternatives, ["x-clr-type"] = JsonSupport.TypeName(type), ["x-shape"] = shape.Kind.ToString().ToLowerInvariant(),
            ["description"] = $"CLR {type}. {(shape.Literal ? "Literal JSON or " : "Requires ")}an assignable {{$handle:id}}; references work recursively. {shape.Reason}" };
    }
    private static JsonObject Reserved() => new() { ["anyOf"] = new JsonArray(new[] { "$handle", "$ref", "$step", "$type", "$method", "$expr" }.Select(k => (JsonNode)new JsonObject { ["type"] = "object", ["required"] = JsonSupport.Node(new[] { k }) }).ToArray()) };
    public static JsonObject Handle() => JsonSupport.ObjectSchema(new() { ["$handle"] = JsonSupport.StringSchema("Session-scoped live object ID") }, "$handle");
    public JsonObject Inputs(MemberDescriptor member)
    {
        var properties = new JsonObject(); var required = new List<string>();
        if (member.NeedsTarget) { properties["target"] = For(member.Method.DeclaringType!); required.Add("target"); }
        if (member.Method.IsGenericMethodDefinition)
        {
            properties["typeArgs"] = new JsonObject { ["type"] = "array", ["items"] = JsonSupport.StringSchema("Alias or loaded CLR type name"), ["minItems"] = member.Method.GetGenericArguments().Length, ["maxItems"] = member.Method.GetGenericArguments().Length };
            required.Add("typeArgs");
        }
        foreach (var p in member.Method.GetParameters())
        {
            if (p.IsOut && !p.IsIn || CallScope.IsInjected(p.ParameterType)) continue;
            var schema = For(p.ParameterType, _model.Nullable(p));
            if (p.HasDefaultValue && p.DefaultValue is not DBNull && p.DefaultValue != Missing.Value) schema["default"] = JsonSupport.Node(p.DefaultValue);
            schema["x-direction"] = p.ParameterType.IsByRef ? p.IsIn ? "in" : "ref" : "in";
            properties[JsonSupport.ParameterName(p)] = schema;
            if (!p.HasDefaultValue && !_model.Nullable(p)) required.Add(JsonSupport.ParameterName(p));
        }
        return JsonSupport.ObjectSchema(properties, required.ToArray());
    }
}
