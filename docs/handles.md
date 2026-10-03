# Extended mode: from DLL to usable, composable tools

Extended mode is **opt-in**. The default entry point retains the legacy `*Util`
static-method discovery, tool names, adapters and MCP stdio transport.

```sh
dotnet introspectMCP.dll /absolute/path/Library.dll --extended
```

**Load trusted managed DLLs only. This is not a sandbox.** Constructors, getters,
setters, methods and disposal code in that DLL execute in this process. Policy
filters control exposed entry points, not what trusted code can do internally.

## Build, test, and connect

On an ordinary .NET 8 development machine with NuGet access:

```sh
# Original Windows target (WindowsDesktop-dependent DLLs need Windows at runtime).
dotnet build -c Release -p:EnableWindowsTargeting=true

# Opt-in cross-platform managed-only build; original default target is unchanged.
dotnet build -c Release -p:PortableBuild=true
dotnet run --project tests/IntegrationTests -c Release -p:PortableBuild=true
```

In the restricted Linux workspace, the verified **.NET 10 source-build fallback**
is a separate, working build/test route:

```sh
./scripts/test.sh             # Bootstrap if necessary, legacy + extended tests
./scripts/test.sh --offline   # Cached sources/toolchain only
```

See [the fallback's provenance and limitations](build-without-nuget.md). Passing
that suite does **not** constitute verification of the .NET 8/Windows artifact.

A typical stdio MCP client configuration:

```json
{
  "mcpServers": {
    "library": {
      "command": "dotnet",
      "args": [
        "/absolute/path/introspectMCP.dll",
        "/absolute/path/Library.dll",
        "--extended"
      ]
    }
  }
}
```

The fallback script prints the actual executable and server DLL paths to use in
this workspace. Do not use `dotnet run` in a stdio MCP configuration unless you
also ensure build output cannot contaminate the protocol stream.

## Start with discovery, not guessed signatures

1. `session_info {}` explains the conventions and current limits.
2. `list_types {"query":"Widget","limit":20}` searches the assembly.
3. `describe_type {"type":"Fixtures.Widget"}` returns method IDs, constructors,
   input schemas, exclusions, XML summaries/signatures, and type-flow hints.
4. Call the advertised method, or always use `invoke_method`:

```json
{
  "method": "Demo_MakeWidget",
  "arguments": { "name": "demo" }
}
```

`describe_type` accepts `offset` and `limit` (default 20, maximum 100). `list_types`
also paginates. `find_implementations` returns up to 50 implementations per page.
Use exact method names from discovery: overloads and same-named types receive a
stable, sanitized signature-hash suffix. Public declared methods and constructors
are considered, not just `*Util` classes. Unsupported methods remain visible in
the catalog and coverage report; `invoke_method` returns a structured reason.

When the number or estimated schema-token cost of individual tools exceeds the
budget, only the compact catalog/composition/lifecycle tools are advertised.
**Methods remain callable through `invoke_method` and `pipeline`.**

## Literal arguments

Arguments recursively accept:

- JSON scalars; enum names, flags strings, or integers; decimal, Guid, Uri,
  DateTime/DateTimeOffset, DateOnly/TimeOnly, TimeSpan.
- Nullable inputs and omitted default-valued arguments. A default does not make
  explicit `null` valid for a non-nullable value.
- Arrays, common generic lists/sets, and scalar-keyed dictionaries.
- Mutable DTOs and constructor-bound DTOs/records. Constructor parameter names
  must match CLR member names; wire names honor `JsonPropertyName`.
- A compatible handle at any of those nesting levels.

DTO candidates undergo a load-time synthetic bind → serialize → bind → serialize
comparison. Failures are logged and demoted to handle-only. This is a conservative
sample test, not a proof for every possible value. Cyclic type shapes and resources
are handle-only. DTO tests execute target constructors/accessors at load time.

An example DTO argument using the test fixture:

```json
{
  "value": {
    "Name": "order-1",
    "Address": { "City": "Baton Rouge" },
    "Values": [1, 2],
    "$$cash": 12.50
  }
}
```

**Escape data keys beginning with `$` by adding another `$`.** Here the CLR
member's wire name is `$cash`. Reserved markers such as `$handle` and `$type`
are not ordinary DTO data. Dictionaries use the same escaping rule.

Bare `object` becomes ordinary CLR dictionaries, lists, strings, booleans, and
numbers—not `JsonElement`. For an interface, abstract/base type, or a specifically
typed object value, provide a discriminator:

```json
{ "greeter": { "$type": "Fixtures.Friendly", "Name": "Ada" } }
```

```json
{ "value": { "$type": "System.Int32", "$value": 42 } }
```

Runtime types that differ from the declaration are tagged in output. Generic CLR
names may be verbose; reuse the emitted tag or a handle rather than guessing it.
`find_implementations` supplies concrete choices; schemas show a capped preview.

## Live handles and instance methods

Every tool result has a `callId`. Success includes `ok`, `result`, and (for method
calls) `outputs` for `ref`/`out` arguments. Non-inline results look like:

```json
{
  "callId": "call_1_example",
  "ok": true,
  "result": {
    "$handle": "h_example",
    "$type": "Fixtures.Widget",
    "declaredType": "Fixtures.Widget",
    "producingCallId": "call_1_example",
    "summary": "Widget",
    "members": { "Name": "demo" }
  },
  "outputs": {},
  "progress": []
}
```

The IDs and call provenance above are illustrative: use the random opaque ID returned by your session.
The preview never invokes user `ToString` or property getters; it samples only
small scalar fields/auto-property backing fields.

Pass the object into another call:

```json
{ "method": "Demo_UseWidget", "arguments": { "widget": { "$handle": "h_example" } } }
```

Instance calls **require** `target`; no hidden instance is manufactured:

```json
{ "method": "Widget_ToToken", "arguments": { "target": { "$handle": "h_example" } } }
```

A literal round-trippable DTO can also be an explicit instance target. Constructor
tools are named `new_Type` (with suffixes for overloads) and always return a handle,
including for otherwise-inline DTOs.

Handles work in mixed collections and nested DTOs:

```json
{
  "method": "Demo_UseBag",
  "arguments": { "bag": { "Widgets": [{ "$handle": "h_first" }, { "$handle": "h_second" }] } }
}
```

The binder checks assignability; it does not silently convert unrelated objects.
Errors include expected/actual types and up to three conversion candidates where
available. Calls lock referenced handles in stable ID order for their duration.
Distinct handles can run concurrently. The DLL can still mutate its own static
state or launch background work; the service cannot make arbitrary library code
thread-safe.

### Project a member without invoking a method

```json
{
  "method": "Demo_NullableText",
  "arguments": {
    "text": { "$ref": "h_widget_list", "path": "[1].Name" },
    "copies": 1
  }
}
```

Paths support public readable properties/fields, numeric list indices, and quoted
string dictionary indices. Use CLR member names, not JSON aliases. Method-call
syntax such as `GetType()` is rejected. Actual projections may execute getters;
dry-run projections inspect only their types.

### Lifetime and cleanup

- Handles belong to one stdio session/process. They do not survive restart and
  cannot be used by another client session.
- Idle TTL and LRU capacity are configurable. Active calls pin their inputs.
- `list_handles {}` reports live values, provenance, limits and disposal errors.
  It accepts `offset`/`limit` (default 50, maximum 100) and returns `nextOffset`.
- `release_handle {"handle":"h_example"}` releases/disposes the value. A handle
  still used by an active call, cursor or retained delegate returns `handle_busy`.
- `IDisposable`/`IAsyncDisposable` is honored on release, eviction and shutdown.
  Public disposal entry points are catalogued as `lifecycle_use_release_handle`;
  use the lifecycle tool rather than manually disposing behind the store's back.
- Sequence cursors keep their source alive. Escaping delegates conservatively
  retain borrowed handle inputs, including closure captures.
- Recent tombstones preserve `released`, `expired`, `evicted`, or `completed`
  reasons and the producing call. Tombstones are bounded (at least 1,024);
  sufficiently old or foreign IDs report `unknown_or_foreign` instead.

## Delegates, generics, ref/out, and injected services

A delegate parameter accepts a delegate handle or a compatible registered method:

```json
{
  "method": "Demo_Count",
  "arguments": { "values": [-1, 2, 3], "predicate": { "$method": "Demo_Positive" } }
}
```

Instance bindings also require `target` inside the `$method` object. CLR signature
compatibility is checked before binding. `$expr` is explicitly disabled.

Generic methods require a `typeArgs` array (aliases such as `int`/`string` or
already-loaded CLR type names):

```json
{
  "method": "Demo_Generic",
  "arguments": { "typeArgs": ["Fixtures.Address"], "value": { "City": "Paris" } }
}
```

CLR class/struct/new/base/interface constraints and unmanaged field constraints
are checked before closing the method. Compiler-only nullable annotations such
as `notnull` are not a runtime type-system guarantee.

`out` is omitted from input; `ref` appears in input and output; `in` is input-only:

```json
{ "method": "Demo_RefOut", "arguments": { "number": 3, "increment": 4 } }
```

The result contains `outputs.number = 7` and `outputs.text = "7"`.

`CancellationToken`, `IProgress<T>`, `ILogger` and `ILogger<T>` are injected and
omitted from schemas. Progress keeps the last 16 scalar reports in the response;
complex or non-JSON reports become type summaries. Loggers are no-op instances. This version
does not stream progress notifications or arbitrary target logs over MCP.
Cancellation is cooperative; synchronous/uncooperative DLL code cannot be safely
interrupted in-process.

## Bounded sequences and streams

Sync and async sequence returns include:

- A handle for the original sequence.
- A bounded `items` array (opaque items themselves get handles).
- A `cursor` and ready-to-use `next` tool call when enumeration may continue.

```json
{ "cursor": "h_cursor", "pageSize": 40 }
```

Send that to `read_sequence`. To start a new enumeration instead, supply `handle`.
Never supply both. A full final page can be followed by an empty terminal page:
the service does **not** prefetch a potentially blocking next event. Release an
unfinished cursor to dispose its enumerator. A source that blocks or ignores
cancellation can still block a read; there is no forced thread termination.

`byte[]` uses base64. `Stream` accepts a handle, `{ "base64": "aGVsbG8=" }`, or a
read-only `{ "path": "relative/file.txt" }` beneath an explicitly configured
`--file-root`. Outside-root paths and symbolic-link traversal are rejected.
Without `--file-root`, path input is disabled. This is not a filesystem sandbox
against other trusted code running in the same process.

Stream outputs are handles. `read_stream` reads from the **current live position**:

```json
{ "handle": "h_stream", "maxBytes": 8192 }
```

It returns base64, `bytesRead`, and `eof`; the maximum read is 65,536 bytes. Inputs
created for a call are disposed afterward unless their ownership transfers into
a returned handle.

## Pipelines: preserve identity, not just JSON

Call `pipeline` with ordered steps, using `$step` to reference an earlier **live**
result. This fixture example is also an executable integration-test case:

```json
{
  "steps": [
    { "id": "widget", "method": "Demo_MakeWidget", "arguments": { "name": "sample" } },
    { "id": "token", "method": "Widget_ToToken", "arguments": { "target": { "$step": "widget" } } },
    { "id": "receipt", "method": "Token_Finish", "arguments": { "target": { "$step": "token" } } },
    { "id": "read", "method": "Receipt_Read", "arguments": { "target": { "$step": "receipt" } } }
  ]
}
```

Use `path` for a projection, or `output` to select a prior ref/out parameter:
`{ "$step": "split", "output": "number" }`. References cannot point forward.
IDs must be unique, 1–64 letters/digits/underscores/hyphens; at most 32 steps.

Add `"dryRun": true` to validate input shapes, handle assignability, generic
constraints and static type flow **without invoking target methods, constructors
or getters during that request**. It cannot predict runtime values, collection
bounds, side effects or user-code failures. Declared return types may be less
specific than the types a real call would produce. Load-time DTO tests are separate.

Syntax/method identifiers are validated before execution. During execution, the
first binding/invocation error stops the pipeline. Completed steps and their call
IDs remain in `steps`; `failedStep` identifies the failure. **No side effects are
rolled back.** Handle values from completed steps remain available until normal
lifetime rules remove them.

## Ask how to obtain a missing type

```json
{ "type": "Fixtures.Receipt", "maxDepth": 3 }
```

`how_to_get` runs a bounded breadth-first search and returns up to three ready-to-
run `pipeline` calls, starting with synthetic literal values. No target methods
are invoked by the planner. It follows assignable return/ref/out types and
instance targets. Review the generated sample values and side effects, optionally
dry-run, then execute the supplied `arguments`.

Generic methods requiring caller-chosen type arguments are not automatically
instantiated by this planner. The search is bounded; `truncated` or an empty plan
list does not prove a type is unobtainable. `type_flow` and `describe_type` expose
the top three producers/consumers to help build a custom pipeline.

## Errors, exclusions and coverage

Recognized tools return errors as **both** MCP `isError: true` and a JSON envelope
with `callId`, code, message, argument path, details, binding hints, and a filtered
target-DLL stack. Reflection wrapper exceptions are unwrapped. Text content and
`structuredContent` carry the same JSON.

Pointers, function pointers, ref structs/Span, TypedReference, IntPtr/UIntPtr,
byref returns, native entry points, open generic declaring types, policy-denied
members and unsupported special/lifecycle members have explicit exclusion reasons.
Do not infer absence from the individually advertised tools alone: consult
`describe_type` and `coverage`.

`coverage {}` reports `total = literal + handle + unsupported`, exclusions by
reason, DTO demotions and loader diagnostics. `complete: false` flags discovery
problems; counts cover the public members successfully enumerated, not an
unverified promise about missing dependencies. You can also run:

```sh
dotnet introspectMCP.dll /path/Library.dll --extended --inspect
```

## Configuration

Run `--help` for defaults and ranges. Key controls:

| Option | Default | Meaning |
| --- | --- | --- |
| `--tool-budget` | 100 | Above this count, advertise the compact dispatcher/catalog |
| `--schema-budget` | 24000 | Approximate individual-tool schema/description tokens (characters ÷ 4) |
| `--page-size` | 40 | Sequence items per page; range 1–200 |
| `--max-handles` | 256 | Live values **and cursors**; range 4–10,000 |
| `--handle-ttl` | 900 | Idle seconds; active leases are protected |
| `--file-root` | disabled | Opt in to read-only Stream path inputs |
| `--allow` / `--deny` | built-in deny rules | Repeatable, case-insensitive member globs; deny wins |

Globs match `Namespace.Type.Method` (constructors use `Namespace.Type..ctor`).
Built-in denies cover Process, Environment, reflection/interop entry points,
File/Directory, networking namespaces and Microsoft.Win32. They cannot detect
indirect calls made inside an allowed method. There is no expression evaluator,
new network transport, native DLL introspection, or privilege boundary here.

See [implementation status](implementation-status.md) for validation evidence and
remaining limitations rather than assuming an unbounded feature set.
