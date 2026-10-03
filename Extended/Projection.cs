using System.Collections;
using System.Text.RegularExpressions;

namespace IntrospectMcp.Extended;

public static class Projection
{
    private sealed record Segment(string? Member, object? Index);
    private static BindingException Invalid() => new("invalid_projection", "Only public property/field names and [nonnegativeInteger]/[\"key\"] indices are allowed. Method calls are forbidden.", "path");
    private static IEnumerable<Segment> Parse(string path)
    {
        if (path.Length > 1024) throw new BindingException("invalid_projection", "Projection is too long.", "path");
        int offset = 0, count = 0;
        while (offset < path.Length)
        {
            if (++count > 32) throw new BindingException("invalid_projection", "Too many projection segments.", "path");
            if (offset > 0 && path[offset] == '.') { offset++; if (offset == path.Length) throw Invalid(); }
            if (path[offset] == '[')
            {
                int close = offset + 1;
                bool quoted = false, escaped = false;
                for (; close < path.Length; close++)
                {
                    char c = path[close];
                    if (escaped) { escaped = false; continue; }
                    if (quoted && c == '\\') { escaped = true; continue; }
                    if (c == '"') quoted = !quoted;
                    else if (!quoted && c == ']') break;
                }
                if (close == path.Length || quoted) throw Invalid();
                string content = path[(offset + 1)..close]; object index;
                if (int.TryParse(content, out int n) && n >= 0) index = n;
                else if (content.StartsWith('"'))
                {
                    try { index = JsonSerializer.Deserialize<string>(content) ?? throw Invalid(); }
                    catch (JsonException) { throw Invalid(); }
                }
                else throw Invalid();
                yield return new(null, index); offset = close + 1;
            }
            else
            {
                var match = Regex.Match(path[offset..], "^[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                if (!match.Success) throw Invalid();
                yield return new(match.Value, null); offset += match.Length;
            }
            if (offset < path.Length && path[offset] is not '.' and not '[') throw Invalid();
        }
    }
    private static PropertyInfo? Indexer(Type type, object? index) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .FirstOrDefault(p => p.GetMethod?.IsPublic == true && p.GetIndexParameters().Length == 1 && p.GetIndexParameters()[0].ParameterType.IsInstanceOfType(index));
    public static object? Read(object? value, string path, bool dryRun = false)
    {
        // Validate the entire path before touching any user property getter.
        foreach (var segment in Parse(path).ToArray())
        {
            Type? type = value is SymbolicValue symbol ? symbol.Type : value?.GetType();
            if (type is null) throw new BindingException("null_projection", "Projection reached null before the end of its path.", path);
            if (segment.Member is string name)
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property?.GetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0)
                    value = dryRun ? new SymbolicValue(property.PropertyType) : property.GetValue(value);
                else if (type.GetField(name, BindingFlags.Public | BindingFlags.Instance) is FieldInfo field)
                    value = dryRun ? new SymbolicValue(field.FieldType) : field.GetValue(value);
                else throw new BindingException("unknown_projection", $"{type} has no readable public field/property '{name}'.", path);
            }
            else
            {
                var dictionary = TypeModel.Generic(type, typeof(IDictionary<,>)) ?? TypeModel.Generic(type, typeof(IReadOnlyDictionary<,>));
                if (dictionary is not null && !dictionary.GetGenericArguments()[0].IsInstanceOfType(segment.Index))
                    throw new BindingException("invalid_indexer", $"{type} requires a {dictionary.GetGenericArguments()[0]} key.", path);
                if (dryRun)
                {
                    Type? element = dictionary?.GetGenericArguments()[1];
                    if (element is null && segment.Index is int)
                        element = type.IsArray ? type.GetElementType() : TypeModel.Generic(type, typeof(IList<>))?.GetGenericArguments()[0] ?? TypeModel.Generic(type, typeof(IReadOnlyList<>))?.GetGenericArguments()[0];
                    if (element is null && (typeof(IDictionary).IsAssignableFrom(type) || segment.Index is int && typeof(IList).IsAssignableFrom(type))) element = typeof(object);
                    element ??= Indexer(type, segment.Index)?.PropertyType;
                    if (element is null) throw new BindingException("invalid_indexer", $"{type} has no compatible public indexer.", path);
                    value = new SymbolicValue(element); continue;
                }
                if (value is IList list && segment.Index is int i)
                {
                    if (i >= list.Count) throw new BindingException("index_out_of_range", "Index exceeds the list size.", path);
                    value = list[i];
                }
                else if (value is IDictionary map)
                {
                    if (!map.Contains(segment.Index!)) throw new BindingException("missing_key", "Dictionary does not contain that key.", path);
                    value = map[segment.Index!];
                }
                else
                {
                    var indexer = Indexer(type, segment.Index);
                    if (indexer is null) throw new BindingException("invalid_indexer", "No compatible public indexer.", path);
                    value = indexer.GetValue(value, new[] { segment.Index });
                }
            }
        }
        return value;
    }
}
