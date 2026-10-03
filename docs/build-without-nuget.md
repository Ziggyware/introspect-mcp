# Building when the SDK and NuGet download hosts are unavailable

The normal project still targets **`net8.0-windows10.0.26100.0`** and still references
`Microsoft.Extensions.Hosting 10.0.10` and `ModelContextProtocol 1.4.1`. None of that
has been changed to make the development fallback pass.

A restricted Linux x64 workspace can instead compile and exercise the repository
sources using a verified .NET 10 SDK and source-built MCP dependencies, without
retargeting the shipping project. This is useful build access, but **not a
successful restore/build of the shipping .NET 8/Windows project**. The bootstrap
script runs legacy smoke tests; `scripts/test.sh` also runs the extended suite.

## Run it

Prerequisites: Linux x64, bash, git, Python 3, tar, sha512sum, and the usual .NET
runtime native prerequisites (including ICU). The first run needs GitHub Git
access; it does not need NuGet, Microsoft's SDK download hosts, npm, or sudo.

```bash
./scripts/build-without-nuget.sh

# Subsequent runs: reject missing caches instead of downloading anything.
./scripts/build-without-nuget.sh --offline

# Full regression gate: bootstrap/legacy smoke, then extended integration tests.
./scripts/test.sh --offline
```

The script:

1. Fetches a commit-pinned GitHub mirror containing split SDK archive files.
2. Reassembles the **unmodified** `dotnet-sdk-10.0.400-linux-x64.tar.gz` and verifies
   its SHA-512 against Microsoft's published digest **before executing it**.
3. Fetches pinned official source revisions for MCP **1.4.1** and
   `Microsoft.Extensions.AI.Abstractions` **10.5.2**, including the MCP analyzer.
   It refuses modified tracked source files or unexpected revisions.
4. Generates separate `net10.0` projects outside the checkout. Those projects
   compile the upstream source without editing it, and link this repository's
   root and Extended C# files without changing them. Roslyn and the shared-framework assemblies
   come from the verified SDK. No fake `.nupkg` files are created.
5. Builds with warnings as errors and a NuGet configuration that clears **all**
   package sources. NuGet audit is disabled for this isolated offline build;
   this is not a vulnerability audit.
6. Runs an actual MCP C# SDK client against a service subprocess over its existing
   stdio transport, with a 30-second test deadline and restricted child environment.

Default locations:

| Content | Location | Override |
| --- | --- | --- |
| SDK installation | `$HOME/.local/share/dotnet` | `INTROSPECT_DOTNET_ROOT` |
| Sources, archive, projects, binaries | `${XDG_CACHE_HOME:-$HOME/.cache}/introspect-mcp` | `INTROSPECT_BUILD_CACHE` |

Allow approximately 1.3 GB of disk space. No toolchains or dependency binaries are
checked into this repository. `.cs.in` smoke-test sources are explicitly compiled
by the generated projects, and do not enter the shipping project's C# file glob.
The script prints a command for running the resulting service with a DLL path.
To use the installed CLI directly, add `$HOME/.local/share/dotnet` to your shell
`PATH` (or the chosen `INTROSPECT_DOTNET_ROOT`).

## Verified baseline

Both a fresh-source bootstrap and the cached/offline path were run successfully
on Debian 12 x64:

- SDK **10.0.400**, runtime/shared framework **10.0.11**.
- **Build succeeded: 0 warnings, 0 errors.**
- Real MCP `initialize` and `tools/list`; all seven fixture tools discovered.
- Scalar input and Encoding adapter schemas asserted.
- Six successful `tools/call` checks: integer addition, string echo, asynchronous
  addition, Encoding input, CultureInfo input, Encoding return adaptation.
- A seventh call throws inside the fixture and is asserted to return an MCP
  `isError` result, not a client exception.

Those seven calls guard backward compatibility. The full `scripts/test.sh` gate
additionally builds `tests/Fixtures` and `tests/IntegrationTests`, then executes
**539 assertions** covering classifiers, live handles, pipelines, discovery and
edge cases. See [implementation status](implementation-status.md) for the tested
stages and [the usage guide](handles.md) for examples.

## Important differences from the shipping build

| Shipping project | Isolated fallback |
| --- | --- |
| .NET 8 / Windows, Windows Forms enabled | .NET 10 / Linux, no WindowsDesktop framework |
| NuGet `ModelContextProtocol 1.4.1` package | Same tagged source revision, locally compiled |
| NuGet `Microsoft.Extensions.Hosting 10.0.10` | Hosting **10.0.11** from the installed ASP.NET Core shared framework |
| NuGet's transitive dependency graph | Source-built AI abstractions plus installed shared-framework assemblies |
| Normal upstream packaging, signing and analyzers | Isolated projects; upstream MCP analyzer built with SDK Roslyn |

Do not ship this artifact as though it were the original Windows build. DLLs that
require Windows Forms/WPF or other Windows-only dependencies still require the
Windows build/runtime. Success here does not close the .NET 8/Windows verification
gate. Extended phases were compiled and tested incrementally on this explicitly
identified fallback, not on the original shipping target. A separate attempt to restore `introspectMCP.csproj` with the installed SDK still failed with
`NU1301`: the connection to `https://api.nuget.org/v3/index.json` ended during TLS.

## Provenance

- SDK mirror revision:
  `andschir/dotnet-sdk-10.0.400-linux-x64@f4ca2352ed0e871c3adb118cf22ed86f7ff20c4e`.
- Expected SDK SHA-512:
  `1033977dd837150e0814cf0c5d5b17ceb63925fda7ba2158b47258a4bd7c048cf82eac3bc1166f3146f53124a3f5fba09db1de1260d2ce96399860303b404b48`.
- Independent digest source: Microsoft's
  [release-notes/10.0/releases.json](https://github.com/dotnet/core/blob/main/release-notes/10.0/releases.json),
  SDK **10.0.400**, RID **linux-x64**.
- MCP official tag **v1.4.1**:
  `modelcontextprotocol/csharp-sdk@2b7fd35fbe58dfb9f00eae8b3393e1a7361b5e01`.
- AI abstractions official tag **v10.5.2**:
  `dotnet/extensions@2a86d759c251eee39274c191bd9f8e14c58f875a`.

The upstream licenses remain in the cached source checkouts and SDK installation.

## Other build route

A GitHub Actions workflow on the session branch can cross-build the original
Windows-targeted project using an official .NET 8 SDK, and optionally export its
Linux SDK and restored NuGet cache for local use. That route was attempted, but
GitHub blocked the job before runner startup. It has not produced a successful
original-target build. The workflow is manual-only to avoid repeated failed runs
and automatic large toolchain uploads on ordinary pushes.
