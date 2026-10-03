using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using IntrospectMcp.Extended;

var dotnet = args.Length > 0 ? args[0] : "dotnet";
var server = args.Length > 1 ? args[1] : typeof(InvocationEngine).Assembly.Location;
var fixture = args.Length > 2 ? args[2] : typeof(Fixtures.Demo).Assembly.Location;
int phase = args.Length > 3 ? int.Parse(args[3]) : 3;
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
{
    Command = dotnet, Arguments = [server, fixture, "--extended", "--page-size", "2", "--tool-budget", "500", "--schema-budget", "1000000"], InheritEnvironmentVariables = false,
}), cancellationToken: deadline.Token);
int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
JsonObject J(string json) => JsonNode.Parse(json)!.AsObject();
async Task<JsonObject> Call(string name, JsonObject? arguments = null, string? error = null, McpClient? other = null)
{
    var response = await (other ?? client).CallToolAsync(name, arguments?.ToDictionary(p => p.Key, p => (object?)p.Value), cancellationToken: deadline.Token);
    var payload = JsonNode.Parse(string.Join("", response.Content.OfType<TextContentBlock>().Select(t => t.Text)))!.AsObject();
    Check(payload["callId"]?.GetValue<string>().StartsWith("call_", StringComparison.Ordinal) == true, name + " has callId");
    Check(response.StructuredContent is not null && JsonNode.DeepEquals(payload, JsonNode.Parse(response.StructuredContent.Value.GetRawText())), name + " wire structuredContent equals text JSON");
    Check(response.IsError == (error is not null), name + " unexpected result: " + payload);
    if (error is not null) Check(payload["error"]?["code"]?.GetValue<string>() == error, name + " expected " + error + ": " + payload);
    return payload;
}
JsonNode? Result(JsonObject response) => response["result"];

var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
Check(tools.Any(t => t.Name == "Demo_Add"), "Extended discovery includes non-*Util public types");
Check(!tools.Any(t => t.Name is "Demo_SpanLength" or "Demo_Pointer"), "Stack-only tools are not invocable");
Check(Result(await Call("Demo_Add", J("{\"left\":20,\"right\":22}")))!.GetValue<int>() == 42, "scalar call");
Check(Result(await Call("Demo_Ordinary", J("{\"value\":{\"a\":[1,true]}}")))!.GetValue<string>().StartsWith("Dictionary", StringComparison.Ordinal), "object maps to CLR dictionary, not JsonElement");
var order = J("{\"value\":{\"Name\":\"A\",\"Address\":{\"City\":\"Baton Rouge\"},\"Values\":[1,2],\"$$cash\":1.25}}");
var echo = Result(await Call("Demo_EchoOrder", order))!;
Check(echo["Address"]?["City"]?.GetValue<string>() == "Baton Rouge" && echo["$$cash"]?.GetValue<decimal>() == 1.25m, "nested DTO and escaped reserved key");
await Call("Demo_EchoOrder", J("{\"value\":{\"Name\":\"x\"}}"), "missing_property");
Check(Result(await Call("Demo_NullableText"))!.GetValue<string>() == "nilnil", "nullable and default parameters omitted");
await Call("Demo_NullableText", J("{\"copies\":null}"), "null_not_allowed");
Check(Result(await Call("Demo_Scalar", J("{\"flavor\":\"Chocolate\",\"id\":\"00000000-0000-0000-0000-000000000001\",\"date\":\"2024-01-02\",\"delay\":\"00:00:03\",\"amount\":1.25}")))!.GetValue<string>().EndsWith(":3:1.25", StringComparison.Ordinal), "enum, temporal, Guid, decimal");
Check(Result(await Call("Demo_Sum", J("{\"values\":[[1,2],[3,4]]}")))!.GetValue<int>() == 10, "recursive collections");
Check(Result(await Call("Demo_Greet", J("{\"greeter\":{\"$type\":\"Fixtures.Friendly\",\"Name\":\"Ada\"}}")))!.GetValue<string>() == "Hello Ada", "interface discriminator");
await Call("Demo_Greet", J("{\"greeter\":{\"$type\":\"System.String\",\"$value\":\"bad\"}}"), "type_mismatch");
var polymorphic = Result(await Call("Demo_Greeter", J("{\"name\":\"Ada\"}")));
Check(polymorphic?["$type"]?.GetValue<string>() == "Fixtures.Friendly", "runtime type tag on polymorphic return");
var byref = await Call("Demo_RefOut", J("{\"number\":3,\"increment\":4}"));
Check(byref["outputs"]?["number"]?.GetValue<int>() == 7 && byref["outputs"]?["text"]?.GetValue<string>() == "7" && byref["ok"]?.GetValue<bool>() == true, "ref/out and void envelope");
Check(Result(await Call("Demo_Async", J("{\"value\":3}")))!.GetValue<int>() == 4, "Task awaited");
Check(Result(await Call("Demo_ValueAsync", J("{\"value\":3}")))!.GetValue<int>() == 5, "ValueTask awaited");
var injection = await Call("Demo_Inject", J("{\"value\":\"ok\"}"));
Check(injection["progress"]?.AsArray().Count == 1, "injected cancellation/progress/logger");
var injectionSchema = tools.Single(t => t.Name == "Demo_Inject").JsonSchema;
Check(injectionSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).SequenceEqual(["value"]), "injections omitted from schema");
await Call("Demo_Generic", J("{\"value\":{}}"), "type_args_required");
await Call("Demo_Generic", J("{\"typeArgs\":[\"string\"],\"value\":\"a\"}"), "generic_constraint");
Check(Result(await Call("Demo_Generic", J("{\"typeArgs\":[\"Fixtures.Address\"],\"value\":{\"City\":\"x\"}}")))?["City"]?.GetValue<string>() == "x", "constrained generic closes before binding");
Check(Result(await Call("Demo_Bytes", J("{\"bytes\":\"AQID\"}")))!.GetValue<string>() == "010203", "base64 byte[]");
Check(Result(await Call("Demo_Read", J("{\"input\":{\"base64\":\"aGVsbG8=\"}}")))!.GetValue<string>() == "hello", "stream base64 input");
await Call("Demo_Read", J("{\"input\":{\"path\":\"/etc/passwd\"}}"), "file_access_disabled");
var failure = await Call("Demo_Fail", error: "invocation_failed");
Check(failure["error"]?["exceptionType"]?.GetValue<string>() == typeof(InvalidOperationException).FullName && failure["error"]?["targetStack"]?.AsArray().Count > 0, "unwrapped exception and target-DLL stack");
await Call("Demo_Add", J("{\"left\":1,\"right\":2,\"typo\":3}"), "unknown_argument");
Check(Result(await Call("Demo_Noisy"))!.GetValue<int>() == 7, "target stdout redirected away from JSON-RPC");
var coverage = Result(await Call("coverage"))!;
Check(coverage["unsupportedByReason"]?["pointer_or_stack_only"]?.GetValue<int>() >= 2, "unsupported reason counts");
Check(coverage["dtoDemotions"]?["Fixtures.BrokenDto"] is not null, "P4 round-trip failure demotes and reports DTO");
Check(coverage["total"]!.GetValue<int>() == coverage["literal"]!.GetValue<int>() + coverage["handle"]!.GetValue<int>() + coverage["unsupported"]!.GetValue<int>(), "coverage adds up; no silent drop");
var implementers = Result(await Call("find_implementations", J("{\"type\":\"Fixtures.IGreeter\"}")));
Check(implementers?["total"]?.GetValue<int>() == 2, "two discoverable interface implementers");
Console.WriteLine($"PASS phase 1: {checks} assertions, real MCP stdio and structured JSON-RPC payloads");


if (phase >= 2)
{
    JsonObject H(string id) => new() { ["$handle"] = id };
    string Handle(JsonObject response) => Result(response)!["$handle"]!.GetValue<string>();
    var produced = await Call("Demo_MakeWidget", J("{\"name\":\"abcd\"}"));
    string widget = Handle(produced);
    Check(Result(produced)?["producingCallId"]?.GetValue<string>() == produced["callId"]?.GetValue<string>(), "producer call provenance");
    Check(Result(await Call("Demo_UseWidget", new() { ["widget"] = H(widget) }))!.GetValue<string>() == "abcd", "factory/consumer live handle");
    string token = Handle(await Call("Widget_ToToken", new() { ["target"] = H(widget) }));
    string receipt = Handle(await Call("Token_Finish", new() { ["target"] = H(token) }));
    Check(Result(await Call("Receipt_Read", new() { ["target"] = H(receipt) }))!.GetValue<int>() == 8, "live factory -> token -> receipt -> read chain");
    Check(Result(await Call("Demo_UseWidgets", new() { ["widgets"] = new JsonArray(H(widget), H(widget)) }))!.GetValue<string>() == "abcd,abcd", "recursive list handle substitution");
    Check(Result(await Call("Demo_UseBag", new() { ["bag"] = new JsonObject { ["Widgets"] = new JsonArray(H(widget)) } }))!.GetValue<string>() == "abcd", "nested DTO/collection handle substitution");
    string counter = Handle(await Call("new_Counter", J("{\"initial\":10}")));
    await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Call("Counter_Add", new() { ["target"] = H(counter), ["amount"] = 1 })));
    Check(Result(await Call("Demo_Add", new() { ["left"] = new JsonObject { ["$ref"] = counter, ["path"] = "Current" }, ["right"] = 0 }))!.GetValue<int>() == 22, "same-handle concurrent calls serialized, property projection");
    await Call("Counter_Add", J("{\"amount\":1}"), "target_required");
    await Call("Demo_UseWidget", new() { ["widget"] = H(counter) }, "type_mismatch");
    await Call("Demo_Add", new() { ["left"] = new JsonObject { ["$ref"] = counter, ["path"] = "GetType()" }, ["right"] = 0 }, "invalid_projection");
    await Call("release_handle", new() { ["handle"] = counter });
    Check(Result(await Call("Demo_Disposed"))!.GetValue<int>() == 1, "release calls IDisposable.Dispose");
    var stale = await Call("Counter_Add", new() { ["target"] = H(counter), ["amount"] = 1 }, "invalid_handle");
    Check(stale["error"]?["details"]?["reason"]?.GetValue<string>() == "released" && stale["error"]?["details"]?["producingCallId"] is not null, "use-after-release includes reason and producing call");
    string predicate = Handle(await Call("Demo_Predicate"));
    Check(Result(await Call("Demo_Count", new() { ["values"] = new JsonArray(-1, 2, 3), ["predicate"] = H(predicate) }))!.GetValue<int>() == 2, "delegate return -> handle -> callback chain");
    Check(Result(await Call("Demo_Count", J("{\"values\":[-1,2,3],\"predicate\":{\"$method\":\"Demo_Positive\"}}")))!.GetValue<int>() == 2, "compatible $method binding");
    await Call("Demo_Count", J("{\"values\":[1],\"predicate\":{\"$method\":\"Demo_Add\"}}"), "delegate_signature");
    await Call("Demo_Count", J("{\"values\":[1],\"predicate\":{\"$expr\":\"x => true\"}}"), "expression_disabled");
    string stream = Handle(await Call("Demo_MakeStream", J("{\"text\":\"hello\"}")));
    Check(Result(await Call("read_stream", new() { ["handle"] = stream, ["maxBytes"] = 2 }))?["base64"]?.GetValue<string>() == "aGU=", "bounded stream reads");
    Check(Result(await Call("Demo_Read", new() { ["input"] = H(stream) }))!.GetValue<string>() == "llo", "stream identity and position compose");
    await Call("release_handle", new() { ["handle"] = stream });
    var numbers = Result(await Call("Demo_Numbers"))!;
    Check(numbers["items"]!.AsArray().Select(x => x!.GetValue<int>()).SequenceEqual([0, 1]), "infinite synchronous sequence is bounded");
    string cursor = numbers["cursor"]!.GetValue<string>();
    var next = Result(await Call("read_sequence", new() { ["cursor"] = cursor }))!;
    Check(next["items"]!.AsArray().Select(x => x!.GetValue<int>()).SequenceEqual([2, 3]), "sequence cursor resumes without re-enumeration");
    await Call("release_handle", new() { ["handle"] = cursor });
    var asyncNumbers = Result(await Call("Demo_AsyncNumbers"))!;
    string asyncCursor = asyncNumbers["cursor"]!.GetValue<string>();
    await Call("read_sequence", new() { ["cursor"] = asyncCursor });
    var last = Result(await Call("read_sequence", new() { ["cursor"] = asyncCursor }))!;
    Check(last["items"]!.AsArray().Single()!.GetValue<int>() == 4 && last["next"] is null, "async sequence final page and disposal");
    await Call("read_sequence", new() { ["cursor"] = asyncCursor }, "invalid_handle");
    var widgets = Result(await Call("Demo_Widgets"))!;
    Check(widgets["items"]!.AsArray().All(x => x?["$handle"] is not null), "sequence of opaque objects produces usable item handles");
    Check(Result(await Call("Demo_NullableText", new() { ["text"] = new JsonObject { ["$ref"] = widgets["$handle"]!.GetValue<string>(), ["path"] = "[1].Name" }, ["copies"] = 1 }))!.GetValue<string>() == "b", "index/property projection");
    string cycle = Handle(await Call("Demo_Cycle"));
    Check(cycle.StartsWith("h_", StringComparison.Ordinal), "cyclic DTO return handled without recursion");
    var targetLiteral = J("{\"target\":{\"Name\":\"Grace\"}}");
    Check(Result(await Call("Friendly_Greet", targetLiteral))!.GetValue<string>() == "Hello Grace", "explicit literal DTO instance target");
    await using (var other = await McpClient.CreateAsync(new StdioClientTransport(new() { Command = dotnet, Arguments = [server, fixture, "--extended", "--schema-budget", "1000000"], InheritEnvironmentVariables = false }), cancellationToken: deadline.Token))
    { await Call("Demo_UseWidget", new() { ["widget"] = H(widget) }, "invalid_handle", other); }
    var clock = new TestClock();
    var options = new ServiceOptions { MaxHandles = 4, HandleTtl = TimeSpan.FromSeconds(10) };
    await using (var store = new HandleStore(options, clock))
    {
        var resource = new TestResource(); var lease = store.Register(resource, typeof(TestResource), "producer"); string id = lease.Entry.Id;
        clock.Advance(TimeSpan.FromSeconds(20)); await store.SweepAsync(); Check(resource.Disposals == 0, "active pin prevents expiry");
        await lease.DisposeAsync(); clock.Advance(TimeSpan.FromSeconds(11)); await store.SweepAsync();
        Check(resource.Disposals == 1, "TTL disposes exactly once");
        try { await store.Pin(id).DisposeAsync(); throw new Exception("Expired handle accepted"); }
        catch (BindingException e) { Check(e.Details["reason"]?.GetValue<string>() == "expired" && e.Details["producingCallId"]?.GetValue<string>() == "producer", "expired tombstone details"); }
        var lru = new List<TestResource>(); string? first = null;
        for (int i = 0; i < 5; i++)
        {
            var item = new TestResource(); lru.Add(item); var pin = store.Register(item, typeof(TestResource), "lru"); first ??= pin.Entry.Id;
            await pin.DisposeAsync(); clock.Advance(TimeSpan.FromMilliseconds(1));
        }
        await store.DrainAsync(); Check(lru[0].Disposals == 1 && lru[1].Disposals == 0, "LRU evicts and disposes oldest unused object");
        try { await store.Pin(first!).DisposeAsync(); throw new Exception("Evicted handle accepted"); }
        catch (BindingException e) { Check(e.Details["reason"]?.GetValue<string>() == "evicted", "LRU tombstone reason"); }
    }
    Console.WriteLine($"PASS phase 2: {checks} cumulative assertions; handles, 3+ live chains, disposal, projection, callbacks, paging, isolation, concurrency");
}
if (phase >= 3)
{
    var chain = J("""
    {"steps":[
      {"id":"widget","method":"Demo_MakeWidget","arguments":{"name":"sample"}},
      {"id":"token","method":"Widget_ToToken","arguments":{"target":{"$step":"widget"}}},
      {"id":"receipt","method":"Token_Finish","arguments":{"target":{"$step":"token"}}},
      {"id":"read","method":"Receipt_Read","arguments":{"target":{"$step":"receipt"}}}
    ]}
    """);
    var pipeline = await Call("pipeline", chain);
    Check(pipeline["completed"]?.GetValue<int>() == 4 && pipeline["result"]?.GetValue<int>() == 12, "ordered live pipeline result");
    Check(pipeline["steps"]!.AsArray().Select(s => s!["callId"]!.GetValue<string>()).Distinct().Count() == 4, "per-step callId provenance");
    var identity = await Call("pipeline", J("""
      {"steps":[{"id":"made","method":"Demo_Tracked"},{"id":"same","method":"Demo_Same","arguments":{"value":{"$step":"made"}}}]}
      """));
    Check(identity["result"]?.GetValue<bool>() == true, "pipeline passes LIVE DTO identity, not serialized copies");
    int before = Result(await Call("Demo_CallCount"))!.GetValue<int>();
    var dry = await Call("pipeline", J("""
      {"dryRun":true,"steps":[{"id":"touch","method":"Demo_SideEffect","arguments":{"value":4}},{"id":"fail","method":"Demo_Fail"}]}
      """));
    Check(dry["dryRun"]?.GetValue<bool>() == true && Result(await Call("Demo_CallCount"))!.GetValue<int>() == before, "dryRun performs no target invocation");
    await Call("pipeline", J("""
      {"dryRun":true,"steps":[{"id":"number","method":"Demo_Add","arguments":{"left":1,"right":2}},{"id":"bad","method":"Demo_UseWidget","arguments":{"widget":{"$step":"number"}}}]}
      """), "type_mismatch");
    var partial = await Call("pipeline", J("""
      {"steps":[{"id":"one","method":"Demo_SideEffect","arguments":{"value":1}},{"id":"bad","method":"Demo_Fail"},{"id":"never","method":"Demo_SideEffect","arguments":{"value":2}}]}
      """), "invocation_failed");
    Check(partial["completed"]?.GetValue<int>() == 1 && partial["failedStep"]?.GetValue<string>() == "bad" && Result(await Call("Demo_CallCount"))!.GetValue<int>() == before + 1, "pipeline stops at first failure, retains results and does not claim rollback");
    await Call("pipeline", J("""
      {"steps":[{"id":"a","method":"Demo_UseWidget","arguments":{"widget":{"$step":"later"}}},{"id":"later","method":"Demo_MakeWidget","arguments":{"name":"x"}}]}
      """), "unknown_step");
    var outputs = await Call("pipeline", J("""
      {"steps":[{"id":"split","method":"Demo_RefOut","arguments":{"number":3,"increment":4}},{"id":"use","method":"Demo_Add","arguments":{"left":{"$step":"split","output":"number"},"right":1}}]}
      """));
    Check(outputs["result"]?.GetValue<int>() == 8, "pipeline ref/out values remain live and composable");
    var hints = Result(await Call("type_flow", J("{\"type\":\"Fixtures.Widget\"}")))!;
    Check(hints["producers"]!.AsArray().Count is > 0 and <= 3 && hints["consumers"]!.AsArray().Count is > 0 and <= 3, "top-three type-flow hints");
    var plans = Result(await Call("how_to_get", J("{\"type\":\"Fixtures.Receipt\",\"maxDepth\":3}")))!["plans"]!.AsArray();
    Check(plans.Count is > 0 and <= 3, "BFS discovers a three-call plan from literal sources");
    var ready = await Call("pipeline", plans[0]!["arguments"]!.AsObject());
    Check(ready["result"]?["$handle"] is not null && ready["completed"]?.GetValue<int>() == 3, "how_to_get plan executes without manual substitutions");
    var describe = Result(await Call("describe_type", J("{\"type\":\"Fixtures.Demo\"}")))!;
    Check(describe["methods"]!.AsArray().Any(m => m?["name"]?.GetValue<string>() == "Demo_Add" && m["description"]!.GetValue<string>().Contains("Add two integers", StringComparison.Ordinal)), "adjacent XML summaries loaded");
    await using var compact = await McpClient.CreateAsync(new StdioClientTransport(new()
    { Command = dotnet, Arguments = [server, fixture, "--extended", "--tool-budget", "0", "--deny", "Fixtures.Demo.Fail"], InheritEnvironmentVariables = false }), cancellationToken: deadline.Token);
    var compactTools = await compact.ListToolsAsync(cancellationToken: deadline.Token);
    Check(compactTools.Any(t => t.Name == "invoke_method") && compactTools.All(t => !t.Name.StartsWith("Demo_", StringComparison.Ordinal)), "tool budget switches to compact catalog without losing invoke access");
    Check(Result(await Call("session_info", other: compact))?["discovery"]?.GetValue<string>() == "compact", "compact mode advertised to client");
    Check(Result(await Call("invoke_method", J("{\"method\":\"Demo_Add\",\"arguments\":{\"left\":2,\"right\":3}}"), other: compact))!.GetValue<int>() == 5, "generic dispatcher remains usable in compact mode");
    await Call("invoke_method", J("{\"method\":\"Demo_Fail\"}"), "unsupported_method", compact);
    var typePage = Result(await Call("list_types", J("{\"limit\":2}"), other: compact))!;
    Check(typePage["items"]!.AsArray().Count == 2 && typePage["nextOffset"]?.GetValue<int>() == 2, "bounded discoverability pagination");
    Console.WriteLine($"PASS phase 3: {checks} cumulative assertions; live pipelines, invocation-free dryRun, partial failure, BFS plans, documentation and budget mode");
}
if (phase >= 3)
{
    Check(Result(await Call("EdgeCases_Echo", J("{\"value\":{\"Label\":\"record\",\"Count\":3}}")))?["Label"]?.GetValue<string>() == "record", "immutable DTO constructor binding and round-trip");
    Check(Result(await Call("EdgeCases_Temporal", J("{\"value\":{\"When\":\"2000-01-02T03:04:05Z\",\"Map\":{\"a\":1}}}")))?["Map"]?["a"]?.GetValue<int>() == 1, "canonical temporal DTO self-test is not falsely demoted");
    Check(Result(await Call("EdgeCases_Set", J("{\"values\":[1,1,2]}")))!.GetValue<int>() == 2, "ISet literal binds to HashSet");
    var tagged = Result(await Call("EdgeCases_Map"))!;
    Check(tagged["$type"] is not null && Result(await Call("Demo_Ordinary", new() { ["value"] = tagged.DeepClone() }))!.GetValue<string>().StartsWith("Dictionary", StringComparison.Ordinal), "runtime dictionary type tag round-trips through loaded type resolver");
    Check(Result(await Call("EdgeCases_NonFinite"))?["$handle"] is not null, "non-JSON number uses a live handle");
    await Call("EdgeCases_BoxedPointer", error: "unsupported_return");
    await Call("EdgeCases_Unmanaged", J("{\"typeArgs\":[\"Fixtures.ManagedStruct\"],\"value\":{\"Text\":\"managed\"}}"), "generic_constraint");
    Check((await Call("EdgeCases_VoidTask"))["result"] is null, "non-generic Task returns void, not boxed VoidTaskResult");
    Check(Result(await Call("EdgeCases_Logger"))!.GetValue<int>() == 1, "generic ILogger injected");
    Check(Result(await Call("EdgeCases_Encoding"))!.GetValue<string>() == "utf-8", "Encoding return keeps its scalar adapter representation");
    var taggedEncoding = Result(await Call("EdgeCases_ObjectEncoding"))!;
    Check(Result(await Call("EdgeCases_EncodingName", new() { ["value"] = taggedEncoding.DeepClone() }))!.GetValue<string>() == "utf-8", "polymorphic Encoding adapter round-trips");
    var livePage = Result(await Call("EdgeCases_LiveTail"))!;
    Check(livePage["items"]!.AsArray().Count == 2, "bounded async page does not block prefetching the next event");
    await Call("release_handle", new() { ["handle"] = livePage["cursor"]!.GetValue<string>() });
    string probe = Result(await Call("new_GetterProbe"))!["$handle"]!.GetValue<string>();
    Check(Result(await Call("EdgeCases_ProbeReads"))!.GetValue<int>() == 0, "handle previews never execute custom getters");
    await Call("pipeline", new() { ["dryRun"] = true, ["steps"] = new JsonArray(new JsonObject { ["id"] = "inspect", ["method"] = "Demo_Add", ["arguments"] = new JsonObject { ["left"] = new JsonObject { ["$ref"] = probe, ["path"] = "Value" }, ["right"] = 1 } }) });
    Check(Result(await Call("EdgeCases_ProbeReads"))!.GetValue<int>() == 0, "dryRun projection validates types without running getters");
    string capturedTarget = Result(await Call("new_Counter", J("{\"initial\":5}")))!["$handle"]!.GetValue<string>();
    string callback = Result(await Call("EdgeCases_Captured", new() { ["counter"] = new JsonObject { ["$handle"] = capturedTarget } }))!["$handle"]!.GetValue<string>();
    await Call("release_handle", new() { ["handle"] = capturedTarget }, "handle_busy");
    await Call("release_handle", new() { ["handle"] = callback });
    await Call("release_handle", new() { ["handle"] = capturedTarget });
    await Call("list_handles", J("{\"typo\":true}"), "unknown_argument");
    var listing = Result(await Call("list_handles", J("{\"limit\":1}")))!;
    Check(listing["handles"]!.AsArray().Count == 1 && listing["nextOffset"]!.GetValue<int>() == 1, "handle discovery is paginated");
    Check(Result(await Call("list_types", J("{\"offset\":2147483647}")))?["nextOffset"] is null, "pagination cannot overflow at maximum offset");
    await Call("invoke_method", J("{\"method\":\"Demo_Add\",\"arguments\":[]}"), "invalid_argument");
    var numbers = Result(await Call("Demo_Numbers"))!;
    var fresh = Result(await Call("read_sequence", new() { ["handle"] = numbers["$handle"]!.GetValue<string>(), ["pageSize"] = 1 }))!;
    Check(fresh["items"]!.AsArray().Count == 1, "fresh sequence enumeration honors requested pageSize");
    await Call("read_sequence", new() { ["handle"] = numbers["$handle"]!.GetValue<string>(), ["cursor"] = numbers["cursor"]!.GetValue<string>() }, "invalid_argument");
    await Call("read_sequence", new() { ["handle"] = numbers["$handle"]!.GetValue<string>(), ["pageSize"] = 0 }, "invalid_argument");
    await Call("release_handle", new() { ["handle"] = fresh["cursor"]!.GetValue<string>() });
    await Call("release_handle", new() { ["handle"] = numbers["cursor"]!.GetValue<string>() });
    await Call("release_handle", new() { ["handle"] = numbers["$handle"]!.GetValue<string>() });
    string box = Result(await Call("new_ProjectionBox"))!["$handle"]!.GetValue<string>();
    JsonObject Ref(string path) => new() { ["$ref"] = box, ["path"] = path };
    Check(Result(await Call("EdgeCases_NullableNumber", new() { ["value"] = Ref("Optional") }))!.GetValue<int>() == -1, "projected null binds to Nullable<T>");
    Check(Result(await Call("EdgeCases_NullableNumber", new() { ["value"] = Ref("Values[0]") }))!.GetValue<int>() == 3, "boxed value binds to Nullable<T>");
    await Call("Demo_Ordinary", new() { ["value"] = Ref("Optional") }, "null_not_allowed");
    Check(Result(await Call("Demo_Add", new() { ["left"] = Ref("Map[\"a]b\\\"c\"]"), ["right"] = 0 }))!.GetValue<int>() == 42, "quoted dictionary projection accepts embedded closing bracket and escaped quote");
    await Call("pipeline", new() { ["dryRun"] = true, ["steps"] = new JsonArray(new JsonObject { ["id"] = "bad", ["method"] = "Demo_Add", ["arguments"] = new JsonObject { ["left"] = Ref("Values[\"wrong\"]"), ["right"] = 1 } }) }, "invalid_indexer");
    Check(Result(await Call("EdgeCases_BooleanKeys", J("{\"value\":{\"true\":\"yes\",\"false\":\"no\"}}")))!.GetValue<string>() == "yesno", "boolean dictionary keys bind from JSON property names");
    Check(Result(await Call("EdgeCases_Progress"))!.GetValue<int>() == 1, "non-JSON progress values cannot crash target methods");
    int outConstructors = Result(await Call("EdgeCases_OutStructConstructions"))!.GetValue<int>();
    await Call("pipeline", J("{\"dryRun\":true,\"steps\":[{\"id\":\"out\",\"method\":\"EdgeCases_OutStruct\"}]}"));
    Check(Result(await Call("EdgeCases_OutStructConstructions"))!.GetValue<int>() == outConstructors, "dryRun never executes custom out-struct constructors");
    Check((await Call("EdgeCases_OutStruct"))["outputs"]?["value"]?["Number"]?.GetValue<int>() == 0, "out value types use normal zero-initialized reflection storage");
    var updatedCoverage = Result(await Call("coverage"))!;
    Check(updatedCoverage["unsupportedByReason"]?["pointer_or_stack_only"]?.GetValue<int>() >= 5, "function pointers, TypedReference and IntPtr exclusions are reported");
    await using var local = new InvocationEngine(typeof(Fixtures.Demo).Assembly);
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    Check((await local.ExecuteAsync("Demo_Add", J("{\"left\":1,\"right\":2}"), cancelled.Token))["error"]?["code"]?.GetValue<string>() == "cancelled", "cooperative cancellation is a structured result");
    await using var tinyBudget = new InvocationEngine(typeof(Fixtures.Demo).Assembly, new ServiceOptions { ToolBudget = 1000, SchemaTokenBudget = 1 });
    Check(tinyBudget.Tools().All(t => !t.ProtocolTool.Name.StartsWith("Demo_", StringComparison.Ordinal)) && tinyBudget.CompactMode, "schema-token budget also triggers compact mode");
    await using (var cursorCheck = SequenceCursor.Create(Fixtures.EdgeCases.LiveTail(), "test_source", CancellationToken.None))
    {
        Check((await cursorCheck.Page(2, deadline.Token)).Items.Count == 2, "direct live cursor produces initial page");
        using var pageCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        try { await cursorCheck.Page(1, pageCancellation.Token).WaitAsync(TimeSpan.FromSeconds(2)); Check(false, "async page should honor cancellation"); }
        catch (OperationCanceledException) { Check(true, "in-flight page cancellation reaches the async enumerator"); }
    }
    await using (var tight = new InvocationEngine(typeof(Fixtures.Demo).Assembly, new ServiceOptions { MaxHandles = 4 }))
    {
        var pins = Enumerable.Range(0, 4).Select(_ => tight.Handles.Register(new TestResource(), typeof(TestResource), "test_pin")).ToArray();
        try
        {
            int before = Fixtures.Counter.Disposed;
            var capacity = await tight.ExecuteAsync("new_Counter", J("{\"initial\":1}"));
            Check(capacity["error"]?["code"]?.GetValue<string>() == "handle_capacity" && Fixtures.Counter.Disposed == before + 1, "failed export disposes newly produced resources");
        }
        finally { foreach (var pin in pins) await pin.DisposeAsync(); }
    }
    Console.WriteLine($"PASS edge cases: {checks} cumulative assertions");
}
Console.WriteLine($"PASS requested phase {phase}");

sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now += delta;
}
sealed class TestResource : IDisposable { public int Disposals; public void Dispose() => Disposals++; }
