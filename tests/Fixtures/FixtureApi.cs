using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Fixtures;

public enum Flavor { Vanilla, Chocolate }
public sealed class Address { public string City { get; set; } = ""; }
public class Order
{
    public string Name { get; set; } = "";
    public Address Address { get; set; } = new();
    public List<int> Values { get; set; } = new();
    [JsonPropertyName("$cash")] public decimal Cash { get; set; }
    public string? Note { get; set; }
}
public sealed class BrokenDto { private int _number; public int Number { get => _number; set => _number = value + 1; } }
public sealed class CyclicDto { public CyclicDto? Next { get; set; } public string Name { get; set; } = ""; }
public interface IGreeter { string Greet(); }
public sealed class Friendly : IGreeter { public string Name { get; set; } = ""; public string Greet() => "Hello " + Name; }
public sealed class Formal : IGreeter { public string Name { get; set; } = ""; public string Greet() => "Good day " + Name; }
public sealed class Widget
{
    private Widget(string name) => Name = name;
    public string Name { get; }
    public static Widget Create(string name) => new(name);
    public Token ToToken() => new(Name.Length);
    public string Rename(string prefix) => prefix + Name;
}
public sealed class WidgetBag { public List<Widget> Widgets { get; set; } = new(); }
public sealed class Token { internal Token(int value) => Value = value; public int Value { get; } public Receipt Finish() => new(Value * 2); }
public sealed class Receipt { internal Receipt(int value) => Value = value; public int Value { get; } public int Read() => Value; }
public sealed class Counter : IDisposable
{
    public static int Disposed;
    private int _value;
    public Counter(int initial) => _value = initial;
    public int Current => _value;
    public void Add(int amount) { int old = _value; Thread.Sleep(3); _value = old + amount; }
    public void Dispose() => Interlocked.Increment(ref Disposed);
}

public static class Demo
{
    public static int Calls;
    private static Address? _tracked;
    public static Address Tracked() => _tracked = new Address { City = "live" };
    public static bool Same(Address value) => ReferenceEquals(_tracked, value);
    /// <summary>Add two integers, useful as a first-call smoke test.</summary>
    public static int Add(int left, int right) => left + right;
    public static string Ordinary(object value) => value.GetType().Name;
    public static Order EchoOrder(Order value) => value;
    public static string NullableText(string? text, int copies = 2) => string.Concat(Enumerable.Repeat(text ?? "nil", copies));
    public static string Scalar(Flavor flavor, Guid id, DateOnly date, TimeSpan delay, decimal amount) => $"{flavor}:{id}:{date:yyyy-MM-dd}:{delay.TotalSeconds}:{amount.ToString(CultureInfo.InvariantCulture)}";
    public static int Sum(List<List<int>> values) => values.SelectMany(x => x).Sum();
    public static string Greet(IGreeter greeter) => greeter.Greet();
    public static IGreeter Greeter(string name) => new Friendly { Name = name };
    public static void RefOut(ref int number, out string text, in int increment) { number += increment; text = number.ToString(CultureInfo.InvariantCulture); }
    public static async Task<int> Async(int value) { await Task.Delay(1); return value + 1; }
    public static ValueTask<int> ValueAsync(int value) => ValueTask.FromResult(value + 2);
    public static string Inject(string value, CancellationToken cancellation, IProgress<int> progress, ILogger logger)
    { cancellation.ThrowIfCancellationRequested(); progress.Report(1); logger.LogInformation("fixture"); return value; }
    public static T Generic<T>(T value) where T : class, new() => value;
    public static string Bytes(byte[] bytes) => Convert.ToHexString(bytes);
    public static string Read(Stream input) { using var reader = new StreamReader(input, leaveOpen: true); return reader.ReadToEnd(); }
    public static Stream MakeStream(string text) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
    public static bool Positive(int value) => value > 0;
    public static int Count(List<int> values, Func<int, bool> predicate) => values.Count(predicate);
    public static Func<int, bool> Predicate() => Positive;
    public static Widget MakeWidget(string name) => Widget.Create(name);
    public static string UseWidget(Widget widget) => widget.Name;
    public static string UseWidgets(List<Widget> widgets) => string.Join(",", widgets.Select(w => w.Name));
    public static string UseBag(WidgetBag bag) => UseWidgets(bag.Widgets);
    public static List<Widget> Widgets() => new() { Widget.Create("a"), Widget.Create("b"), Widget.Create("c") };
    public static CyclicDto Cycle() { var v = new CyclicDto { Name = "cycle" }; v.Next = v; return v; }
    public static int Disposed() => Counter.Disposed;
    public static int SideEffect(int value) { Calls++; return value; }
    public static int CallCount() => Calls;
    public static int Noisy() { Console.WriteLine("this must stay off the JSON-RPC stream"); return 7; }
    public static IEnumerable<int> Numbers() { for (int i = 0; ; i++) yield return i; }
    public static async IAsyncEnumerable<int> AsyncNumbers() { for (int i = 0; i < 5; i++) { await Task.Yield(); yield return i; } }
    public static int Fail() => throw new InvalidOperationException("fixture exploded");
    public static int SpanLength(Span<int> span) => span.Length;
    public static unsafe int Pointer(int* value) => *value;
}

public sealed record ImmutableOptions(string Label, int Count);
public sealed class TemporalDto { public DateTimeOffset When { get; set; } public Dictionary<string, int> Map { get; set; } = new(); }
public struct ManagedStruct { public string Text; }
public sealed class GetterProbe { public static int Reads; public int Value => Interlocked.Increment(ref Reads); }
public struct OutValue
{
    public static int Constructions;
    public int Number;
    public OutValue() { Constructions++; Number = 7; }
}
public sealed class ProjectionBox
{
    public int? Optional { get; set; }
    public List<int> Values { get; set; } = new() { 3 };
    public Dictionary<string, int> Map { get; set; } = new() { ["a]b\"c"] = 42 };
}
public static class EdgeCases
{
    public static ImmutableOptions Echo(ImmutableOptions value) => value;
    public static TemporalDto Temporal(TemporalDto value) => value;
    public static int Set(ISet<int> values) => values.Count;
    public static object Map() => new Dictionary<string, int> { ["answer"] = 42 };
    public static System.Text.Encoding Encoding() => System.Text.Encoding.UTF8;
    public static object ObjectEncoding() => System.Text.Encoding.UTF8;
    public static string EncodingName(System.Text.Encoding value) => value.WebName;
    public static double NonFinite() => double.NaN;
    public static IntPtr RuntimePointer() => new(123);
    public static object BoxedPointer() => new IntPtr(123);
    public static int Unmanaged<T>(T value) where T : unmanaged => 1;
    public static async Task VoidTask() => await Task.Yield();
    public static int Logger(ILogger<GetterProbe> logger) => 1;
    public static int NullableNumber(int? value) => value ?? -1;
    public static void OutStruct(out OutValue value) => value = default;
    public static int OutStructConstructions() => OutValue.Constructions;
    public static string BooleanKeys(Dictionary<bool, string> value) => value[true] + value[false];
    public static int Progress(IProgress<double> progress) { progress.Report(double.NaN); return 1; }
    public static int ProbeReads() => GetterProbe.Reads;
    public static Func<int, bool> Captured(Counter counter) => value => counter.Current > value;
    public static async IAsyncEnumerable<int> LiveTail([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    { yield return 1; yield return 2; await Task.Delay(Timeout.Infinite, token); }
    public static unsafe int FunctionPointer(delegate*<int, int> callback) => callback(1);
    public static void TypedReference(TypedReference value) { }
}
