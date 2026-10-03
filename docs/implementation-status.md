# Implementation and verification status

## Compatibility and scope

- `--extended` enables the new engine. The legacy tool-discovery/adaptation code
  remains the default; existing tool names/schemas are not migrated in place.
- The original .NET 8/Windows target and package versions remain the default.
  `-p:PortableBuild=true` selects net8.0 without Windows Forms for managed-only use.
- MCP stdio remains the transport. No native DLL support, service network client,
  scripting endpoint, or expression execution has been added.
- Extended loading uses a collectible AssemblyLoadContext and
  AssemblyDependencyResolver, sharing framework/logging identities with the host.
  Unloading is requested after handle/host disposal; external references retained
  by arbitrary library code can still prevent collection.
- Loader diagnostics identify partial discovery, but some unresolved signature or
  dependency metadata can still stop startup with a structured stderr error.
  Missing dependencies are not emulated or promised to be fully catalogued.

## Tested stages

Every stage compiled cleanly and passed its local gate before the next stage was
added. Gates use the explicitly documented .NET 10/Linux source-build fallback,
not a claimed .NET 8/Windows validation.

| Stage | Evidence |
| --- | --- |
| 1 — classification/binding/invocation | 110 assertions through a real MCP SDK stdio client |
| 2 — live values/lifetimes/composition inputs | 286 cumulative assertions, including TTL/LRU, isolation, concurrent same-handle calls, delegate and stream chains |
| 3 — pipelines/discovery/planning | 358 cumulative assertions, including live DTO identity, dryRun, partial results, generated three-call plans and compact catalog |
| Additional edge cases | 539 cumulative assertions, including immutable records, temporal DTO normalization, generic ILogger, unmanaged constraints, function pointers/TypedReference exclusions, no-prefetch/cancellable async pages, projection edge cases, failed-export disposal and closure ownership |
| Legacy regression | All seven original-source smoke calls still pass |

Each integration call asserts MCP `isError`, a `callId`, and equivalence between
wire `structuredContent` and the JSON in text content. Fixtures cover object,
two interface implementations, nested DTOs/collections, reserved keys, ref/out/in,
Task/ValueTask, generic constraints, delegates, opaque Widget factory/consumer,
List<Widget>, cyclic and broken DTOs, IDisposable, streams and async sequences.

More than three live-handle chains are exercised, including:

1. Widget factory → instance token factory → receipt factory → read.
2. Counter constructor → concurrent mutations → property projection → disposal.
3. Returned delegate → callback consumer.
4. Stream factory → bounded read → another DLL consumer at the live position.

CLI smoke checks also verified clean `--inspect` JSON, `--help`, and a structured
missing-DLL startup error with exit code 2. Bash/Python syntax, JSON documentation
examples and whitespace checks passed.

The sequence code intentionally does not prefetch beyond a page: live async
sources must not hang a full page while awaiting an additional event.

Run everything:

```sh
./scripts/test.sh            # Bootstrap through GitHub if caches are absent
./scripts/test.sh --offline  # Repeat using cached verified tools/sources
```

The repository also contains a manual GitHub Actions gate for the original
Windows-target build and portable .NET 8 integration suite. That remote runner
route has not been verified here. The original project's NuGet restore previously
failed with NU1301/TLS EOF in this environment; hosted runner startup was also
blocked. **Shipping .NET 8/Windows and portable .NET 8 builds remain unverified.**

## Boundaries and deviations

These are intentional or unresolved limitations, not silently completed features:

- The tested runtime is .NET 10.0.11, SDK 10.0.400; MCP 1.4.1 and AI abstractions
  10.5.2 are compiled from pinned official sources. Hosting comes from the 10.0.11
  shared framework rather than the shipping 10.0.10 NuGet package. Normal shipping
  restore/package-graph validation is still required.
- Scalar Encoding/CultureInfo adapters preserve their semantic name, not private
  framework implementation types. Polymorphic tags use the adapter's public type.
- Input collections cover ordinary arrays and common generic collections. Exotic
  collection implementations may require handles rather than literal construction.
  Read-only/multidimensional and stateful values can still be reused by handle.
  Element-level nullable-reference annotations inside generic collections are not
  fully enforced; nullable value types and parameter/DTO-member nullability are.
- DTO inference is conservative and its load-time self-test is sample-based.
  Private state, arbitrary invariants and all possible values cannot be proven
  round-trippable. Accessor/constructor code executes during those self-tests.
- Generic **methods** are supported; open generic declaring types are explicitly
  unsupported. Compiler-only nullable constraints are not CLR runtime constraints.
- Cancellation is cooperative. Neither synchronous user code nor uncooperative
  async enumerators can be safely forcibly stopped in this in-process host.
- Progress is a bounded response buffer, not realtime MCP progress notifications;
  injected ILogger/ILogger<T> instances are no-op loggers. Server diagnostics and
  redirected Console.WriteLine output remain on stderr.
- Lifetime tombstones are bounded. Very old IDs lose their original reason and
  producing-call detail and report unknown/foreign instead. Resource disposal
  exceptions are logged and exposed by list_handles, rather than crashing cleanup.
- Locks protect explicitly referenced handle roots. Arbitrary aliasing inside a
  DLL, static state, getter side effects, or background tasks are not made safe by
  this mechanism. Likewise, retention of dependencies inside arbitrary opaque
  object graphs is not inferred; do not release resources still used by the DLL.
  Call-created streams escaping indirectly inside lazy/custom containers need
  explicit lifetime care. Release may report busy while a call/cursor/delegate retains a
  resource.
- The type-flow planner is bounded, uses declared types and synthetic literals,
  and does not automatically choose generic arguments. Generated calls are
  structurally executable, not a promise that target code will succeed or be
  side-effect-free. Pipelines are not transactions.
- Policy globs filter entry points, not indirect behavior. File-root checks and
  collectible loading are usability controls, not a sandbox for hostile DLLs.
- Optional MetadataLoadContext-only inspection, IL recipe mining, replay/affine
  handles, runtime type refinement and Roslyn `$expr` evaluation are **not
  implemented**. In particular, `--inspect` is not a no-code-execution sandbox.

No optional phase-four execution surface was added merely to increase feature
count. The next release gate is normal .NET 8 restore/build/test on a suitable
machine, followed by tests against the user's actual target DLL and dependency set.
