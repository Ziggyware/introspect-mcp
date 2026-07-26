# introspectMCP

Reflects over a compiled .NET assembly (`filename.dll`), finds every public
static method on a `*Util`-suffixed class, and exposes each one as an MCP
tool over stdio — without hand-writing a wrapper per method. Built as a
`net8.0-windows10.0.26100.0` console app that hosts
`ModelContextProtocol.Server`.

```
filename.dll  --reflect-->  MethodInfo[]  --adapt/filter-->  McpServerTool[]  --stdio-->  MCP client
```

## Why this exists

`filename.dll` was never written with MCP in mind — its methods take and return
plain CLR types (`Encoding`, `CultureInfo`, `StreamWriter`, `object`,
`byte[]`, etc.), not the JSON-serializable surface an MCP tool call expects.
Three problems fall out of that gap, and this project is organized as three
components that solve them independently:

1. **Some CLR types have no honest JSON representation** (open file handles,
   `object`). These must be *excluded*, not coerced.
2. **Some CLR types have no honest JSON representation but a reasonable
   string one** (`Encoding`, `CultureInfo`). These must be *adapted* —
   parsed from a string parameter, serialized to a string return value.
3. **Everything else** can be passed through to the MCP SDK's own
   `McpServerTool.Create(MethodInfo, ...)`, which handles ordinary
   primitives, strings, and simple POCOs on its own.

## Architecture

```
Program.cs
  Main()
    -> BuildInvocableToolsFromDll(path)
         -> Assembly.LoadFrom(path)              [see: Assembly loading]
         -> BuildInvocableToolsFromAssembly(asm)
              -> reflect *Util types + their public static methods
              -> sort deterministically            [see: Tool ordering]
              -> for each (type, method):
                   -> UnrepresentableTypeRegistry.MethodIsUnrepresentable?
                        yes -> skip, log, continue
                   -> ToolNaming.AssignNames(...)   [see: Tool naming]
                   -> AdaptationDecision.NeedsAdaptation(method)?
                        yes -> AdapterBuilder.BuildCallableDelegate(method)
                               -> McpServerTool.Create(delegate, options)
                        no  -> McpServerTool.Create(method, null, options)
         -> tools.ToList()
    -> Host.CreateEmptyApplicationBuilder()
         .AddMcpServer().WithStdioServerTransport().WithTools(tools)
    -> RunAsync()   [blocks on stdio; no output is expected/correct]
```

Every stage that can fail on a *specific* method (bad signature, unknown
type, illegal tool name) catches locally and logs to stderr with `[skip]` —
one bad method must never abort the whole assembly's tool load.

---

## Component 1 — `UnrepresentableTypeRegistry`

**Purpose:** exclude methods whose parameter or return type has no valid
JSON Schema representation, *before* the MCP SDK's schema generator ever
sees them.

```csharp
public static class UnrepresentableTypeRegistry
{
    private static readonly Type[] BlockedByAssignability =
    {
        typeof(Stream), typeof(TextWriter), typeof(TextReader),
        typeof(System.Runtime.InteropServices.SafeHandle),
    };

    public static bool IsUnrepresentable(Type t) =>
        t == typeof(object) ||
        BlockedByAssignability.Any(b => b.IsAssignableFrom(t));

    public static bool MethodIsUnrepresentable(MethodInfo m) =>
        IsUnrepresentable(m.ReturnType) ||
        m.GetParameters().Any(p => IsUnrepresentable(p.ParameterType));
}
```

### What's blocked, and why

| Category | Types | Reason |
|---|---|---|
| Open resource handles | `Stream`, `TextWriter`, `TextReader`, `SafeHandle` | A returned handle is owned by nobody once the tool call completes — no JSON shape preserves "open file descriptor," and the underlying resource leaks. |
| Fully unconstrained type | `object` | No JSON Schema describes "any possible CLR value." The schema generator either emits an empty node or fails outright. |

### The `object` bug (fixed, documented so it doesn't regress)

`object` **cannot** be added to `BlockedByAssignability` and checked via
`IsAssignableFrom`. Every reference type is assignable to `object`
(`typeof(object).IsAssignableFrom(typeof(string))` is `true`), so doing that
silently blocklists *every* method in the assembly — this happened once and
produced `Registered 0 tools`. `object` is checked by exact equality
(`t == typeof(object)`), entirely separate from the assignability scan.

### Consuming code must check *before* calling `McpServerTool.Create`

`Assembly.GetTypes()` can succeed while an individual method's
`GetParameters()`/return-type resolution still throws
(`ReflectionTypeLoadException` variants, missing native/managed deps for a
specific signature). `UnrepresentableTypeRegistry` only excludes *known*
bad shapes; anything else that throws during `McpServerTool.Create` is
still caught and skipped one level up in `BuildInvocableToolsFromAssembly`'s
per-method `try/catch` — the registry and the catch block are complementary,
not redundant.

---

## Component 2 — `ParameterAdapterRegistry` / `ReturnAdapterRegistry`

**Purpose:** for CLR types that *do* have a reasonable string
representation, adapt them — parse a string argument into the real type on
the way in, serialize the real return value to a string on the way out —
instead of excluding the method entirely.

```csharp
public static class ParameterAdapterRegistry
{
    private static readonly List<(Type BaseType, Func<string, object> Parse, Func<object> Fallback)> Adapters = new()
    {
        (typeof(Encoding),    s => Encoding.GetEncoding(s),        () => Encoding.UTF8),
        (typeof(CultureInfo), s => CultureInfo.GetCultureInfo(s),  () => CultureInfo.InvariantCulture),
    };

    private static readonly Dictionary<Type, (Func<string,object>, Func<object>)?> Cache = new();

    public static bool NeedsAdapter(Type t) => Resolve(t) != null;

    public static object Adapt(Type t, string? value)
    {
        var entry = Resolve(t) ?? throw new InvalidOperationException($"No parameter adapter for '{t}'.");
        return string.IsNullOrEmpty(value) ? entry.Fallback() : entry.Parse(value);
    }

    private static (Func<string,object>, Func<object>)? Resolve(Type t)
    {
        if (Cache.TryGetValue(t, out var cached)) return cached;
        var match = Adapters.Where(a => a.BaseType.IsAssignableFrom(t))
                             .Select(a => ((Func<string,object>, Func<object>)?)(a.Parse, a.Fallback))
                             .FirstOrDefault();
        Cache[t] = match;
        return match;
    }
}
```

`ReturnAdapterRegistry` mirrors this shape for outputs (`Func<object,object>`
instead of `Func<string,object>`, no fallback since a return value is never
"absent").

### Design points

- **Match by assignability, not exact type.** `Encoding` is abstract — no
  BCL API ever hands back a bare `Encoding` instance; you get
  `UTF8Encoding`, `ASCIIEncoding`, etc. An exact `Dictionary<Type,...>`
  lookup keyed on `typeof(Encoding)` never matches a concrete parameter
  type and silently falls through to "no adapter needed," which then fails
  downstream when the SDK tries to schema-generate the concrete subtype
  directly (e.g. `UTF8Encoding.Preamble` is a `ReadOnlySpan<byte>`, which
  the serializer rejects). Matching via `BaseType.IsAssignableFrom(t)`
  fixes this.
- **Null/empty input uses `Fallback()`, not an exception.** A client
  omitting an optional `Encoding`/`CultureInfo` argument should get a
  sane default (`UTF8`, `InvariantCulture`), not `Encoding.GetEncoding(null)`
  throwing `ArgumentNullException`. A genuinely bad value (`"bogus-8"`)
  still throws from `Parse` — that's a real client error, not an absence.
- **Per-type result caching.** `Resolve` is an `O(n)` scan over
  `Adapters`; caching by `Type` means each distinct type is scanned once
  across the whole tool-registration pass, not once per method.
- **Extending the registry** means adding one `(BaseType, Parse, Fallback)`
  tuple. No other code changes — `AdaptationDecision.NeedsAdaptation` and
  `AdapterBuilder`'s IL emission are both driven purely off
  `NeedsAdapter(Type)`.

### Known limitation

There is no per-parameter override — `Adapt(Type, string)` only sees the
*type*, not the `ParameterInfo` or `MethodInfo` it came from. If a specific
method needs a fallback different from the type-wide default (e.g. one
method's implicit "no encoding given" meaning is "use the file's declared
encoding," not `UTF8`), that can't be expressed here. Would require
threading `ParameterInfo` through `Adapt` and baking a
`(Type, ParameterInfo) -> value` lookup into `AdapterBuilder`'s IL emission
instead of `(Type) -> value`. Not built speculatively — add only if a
concrete method demonstrates the need.

---

## Component 3 — `AdapterBuilder` (IL generation)

**Purpose:** produce a `Delegate` with a *different* signature than the
original `MethodInfo` — adapted parameter/return types swapped for
`string` — by emitting a small trampoline method at runtime via
`System.Reflection.Emit`.

```
original:  Encoding GetStreamEncoding(string path, Encoding fallback)
exposed:   string   GetStreamEncoding(string path, string fallback)
```

### Why IL emission, not `Expression<T>` or reflection `Invoke`

- `MethodInfo.CreateDelegate` requires the delegate's signature to match
  the method's signature exactly — can't be used when the exposed schema
  needs `string` where the real method wants `Encoding`.
  `Expression.Lambda` + boxed calls work but pay a delegate-of-delegate
  indirection and don't compose cleanly with `DynamicMethod.DefineParameter`
  (needed so the MCP SDK's schema generator sees real parameter names, not
  `arg0`/`arg1`).
- `DynamicMethod` with `skipVisibility: true` lets the trampoline call
  private/internal members if needed and avoids a full assembly-save round
  trip — the method exists only in memory, JIT-compiled once, then invoked
  like any other delegate for the lifetime of the process.

### Fast path vs. adapted path

```csharp
if (!needsParamAdaptation && !needsReturnAdaptation)
    return method.CreateDelegate(Expression.GetDelegateType(...));   // zero IL emitted
```

Only methods that actually need adaptation pay for `DynamicMethod`
construction. `AdaptationDecision.NeedsAdaptation` is the single gate that
decides fast-path vs. IL-emission-path, checked once per method up front.

### IL emission shape (parameters)

For each parameter needing adaptation:

```
ldtoken   <ParameterType>          ; push RuntimeTypeHandle
call      Type.GetTypeFromHandle   ; -> Type
ldarg     i                        ; push the string arg
call      ParameterAdapterRegistry.Adapt(Type, string) -> object
unbox_any <ParameterType>          ; unbox back to the real value type
```

For untouched parameters: `ldarg i` and nothing else.

Then: `call <original method>`.

### IL emission shape (return, when adapted)

```
box       <ReturnType>             ; only if ReturnType.IsValueType
stloc     boxedLocal                ; box the real return value FIRST —
                                     ; Adapt(Type, object) needs (type, value)
                                     ; in that stack order, not (value, type)
ldtoken   <ReturnType>
call      Type.GetTypeFromHandle
ldloc     boxedLocal
call      ReturnAdapterRegistry.Adapt(Type, object) -> object
castclass string
```

The box-into-a-local step matters: if you push the boxed value directly and
then try to load the type token, the stack order comes out wrong for a
two-argument `Adapt(Type, object)` call. Storing to a local first
guarantees the two arguments land on the stack in the order the method
signature expects.

### Known constraint

`ref`/`out` parameters throw `NotSupportedException` explicitly at the top
of `BuildCallableDelegate` — MCP tool calls are request/response, there's no
channel for a "returned via out parameter" value to travel back through a
single JSON result. Any dll method with a `ref`/`out` parameter is
therefore adapted-out entirely (caught by the surrounding `try/catch` in
`BuildInvocableToolsFromAssembly`, logged as `[skip]`), not silently
mishandled.

---

## Component 4 — `ToolNaming`

**Purpose:** produce a name for each `(Type, MethodInfo)` pair that satisfies
the MCP spec's tool-name constraint:

```
^[A-Za-z0-9_.-]{1,128}\z
```

### Why this needed a fix

`Type.Name` for `byte[]` is literally `"Byte[]"`; for `List<int>` it's
`` "List`1" ``. Naively interpolating parameter type names into a
disambiguated overload name (`ConvertUtil_ToBase64_Byte[]_Boolean`)
produces `[`, `]`, and `` ` `` — all illegal per the regex above, and the
MCP client rejects the tool list wholesale (`"Invalid input"` at
registration, not at call time).

### Fix shape

Sanitize each name *component* before joining — not the joined string
after the fact, since a legitimate `_` separator between components is
indistinguishable from a sanitized illegal character once concatenated:

```csharp
private static string SanitizeToolNameSegment(string raw)
{
    var sb = new StringBuilder(raw.Length);
    foreach (var c in raw)
        sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '_');
    var result = Regex.Replace(sb.ToString(), "_{2,}", "_").Trim('_');
    return result.Length == 0 ? "Tool" : result;
}
```

Applied independently to type name, method name, and each parameter type
name, then joined with `_`. Collision handling (two overloads sanitizing to
the same string) is the existing numbered-suffix dedup logic in
`AssignNames`, run *after* sanitization — dedup before sanitization would
miss collisions that sanitization itself introduces.

### Tool ordering

`Assembly.GetTypes()` / `Type.GetMethods()` order is metadata-table order —
not documented, not guaranteed stable across builds, and not alphabetical.
`BuildInvocableToolsFromAssembly` imposes an explicit sort after collecting
candidates, before naming:

```csharp
.OrderBy(tm => tm.Type.Name, StringComparer.Ordinal)
.ThenBy(tm => tm.Method.Name, StringComparer.Ordinal)
.ThenBy(tm => tm.Method.GetParameters().Length)
```

This makes tool list output deterministic and reproducible run-to-run —
important both for diffing `[skip]` logs across builds and for any client
that caches tool order.

---

## Assembly loading — dependency resolution

`Assembly.LoadFrom(dllPath)` on modern .NET (Core/5+) does **not**
probe the loaded assembly's own directory for its dependencies the way
.NET Framework did. `filename.dll`'s own NuGet dependencies
(`HtmlAgilityPack`, `System.Data.SqlClient`, etc.) sit next to it on disk
but are invisible to the default `AssemblyLoadContext` unless explicitly
wired up.

```csharp
private static void RegisterDependencyResolution(string dllPath)
{
    var resolver = new AssemblyDependencyResolver(dllPath);
    var dllDir = Path.GetDirectoryName(dllPath)!;

    AssemblyLoadContext.Default.Resolving += (ctx, name) =>
    {
        var resolved = resolver.ResolveAssemblyToPath(name);
        if (resolved != null) return ctx.LoadFromAssemblyPath(resolved);

        // Fallback for anything missing from filename.dll.deps.json (stale
        // manifest, excluded package) — best-effort, no version check.
        var probePath = Path.Combine(dllDir, name.Name + ".dll");
        return File.Exists(probePath) ? ctx.LoadFromAssemblyPath(probePath) : null;
    };

    AssemblyLoadContext.Default.ResolvingUnmanagedDll += (assembly, unmanagedName) =>
    {
        var path = resolver.ResolveUnmanagedDllToPath(unmanagedName);
        if (path != null) return NativeLibrary.Load(path);
        var direct = Path.Combine(dllDir, unmanagedName);
        return File.Exists(direct) ? NativeLibrary.Load(direct) : IntPtr.Zero;
    };
}
```

Register **before** `Assembly.LoadFrom(dllPath)` runs.

- **Primary path**: `AssemblyDependencyResolver` reads `filename.dll.deps.json`
  (emitted next to `filename.dll` at build time) and resolves the exact
  versions filename.dll was built against, including transitive dependencies.
  Requires `<GenerateDependencyFile>true</GenerateDependencyFile>` in
  filename.dll's `.csproj` if the file isn't present.
- **Fallback path**: flat directory probe next to `filename.dll`, no version
  check. Covers dependencies missing from `deps.json` for any reason.
  Does **not** probe `runtimes/<rid>/native/` subfolders — packages that
  ship RID-specific native assets (e.g. legacy `System.Data.SqlClient`'s
  `SNI.dll`) need that path added explicitly if/when they surface a
  `DllNotFoundException` at call time, not load time.
- `ResolvingUnmanagedDll` is registered unconditionally; it's a no-op if
  nothing in filename.dll P/Invokes into native code, but present in case a
  managed-looking dependency (`System.Drawing.Common` on Windows, via GDI+)
  turns out to need it.

### Windows Desktop shared framework (`System.Windows.Forms`, `System.Drawing.Common`)

These are **not resolvable by any dependency-resolver trick** — they live
in the `Microsoft.WindowsDesktop.App` shared framework, which is only
present in the introspector's own output if the introspector's own
`.csproj` targets a `-windows` TFM:

```xml
<TargetFramework>net8.0-windows10.0.26100.0</TargetFramework>
<UseWindowsForms>true</UseWindowsForms>
```

Without this, any filename.dll method whose signature references
`System.Windows.Forms.*` throws
`FileNotFoundException: ... System.Windows.Forms ...` — not from
`Assembly.LoadFrom` itself, but later, whenever reflection first needs to
resolve that method's full signature (`GetParameters()`,
`GetTypes()`/`ReflectionTypeLoadException`, or `McpServerTool.Create`
internally calling either). The TFM must match — or exceed — whatever
`filename.dll` itself was compiled against.

**Stale-build trap:** changing the TFM changes the build output directory
(`bin\Debug\net8.0\...` → `bin\Debug\net8.0-windows10.0.26100.0\...`). The
old folder isn't deleted automatically. Anything invoking the exe by a
hardcoded path (a shell alias, an MCP client's server config `command`)
will keep silently running the stale pre-fix binary. Always confirm the
actual output path after a TFM change:

```powershell
Remove-Item -Recurse -Force bin, obj
dotnet build
Get-ChildItem bin\Debug -Directory
```

---

## Failure-mode catalogue

Quick reference for symptoms encountered building this out, mapped to root
cause and fix location:

| Symptom | Root cause | Fixed in |
|---|---|---|
| `ReflectionTypeLoadException` on `asm.GetTypes()` | A referenced type (e.g. from `System.Windows.Forms`) can't be resolved by the hosting process's runtime. | `catch (ReflectionTypeLoadException) { types = ex.Types.Where(t => t != null) }`, plus fixing the TFM so the types resolve in the first place. |
| `FileNotFoundException: System.Windows.Forms ...` even after the catch above | `GetParameters()` on an *individual method* throws separately from `GetTypes()` — type-load success doesn't guarantee per-member signature resolution. | Per-method `try/catch` around `t.GetMethods()` and each `GetParameters()` call before building the candidate list. |
| `FileNotFoundException: HtmlAgilityPack ...` | `Assembly.LoadFrom` doesn't probe the loaded DLL's own directory for dependencies on modern .NET. | `AssemblyDependencyResolver` + directory-probe fallback (see above). |
| `InvalidOperationException: ReadOnlySpan<Byte> ... invalid for serialization` | A concrete `Encoding` subtype (`UTF8Encoding`) leaked through to the MCP SDK's schema generator because the adapter registry matched by exact `Type` equality instead of assignability. | `ParameterAdapterRegistry`/`ReturnAdapterRegistry` matching via `BaseType.IsAssignableFrom(t)`. |
| `Registered 0 tools` | `object` added to the assignability-checked blocklist; `IsAssignableFrom` matches every reference type against `object`. | Exact-equality check for `object`, separate from the assignability scan. |
| `Invalid input` at `tools/list`, path `inputSchema.properties.o` etc. | Methods taking a plain `object` parameter — no JSON Schema constrains "any CLR value." | `UnrepresentableTypeRegistry` excludes `object`-typed parameters/returns. |
| `MCP error -32601: Method 'tools/list' is not available` | Symptom of an empty tool list (see `Registered 0 tools` above) or the server process exiting/crashing before `RunAsync()` starts. | Add `[info] Registered N tools` diagnostic before `RunAsync()`; check exit code and stderr first when this appears. |
| `The tool name '...' is invalid` (regex mismatch) | Array (`byte[]`) / generic (`` List`1 ``) type names leaking `[`, `]`, `` ` `` into the tool name string. | `ToolNaming` per-component sanitization. |
| Tools listed in different order between runs / not matching source order | `GetTypes()`/`GetMethods()` order is undocumented metadata-table order. | Explicit `.OrderBy(...).ThenBy(...)` in `BuildInvocableToolsFromAssembly`. |
| Correct build, correct code, error persists from command line only (works in debugger) | Stale binary in an old TFM's output folder still being invoked by a hardcoded path. | Delete `bin`/`obj`, rebuild, confirm the actual output path, update whatever launches the exe. |

---

## Extending this

- **New adaptable type** (has a sane string round-trip, no honest JSON
  shape): add one `(BaseType, Parse, Fallback)` tuple to
  `ParameterAdapterRegistry.Adapters`, and a `(BaseType, Adapt)` pair to
  `ReturnAdapterRegistry.Adapters` if it can also appear as a return type.
  No other code changes.
- **New unrepresentable type** (no JSON shape at all, should be excluded):
  add to `UnrepresentableTypeRegistry.BlockedByAssignability` — **do not**
  add `object` or any other supertype-of-everything this way; it must stay
  the sole exact-equality special case.
- **New DLL to introspect**: pass its path as `arg[0]`. Confirm its own TFM against the introspector's TFM if it
  references Windows Desktop types, and confirm its `.deps.json` exists
  and is current if it has third-party NuGet dependencies.
