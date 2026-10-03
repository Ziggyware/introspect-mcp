using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace IntrospectMcp.Extended;

public sealed class TargetLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _directory;
    public TargetLoadContext(string path) : base($"introspect-{Guid.NewGuid():N}", isCollectible: true)
        => (_resolver, _directory) = (new AssemblyDependencyResolver(path), Path.GetDirectoryName(path)!);
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name is not null && (name.Name.StartsWith("System.", StringComparison.Ordinal) || name.Name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) || name.Name.StartsWith("ModelContextProtocol", StringComparison.Ordinal)))
        {
            var shared = Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
            if (shared is not null) return shared;
        }
        string? path = _resolver.ResolveAssemblyToPath(name);
        if (path is null && name.Name is not null && !name.Name.Contains(Path.DirectorySeparatorChar))
        {
            string probe = Path.Combine(_directory, name.Name + ".dll");
            if (File.Exists(probe)) path = probe;
        }
        return path is null ? null : LoadFromAssemblyPath(path);
    }
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) => throw new NotSupportedException("Extended mode does not load native libraries.");
}

public sealed record MemberDescriptor(string Name, MethodBase Method, string Signature, string Description, string? Unsupported)
{
    public Type ReturnType => Method is MethodInfo m ? TypeModel.UnwrapReturn(m.ReturnType) : Method.DeclaringType!;
    public bool NeedsTarget => Method is MethodInfo { IsStatic: false };
}

public sealed class AssemblyCatalog
{
    private readonly ServiceOptions _options;
    public Assembly Assembly { get; }
    public Type[] Types { get; }
    public List<string> LoadDiagnostics { get; } = new();
    public IReadOnlyList<MemberDescriptor> Members { get; }
    private readonly Dictionary<string, MemberDescriptor> _byName;
    private static readonly Dictionary<string, Type> Aliases = new(StringComparer.Ordinal)
    {
        ["int"] = typeof(int), ["long"] = typeof(long), ["short"] = typeof(short), ["byte"] = typeof(byte), ["bool"] = typeof(bool),
        ["string"] = typeof(string), ["double"] = typeof(double), ["float"] = typeof(float), ["decimal"] = typeof(decimal), ["object"] = typeof(object),
        ["Guid"] = typeof(Guid), ["DateTime"] = typeof(DateTime), ["DateTimeOffset"] = typeof(DateTimeOffset), ["char"] = typeof(char), ["uint"] = typeof(uint),
    };
    private static readonly string[] Dangerous = { "System.Diagnostics.Process*", "System.Environment*", "System.Reflection*", "System.Runtime.InteropServices*", "System.IO.File*", "System.IO.Directory*", "System.Net*", "Microsoft.Win32*" };
    public AssemblyCatalog(Assembly assembly, TypeModel model, ServiceOptions options)
    {
        (Assembly, _options) = (assembly, options);
        try { Types = assembly.GetExportedTypes(); }
        catch (ReflectionTypeLoadException e) { Types = e.Types.Where(t => t?.IsVisible == true).Cast<Type>().ToArray(); LoadDiagnostics.AddRange(e.LoaderExceptions.Where(x => x is not null).Select(x => x!.Message)); }
        var docs = ReadDocs(Path.ChangeExtension(assembly.Location, ".xml"));
        var methods = new List<MethodBase>();
        foreach (var type in Types.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            try { methods.AddRange(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)); methods.AddRange(type.GetConstructors()); }
            catch (Exception e) { LoadDiagnostics.Add($"{type.FullName}: {e.Message}"); }
        }
        var names = methods.GroupBy(m => BaseName(m)).ToDictionary(g => g.Key, g => g.Count());
        Members = methods.Select(m =>
        {
            string signature = Signature(m);
            string name = BaseName(m);
            if (names[name] > 1 || name.Length > 100) name = name[..Math.Min(name.Length, 100)] + "__" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..10].ToLowerInvariant();
            string? reason = null;
            if (!Permitted(m.DeclaringType!.FullName + "." + m.Name)) reason = "policy_denied";
            else if (m is ConstructorInfo && m.DeclaringType.IsAbstract) reason = "abstract_constructor";
            else if (m.Name is "Dispose" or "DisposeAsync" && m.GetParameters().Length == 0 && (typeof(IDisposable).IsAssignableFrom(m.DeclaringType) || typeof(IAsyncDisposable).IsAssignableFrom(m.DeclaringType))) reason = "lifecycle_use_release_handle";
            else if (m.GetParameters().Any(p => (p.Name == "target" && m is MethodInfo { IsStatic: false }) || (p.Name == "typeArgs" && m.IsGenericMethod))) reason = "reserved_parameter_name";
            else if (m.DeclaringType.ContainsGenericParameters) reason = "open_generic_declaring_type";
            else if (m.IsSpecialName && m is not ConstructorInfo) reason = "special_member_use_projection";
            else if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0) reason = "native_entry_point";
            else if (m is MethodInfo { ReturnType.IsByRef: true }) reason = "byref_return";
            else
            {
                var shapes = m.GetParameters().Where(p => !CallScope.IsInjected(p.ParameterType)).Select(p => model.Classify(p.ParameterType))
                    .Append(model.Classify(m is MethodInfo mi ? TypeModel.UnwrapReturn(mi.ReturnType) : m.DeclaringType!));
                reason = shapes.FirstOrDefault(s => s.Kind == ShapeKind.Unsupported)?.Reason;
            }
            docs.TryGetValue(XmlId(m), out string? documentation);
            return new MemberDescriptor(name, m, signature, documentation is null ? signature : documentation[..Math.Min(documentation.Length, 2048)], reason);
        }).OrderBy(m => m.Name, StringComparer.Ordinal).ToArray();
        _byName = Members.ToDictionary(m => m.Name, StringComparer.Ordinal);
    }
    public bool Permitted(string name) => !Dangerous.Concat(_options.Deny).Any(p => Glob(p, name)) && (_options.Allow.Count == 0 || _options.Allow.Any(p => Glob(p, name)));
    private static bool Glob(string pattern, string value) => Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static string BaseName(MethodBase m) => Regex.Replace((m is ConstructorInfo ? "new_" + m.DeclaringType!.Name : m.DeclaringType!.Name + "_" + m.Name), "[^A-Za-z0-9_.-]", "_");
    private static string Signature(MethodBase m) => $"{(m is MethodInfo method ? JsonSupport.TypeName(method.ReturnType) : JsonSupport.TypeName(m.DeclaringType!))} {m.DeclaringType!.FullName}.{m.Name}{(m.IsGenericMethod ? "<" + string.Join(",", m.GetGenericArguments().Select(x => x.Name)) + ">" : "")}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType + " " + p.Name))})";
    public MemberDescriptor Find(string name) => _byName.TryGetValue(name, out var member) ? member : throw new BindingException("unknown_method", $"Unknown method '{name}'. Use describe_type to obtain an exact method/tool name.", "method");
    public Type ResolveType(string name)
    {
        if (name.Length > 2048) throw new BindingException("unknown_type", "Type name is too long.", "type");
        if (Aliases.TryGetValue(name, out var alias)) return alias;
        int separator = name.IndexOf(',');
        if (separator > 0 && !name.Contains('['))
        {
            string typeName = name[..separator].Trim(), assemblyName = name[(separator + 1)..].Split(',')[0].Trim();
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assemblyName);
            return assembly?.GetType(typeName, false, false) ?? throw new BindingException("unknown_type", "Assembly-qualified types must already be loaded.", "type");
        }
        if (name.EndsWith("[]", StringComparison.Ordinal)) return ResolveType(name[..^2]).MakeArrayType();
        if (name.EndsWith('?')) return typeof(Nullable<>).MakeGenericType(ResolveType(name[..^1]));
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).Prepend(Assembly).Distinct();
        if (name.Contains('['))
        {
            try
            {
                var resolved = Type.GetType(name,
                    requested => assemblies.FirstOrDefault(a => a.GetName().Name == requested.Name),
                    (assembly, typeName, ignoreCase) => assembly?.GetType(typeName, false, ignoreCase) ?? assemblies.Select(a => a.GetType(typeName, false, ignoreCase)).FirstOrDefault(t => t is not null),
                    throwOnError: false);
                if (resolved is not null) return resolved;
            }
            catch (Exception e) when (e is ArgumentException or TypeLoadException or FileNotFoundException) { throw new BindingException("unknown_type", e.Message, "type"); }
        }
        var matches = assemblies.Select(a => a.GetType(name, false, false)).Where(t => t is not null).Cast<Type>()
            .Concat(Types.Where(t => t.Name == name)).Distinct().ToArray();
        if (matches.Length != 1) throw new BindingException("unknown_type", $"Type '{name}' is unknown or ambiguous; use its namespace-qualified name from list_types.", "type", new() { ["matches"] = JsonSupport.Node(matches.Select(JsonSupport.TypeName)) });
        return matches[0];
    }
    public Type[] Implementations(Type target) => Types.Where(t => !t.IsAbstract && !t.IsInterface && !t.ContainsGenericParameters && target.IsAssignableFrom(t)).ToArray();
    public MethodBase Close(MemberDescriptor member, JsonArray? typeArgs)
    {
        if (member.Method is not MethodInfo { IsGenericMethodDefinition: true } method)
        {
            if (typeArgs is { Count: > 0 }) throw new BindingException("unexpected_type_args", "This member is not generic.", "typeArgs");
            return member.Method;
        }
        var parameters = method.GetGenericArguments();
        if (typeArgs is null || typeArgs.Count != parameters.Length) throw new BindingException("type_args_required", $"Provide {parameters.Length} typeArgs: {string.Join(", ", parameters.Select(p => p.Name))}.", "typeArgs");
        var types = typeArgs.Select(n => ResolveType(n?.GetValue<string>() ?? "")).ToArray();
        Type Substitute(Type c) => c.IsGenericParameter ? types[Array.IndexOf(parameters, c)] : c.IsArray ? Substitute(c.GetElementType()!).MakeArrayType() : c.IsGenericType ? c.GetGenericTypeDefinition().MakeGenericType(c.GetGenericArguments().Select(Substitute).ToArray()) : c;
        for (int i = 0; i < types.Length; i++)
        {
            var t = types[i]; var p = parameters[i]; var flags = p.GenericParameterAttributes;
            bool bad = t.ContainsGenericParameters || t.IsPointer || t.IsByRefLike || t == typeof(void) ||
                flags.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint) && t.IsValueType ||
                flags.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint) && (!t.IsValueType || Nullable.GetUnderlyingType(t) is not null) ||
                flags.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint) && !t.IsValueType && (t.IsAbstract || t.GetConstructor(Type.EmptyTypes) is null) ||
                p.GetGenericParameterConstraints().Any(c => !Substitute(c).IsAssignableFrom(t));
            if (p.GetCustomAttributesData().Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.IsUnmanagedAttribute") && !Unmanaged(t, new HashSet<Type>())) bad = true;
            if (bad) throw new BindingException("generic_constraint", $"{JsonSupport.TypeName(t)} violates constraints for {p.Name}.", $"typeArgs[{i}]", new() { ["constraints"] = JsonSupport.Node(p.GetGenericParameterConstraints().Select(JsonSupport.TypeName)) });
        }
        return method.MakeGenericMethod(types);
    }
    private static bool Unmanaged(Type type, HashSet<Type> seen)
    {
        if (type.IsPrimitive || type.IsEnum || type.IsPointer) return true;
        if (!type.IsValueType || Nullable.GetUnderlyingType(type) is not null) return false;
        if (!seen.Add(type)) return true;
        return type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).All(f => Unmanaged(f.FieldType, seen));
    }
    private static string XmlId(MethodBase m) => (m is ConstructorInfo ? "M:" + m.DeclaringType!.FullName + ".#ctor" : "M:" + m.DeclaringType!.FullName + "." + m.Name + (m.IsGenericMethod ? "``" + m.GetGenericArguments().Length : "")) +
        (m.GetParameters().Length == 0 ? "" : "(" + string.Join(",", m.GetParameters().Select(p => (p.ParameterType.FullName ?? p.ParameterType.Name).Replace('&', '@'))) + ")");
    private Dictionary<string, string> ReadDocs(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
            return XDocument.Load(reader).Descendants("member").Where(x => x.Attribute("name") is not null).ToDictionary(x => x.Attribute("name")!.Value, x => Regex.Replace(x.Element("summary")?.Value.Trim() ?? "", "\\s+", " "));
        }
        catch (Exception e) { LoadDiagnostics.Add("XML documentation: " + e.Message); return new(); }
    }
}
