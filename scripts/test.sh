#!/usr/bin/env bash
# Reproducible regression gate. First run can bootstrap from GitHub, then use --offline.
set -euo pipefail
if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
  printf 'Usage: %s [--offline]\n\nBuild and run legacy plus extended MCP regression tests on the pinned .NET 10/Linux\nsource-build fallback. --offline requires cached sources/toolchain. This is not\nthe shipping .NET 8/Windows gate. See docs/build-without-nuget.md.\n' "$0"
  exit 0
fi
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
"$root/scripts/build-without-nuget.sh" "$@"
cache="${INTROSPECT_BUILD_CACHE:-${XDG_CACHE_HOME:-$HOME/.cache}/introspect-mcp}"
sdk="${INTROSPECT_DOTNET_ROOT:-$HOME/.local/share/dotnet}"
export DOTNET_ROOT="$sdk" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export PATH="$sdk:$PATH"
build="$cache/source-build"
cd "$build"
dotnet build IntegrationTests/IntegrationTests.csproj --configuration Release --nologo --disable-build-servers \
  -p:UseSharedCompilation=false -p:RestoreConfigFile="$build/NuGet.Config"
dotnet IntegrationTests/bin/Release/net10.0/IntegrationTests.dll \
  "$sdk/dotnet" "$build/Introspect/bin/Release/net10.0/introspectMCP.dll" \
  "$build/ExtendedFixtures/bin/Release/net10.0/Fixtures.dll" 3
printf '\nAll local regression gates passed. Shipping .NET 8/Windows verification is separate.\n'
