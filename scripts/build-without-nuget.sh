#!/usr/bin/env bash
# A source-build fallback for development, NOT validation of the shipping net8.0-windows target.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cache="${INTROSPECT_BUILD_CACHE:-${XDG_CACHE_HOME:-$HOME/.cache}/introspect-mcp}"
sdk_root="${INTROSPECT_DOTNET_ROOT:-$HOME/.local/share/dotnet}"
offline=false
case "${1:-}" in
  '') ;;
  --offline) offline=true ;;
  *) echo "Usage: $0 [--offline]" >&2; exit 2 ;;
esac
if [ "$#" -gt 1 ]; then echo "Usage: $0 [--offline]" >&2; exit 2; fi
if [ "$(uname -s)" != Linux ] || [ "$(uname -m)" != x86_64 ]; then
  echo "This fallback's verified SDK archive is for Linux x64 only." >&2
  exit 2
fi
for command in git python3 tar sha512sum; do command -v "$command" >/dev/null; done
mkdir -p "$cache" "$sdk_root"
cache="$(cd "$cache" && pwd)"
sdk_root="$(cd "$sdk_root" && pwd)"

# Microsoft publishes this SHA-512 in dotnet/core's release-notes/10.0/releases.json
# for dotnet-sdk-10.0.400-linux-x64.tar.gz. Never execute an unchecked mirror binary.
sdk_sha512=1033977dd837150e0814cf0c5d5b17ceb63925fda7ba2158b47258a4bd7c048cf82eac3bc1166f3146f53124a3f5fba09db1de1260d2ce96399860303b404b48

checkout() {
  local name="$1" url="$2" revision="$3" path="$cache/$1"
  if [ ! -d "$path/.git" ]; then
    if "$offline"; then echo "Missing cached source: $path (run once without --offline)." >&2; exit 1; fi
    if [ -e "$path" ]; then echo "Refusing to replace existing non-checkout: $path" >&2; exit 1; fi
    git init --quiet "$path"
    git -C "$path" remote add origin "$url"
    git -C "$path" fetch --quiet --depth 1 origin "$revision"
    git -c advice.detachedHead=false -C "$path" checkout --quiet --detach FETCH_HEAD
  fi
  if [ "$(git -C "$path" rev-parse HEAD)" != "$revision" ]; then
    echo "Cached $name is not at pinned revision $revision; use a fresh INTROSPECT_BUILD_CACHE." >&2
    exit 1
  fi
  if [ -n "$(git -C "$path" status --porcelain --untracked-files=no)" ]; then
    echo "Cached $name has modified tracked files; refusing an unverified source build." >&2
    exit 1
  fi
}

checkout sdk-chunks https://github.com/andschir/dotnet-sdk-10.0.400-linux-x64.git f4ca2352ed0e871c3adb118cf22ed86f7ff20c4e
archive="$cache/dotnet-sdk.tar.gz"
if [ ! -f "$archive" ]; then
  cat "$cache"/sdk-chunks/chunks/dotnet-sdk.part-* > "$archive.tmp"
  mv "$archive.tmp" "$archive"
fi
if ! printf '%s  %s\n' "$sdk_sha512" "$archive" | sha512sum --check --status; then
  echo "SDK SHA-512 mismatch; refusing to extract or execute the mirror archive." >&2
  exit 1
fi
marker="$sdk_root/.introspect-sdk-10.0.400.sha512"
if [ ! -x "$sdk_root/dotnet" ] || [ "$(cat "$marker" 2>/dev/null || true)" != "$sdk_sha512" ]; then
  tar --no-same-owner -xzf "$archive" -C "$sdk_root"
  printf '%s\n' "$sdk_sha512" > "$marker"
fi

checkout mcp-sdk https://github.com/modelcontextprotocol/csharp-sdk.git 2b7fd35fbe58dfb9f00eae8b3393e1a7361b5e01
checkout extensions https://github.com/dotnet/extensions.git 2a86d759c251eee39274c191bd9f8e14c58f875a

export DOTNET_ROOT="$sdk_root"
export PATH="$sdk_root:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1
export DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
build="$cache/source-build"
python3 "$repo_root/scripts/offline/create-projects.py" "$repo_root" "$cache"

# NuGet.Config clears all sources. This restores only project/framework references
# from the verified SDK, not repackaged DLLs masquerading as NuGet packages.
cd "$build"
dotnet --version
dotnet build Client/Client.csproj --configuration Release --nologo --disable-build-servers \
  -p:UseSharedCompilation=false -p:RestoreConfigFile="$build/NuGet.Config"
dotnet Client/bin/Release/net10.0/BuildSmokeClient.dll \
  "$sdk_root/dotnet" \
  "$build/Introspect/bin/Release/net10.0/introspectMCP.dll" \
  "$build/Fixtures/bin/Release/net10.0/BuildSmokeFixtures.dll"

printf '\nSource-build fallback passed (.NET 10/Linux; not the .NET 8/Windows gate).\n'
printf 'Run the service: %q %q /absolute/path/to/your.dll\n' \
  "$sdk_root/dotnet" "$build/Introspect/bin/Release/net10.0/introspectMCP.dll"
