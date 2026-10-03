global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Reflection;
global using System.Threading;
global using System.Threading.Tasks;
global using System.Text.Json;
global using System.Text.Json.Nodes;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntrospectMcp.Extended;

public sealed class ServiceOptions
{
    public int ToolBudget { get; set; } = 100;
    public int SchemaTokenBudget { get; set; } = 24000;
    public int PageSize { get; set; } = 40;
    public int MaxHandles { get; set; } = 256;
    public TimeSpan HandleTtl { get; set; } = TimeSpan.FromMinutes(15);
    public int MaxDepth { get; set; } = 24;
    public int MaxItems { get; set; } = 10000;
    public string? FileRoot { get; set; }
    public List<string> Allow { get; } = new();
    public List<string> Deny { get; } = new();
    public void Validate()
    {
        if (ToolBudget < 0 || SchemaTokenBudget < 1 || PageSize is < 1 or > 200 || MaxHandles is < 4 or > 10000 || HandleTtl <= TimeSpan.Zero)
            throw new ArgumentException("Budgets must be positive; page-size 1..200, max-handles 4..10000, TTL > 0 seconds.");
    }
}

public sealed class BindingException : Exception
{
    public string Code { get; }
    public string? Path { get; }
    public JsonObject Details { get; }
    public BindingException(string code, string message, string? path = null, JsonObject? details = null) : base(message)
        => (Code, Path, Details) = (code, path, details ?? new());
}

public static class JsonSupport
{
    public static JsonNode? Node(object? value) => JsonSerializer.SerializeToNode(value);
    public static string TypeName(Type t) => t.FullName ?? t.Name;
    public static string ParameterName(ParameterInfo p) => p.Name ?? "arg" + p.Position;
    public static Type ValueType(Type t) => t.IsByRef ? t.GetElementType()! : t;
    public static string Key(string name) => name.StartsWith('$') ? "$" + name : name;
    public static string RequiredString(JsonObject args, string name) => args[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
        ? s : throw new BindingException("missing_argument", $"'{name}' must be a nonempty string.", name);
    public static JsonObject Object(JsonNode? node, string path) => node as JsonObject ?? throw new BindingException("invalid_argument", "Expected a JSON object.", path);
    public static int Integer(JsonObject args, string name, int fallback, int min, int max)
    {
        if (!args.ContainsKey(name)) return fallback;
        if (args[name] is JsonValue v && v.TryGetValue<int>(out int i) && i >= min && i <= max) return i;
        throw new BindingException("invalid_argument", $"'{name}' must be an integer from {min} to {max}.", name);
    }
    public static JsonObject ObjectSchema(JsonObject properties, params string[] required) => new()
    { ["type"] = "object", ["properties"] = properties, ["required"] = Node(required), ["additionalProperties"] = false };
    public static JsonObject StringSchema(string description = "") => new() { ["type"] = "string", ["description"] = description };
    public static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException { InnerException: not null } or AggregateException { InnerExceptions.Count: 1 })
            ex = ex is AggregateException a ? a.InnerExceptions[0] : ex.InnerException!;
        return ex;
    }
}

public sealed class CallScope : IAsyncDisposable
{
    private static long _next;
    public string Id { get; } = $"call_{Interlocked.Increment(ref _next)}_{Guid.NewGuid():N}";
    public CancellationToken Cancellation { get; }
    public ConcurrentQueue<JsonNode?> Progress { get; } = new();
    public List<object> Owned { get; } = new();
    public Dictionary<string, object?> LiveOutputs { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, object?>> StepOutputs { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?> Steps { get; } = new(StringComparer.Ordinal);
    public Func<JsonObject, Type, string, object?>? ResolveReference { get; set; }
    public Func<object, Type, JsonNode>? ExportHandle { get; set; }
    public bool DryRun { get; set; }
    public object? LiveResult { get; set; }
    public CallScope? RetainIn { get; set; }
    public Dictionary<string, HandleLease> Handles { get; } = new(StringComparer.Ordinal);
    public HashSet<object> ActiveSequences { get; } = new(ReferenceEqualityComparer.Instance);
    public Func<object, Type, int, JsonNode>? ExportSequence { get; set; }
    public CallScope(CancellationToken cancellation = default) => Cancellation = cancellation;
    public object Inject(Type type)
    {
        if (type == typeof(CancellationToken)) return Cancellation;
        if (type == typeof(ILogger)) return NullLogger.Instance;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ILogger<>))
            return Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(type.GetGenericArguments()))!;
        return Activator.CreateInstance(typeof(ProgressBuffer<>).MakeGenericType(type.GetGenericArguments()), this)!;
    }
    public static bool IsInjected(Type t) => t == typeof(CancellationToken) || t == typeof(ILogger) ||
        t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(IProgress<>) || t.GetGenericTypeDefinition() == typeof(ILogger<>));
    public async ValueTask DisposeAsync()
    {
        foreach (var value in Owned.AsEnumerable().Reverse())
        {
            try { if (value is IAsyncDisposable a) await a.DisposeAsync(); else if (value is IDisposable d) d.Dispose(); }
            catch (Exception e) { Console.Error.WriteLine($"[cleanup] {e.GetType().Name}: {e.Message}"); }
        }
    }
    private sealed class ProgressBuffer<T> : IProgress<T>
    {
        private readonly CallScope _scope;
        public ProgressBuffer(CallScope scope) => _scope = scope;
        public void Report(T value)
        {
            // Bounded, synchronous, thread-safe reporting; no synchronization-context capture.
            var simple = value is null || TypeModel.IsScalar(value.GetType());
            JsonNode? report;
            try { report = simple ? JsonSupport.Node(value) : new JsonObject { ["type"] = typeof(T).FullName }; }
            catch (Exception e) when (e is ArgumentException or JsonException or NotSupportedException)
            { report = new JsonObject { ["type"] = typeof(T).FullName, ["unserializable"] = true }; }
            _scope.Progress.Enqueue(report);
            while (_scope.Progress.Count > 16) _scope.Progress.TryDequeue(out _);
        }
    }
}
