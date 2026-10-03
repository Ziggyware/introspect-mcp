using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntrospectMcp.Extended;

public static class ExtendedHost
{
    public const string Help = """
introspectMCP — managed DLL tools over MCP stdio

  introspectMCP /absolute/path/library.dll                 Legacy *Util tools
  introspectMCP /absolute/path/library.dll --extended      Rich binding + live objects
  introspectMCP /absolute/path/library.dll --extended --inspect

Extended options:
  --tool-budget N        Maximum individually advertised methods (default 100)
  --schema-budget N      Approximate schema token budget (default 24000)
  --page-size N          Sequence page size, 1..200 (default 40)
  --max-handles N        Session live-object limit, 4..10000 (default 256)
  --handle-ttl N         Idle lifetime in seconds (default 900)
  --allow GLOB           Allow matching fully qualified member names; repeatable
  --deny GLOB            Deny matching members; repeatable, deny always wins
  --file-root PATH       Permit read-only Stream path inputs under this folder
  --inspect             Print coverage JSON and exit (no MCP session)
  --help                Show this help

Load trusted managed DLLs only. Reflection is NOT a security sandbox.
The existing stdio transport is unchanged; diagnostics go to stderr.
""";
    public static async Task<int> RunAsync(string[] args)
    {
        TargetLoadContext? loader = null;
        var originalOutput = Console.Out;
        try
        {
            var options = new ServiceOptions(); string? file = null; bool inspect = false;
            for (int i = 0; i < args.Length; i++)
            {
                string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"Missing value for {args[i - 1]}.");
                switch (args[i])
                {
                    case "--extended": break;
                    case "--inspect": inspect = true; break;
                    case "--tool-budget": options.ToolBudget = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--schema-budget": options.SchemaTokenBudget = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--page-size": options.PageSize = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--max-handles": options.MaxHandles = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--handle-ttl": options.HandleTtl = TimeSpan.FromSeconds(double.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture)); break;
                    case "--allow": options.Allow.Add(Value()); break;
                    case "--deny": options.Deny.Add(Value()); break;
                    case "--file-root": options.FileRoot = Path.GetFullPath(Value()); break;
                    default:
                        if (args[i].StartsWith('-') || file is not null) throw new ArgumentException($"Unexpected argument: {args[i]}. Use --help.");
                        file = Path.GetFullPath(args[i]); break;
                }
            }
            options.Validate();
            if (file is null || !File.Exists(file)) throw new FileNotFoundException("Pass the path to an existing managed DLL. Use --help.", file);
            _ = AssemblyName.GetAssemblyName(file);
            loader = new TargetLoadContext(file);
            // Redirect before load-time DTO checks; the SDK uses OpenStandardOutput for JSON-RPC.
            Console.SetOut(Console.Error);
            await using var engine = new InvocationEngine(loader.LoadFromAssemblyPath(file), options);
            if (inspect) { originalOutput.WriteLine(engine.Coverage().ToJsonString(new JsonSerializerOptions { WriteIndented = true })); return 0; }
            var builder = Host.CreateEmptyApplicationBuilder(null);
            builder.Logging.AddConsole(c => c.LogToStandardErrorThreshold = LogLevel.Trace);
            var tools = engine.Tools();
            Console.Error.WriteLine($"[extended] {tools.Count} advertised tools. {engine.Coverage().ToJsonString()}");
            builder.Services.AddMcpServer().WithStdioServerTransport().WithTools(tools);
            using var host = builder.Build();
            await host.RunAsync();
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { isError = true, error = new { code = "startup_failed", message = JsonSupport.Unwrap(e).Message, hint = "Use --help. Extended mode loads trusted managed DLLs only." } }));
            return 2;
        }
        finally { Console.SetOut(originalOutput); loader?.Unload(); }
    }
}
