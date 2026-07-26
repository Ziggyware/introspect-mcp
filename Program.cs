using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;


public static class McpToolIntrospector
{
    public static async Task<int> Main(string[] args)
    {
        var dllPath = args.Length > 0 ? args[0] : throw new Exception("dll argument path missing");

        RegisterDependencyResolution(dllPath);
        List<McpServerTool> tools;
        try
        {
            tools = BuildInvocableToolsFromDll(dllPath).ToList();
            Console.Error.WriteLine($"[info] Registered {tools.Count} tools from '{dllPath}'");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load tools from '{dllPath}': {ex}");
            return 1;
        }

        var b = Host.CreateEmptyApplicationBuilder(settings: null);
        b.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        b.Services.AddMcpServer().WithStdioServerTransport().WithTools(tools);

        await b.Build().RunAsync();
        return 0;
    }

    private static void RegisterDependencyResolution(string dllPath)
    {
        var resolver = new AssemblyDependencyResolver(dllPath);
        var dllDir = Path.GetDirectoryName(dllPath)!;

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            // Primary: deps.json-driven — correct for version selection and transitive deps.
            var resolved = resolver.ResolveAssemblyToPath(name);
            if (resolved != null)
                return ctx.LoadFromAssemblyPath(resolved);

            // Fallback: not declared in deps.json (stale manifest, excluded package,
            // or a reference MSBuild didn't flow through) — if a same-named DLL sits
            // next to file.dll, load it directly. No version check: whatever's on
            // disk is assumed to be what file.dll was actually built/copied against.
            var probePath = Path.Combine(dllDir, name.Name + ".dll");
            if (File.Exists(probePath))
            {
                Console.Error.WriteLine($"[resolve-fallback] '{name.Name}' missing from file.dll.deps.json — loading directly from '{probePath}'");
                return ctx.LoadFromAssemblyPath(probePath);
            }

            return null;
        };

        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (assembly, unmanagedName) =>
        {
            var path = resolver.ResolveUnmanagedDllToPath(unmanagedName);
            if (path != null) return NativeLibrary.Load(path);

            var direct = Path.Combine(dllDir, unmanagedName);
            if (File.Exists(direct)) return NativeLibrary.Load(direct);

            var withExt = direct + ".dll";
            return File.Exists(withExt) ? NativeLibrary.Load(withExt) : IntPtr.Zero;
        };
    }
    public static IEnumerable<McpServerTool> BuildInvocableToolsFromDll(string dllPath)
    {
        if (!File.Exists(dllPath))
            throw new FileNotFoundException(
                $"assembly not found at '{dllPath}'. Pass a valid path as arg[0]", dllPath);

        return BuildInvocableToolsFromAssembly(Assembly.LoadFrom(dllPath));
    }
    // Testable entry point: no filesystem/path dependency, so tests can point this at
    // Assembly.GetExecutingAssembly() with a local *Util fixture instead of file.dll.
    public static IEnumerable<McpServerTool> BuildInvocableToolsFromAssembly(Assembly asm)
    {
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

        var utilTypes = types.Where(t =>
            t.IsClass && t.IsPublic && !t.IsGenericTypeDefinition && t.Name.EndsWith("Util", StringComparison.Ordinal));
        var candidates = utilTypes
            .SelectMany<Type, (Type, MethodInfo)>(t =>
            {
                MethodInfo[] methods;
                try { methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[skip-type] {t.FullName}: {ex.GetType().Name}: {ex.Message}");
                    return Enumerable.Empty<(Type, MethodInfo)>();
                }

                return methods
                    .Where(m => !m.IsSpecialName && !m.IsGenericMethod)
                    .Select(m => (Type: t, Method: m))
                    .Where(tm =>
                    {
                        try { _ = tm.Method.GetParameters(); return true; }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[skip] {t.Name}.{tm.Method.Name}: signature references unresolvable type — {ex.GetType().Name}: {ex.Message}");
                            return false;
                        }
                    }).ToList();
            })
            .OrderBy(tm => tm.Item1.Name, StringComparer.Ordinal)
            .ThenBy(tm => tm.Item2.Name, StringComparer.Ordinal)
            .ThenBy(tm => tm.Item2.GetParameters().Length)
            .ToList();

        var names = ToolNaming.AssignNames(candidates);
        var results = new List<McpServerTool>();

        foreach (var (type, method) in candidates)
        {
            var toolName = names[method];

            if (UnrepresentableTypeRegistry.MethodIsUnrepresentable(method))
            {
                Console.Error.WriteLine($"[skip] {toolName}: return or parameter type is an opaque resource handle, cannot cross MCP boundary");
                continue;
            }

            try
            {
                var options = new McpServerToolCreateOptions
                {
                    Name = toolName,
                    Description = $"{type.FullName} {method.Name}"
                };

                if (AdaptationDecision.NeedsAdaptation(method))
                {
                    var mthd = AdapterBuilder.BuildCallableDelegate(method);
                    results.Add(McpServerTool.Create(mthd, options));
                }
                else
                {
                    results.Add(McpServerTool.Create(method, target: null, options: options));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[skip] {toolName}: {ex.GetType().Name}: {ex.Message}");
            }

        }
        return results;
    }
}