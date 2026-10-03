#!/usr/bin/env python3
"""Generate isolated projects from pinned upstream sources, without editing them.

This intentionally targets net10.0 and consumes the installed shared framework.
It is a development fallback, not an emulation of the original NuGet packages.
"""
from pathlib import Path
import sys
from xml.sax.saxutils import escape

repo, cache = (Path(p).resolve() for p in sys.argv[1:])
build = cache / "source-build"
mcp = cache / "mcp-sdk" / "src"
extensions = cache / "extensions" / "src"


def write(path: str, content: str) -> None:
    target = build / path
    target.parent.mkdir(parents=True, exist_ok=True)
    if not target.exists() or target.read_text(encoding="utf-8") != content + "\n":
        target.write_text(content + "\n", encoding="utf-8")


def source(path: Path, link: str | None = None) -> str:
    attr = f' Link="{link}"' if link else ""
    return f'<Compile Include="{escape(str(path), {chr(34): "&quot;"})}"{attr} />'


def project(name: str, assembly: str, items: list[str], properties: str = "") -> None:
    write(f"{name}/{name}.csproj", f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>{assembly}</AssemblyName>
    {properties}
  </PropertyGroup>
  <ItemGroup>
    {chr(10).join(items)}
  </ItemGroup>
</Project>''')


write("global.json", '{"sdk":{"version":"10.0.400","rollForward":"disable"}}')
write("NuGet.Config", '''<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /></packageSources>
</configuration>''')
write("Directory.Build.props", '''<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>preview</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <IsPackable>false</IsPackable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <NuGetAudit>false</NuGetAudit>
    <NoWarn>$(NoWarn);MCPEXP001;MEAI001</NoWarn>
  </PropertyGroup>
</Project>''')

framework = '<FrameworkReference Include="Microsoft.AspNetCore.App" />'
project("AI", "Microsoft.Extensions.AI.Abstractions", [
    source(extensions / "Libraries/Microsoft.Extensions.AI.Abstractions/**/*.cs"),
    source(extensions / "Shared/Throw/**/*.cs"),
    source(extensions / "Shared/DiagnosticIds/**/*.cs"),
    source(extensions / "Shared/EmptyCollections/**/*.cs"),
    source(extensions / "LegacySupport/MediaTypeMap/**/*.cs"),
], "<Version>10.5.2</Version><ImplicitUsings>disable</ImplicitUsings>")

# Roslyn is already part of the SDK; the upstream analyzer is compiled unchanged.
project("Analyzers", "ModelContextProtocol.Analyzers", [
    source(mcp / "ModelContextProtocol.Analyzers/**/*.cs"),
    '<Reference Include="Microsoft.CodeAnalysis" HintPath="$(MSBuildToolsPath)/Roslyn/bincore/Microsoft.CodeAnalysis.dll" />',
    '<Reference Include="Microsoft.CodeAnalysis.CSharp" HintPath="$(MSBuildToolsPath)/Roslyn/bincore/Microsoft.CodeAnalysis.CSharp.dll" />',
])

common = [source(mcp / "Common" / p) for p in [
    "Throw.cs", "Obsoletions.cs", "Polyfills/**/*.cs",
]]
project("Core", "ModelContextProtocol.Core", [
    framework,
    '<ProjectReference Include="../AI/AI.csproj" />',
    '<ProjectReference Include="../Analyzers/Analyzers.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />',
    source(mcp / "ModelContextProtocol.Core/**/*.cs"),
    *common,
    *[source(mcp / "Common" / p) for p in [
        "Experimentals.cs", "EncodingUtilities.cs", "HttpResponseMessageExtensions.cs", "ServerSentEvents/**/*.cs",
    ]],
], "<Version>1.4.1</Version>")
project("MCP", "ModelContextProtocol", [
    framework,
    '<ProjectReference Include="../Core/Core.csproj" />',
    source(mcp / "ModelContextProtocol/**/*.cs"),
    *common,
], "<Version>1.4.1</Version><AllowUnsafeBlocks>true</AllowUnsafeBlocks>")
project("Introspect", "introspectMCP", [
    framework,
    '<ProjectReference Include="../MCP/MCP.csproj" />',
    source(repo / "*.cs"),
    source(repo / "Extended/**/*.cs"),
], "<OutputType>Exe</OutputType><ImplicitUsings>disable</ImplicitUsings>")
project("Fixtures", "BuildSmokeFixtures", [
    source(repo / "scripts/offline/SmokeFixtures.cs.in", "SmokeFixtures.cs"),
])
project("Client", "BuildSmokeClient", [
    '<ProjectReference Include="../MCP/MCP.csproj" />',
    '<ProjectReference Include="../Introspect/Introspect.csproj" ReferenceOutputAssembly="false" />',
    '<ProjectReference Include="../Fixtures/Fixtures.csproj" ReferenceOutputAssembly="false" />',
    source(repo / "scripts/offline/SmokeClient.cs.in", "Program.cs"),
], "<OutputType>Exe</OutputType>")
print(f"Generated isolated, package-free build projects at {build}")

project("ExtendedFixtures", "Fixtures", [framework, source(repo / "tests/Fixtures/*.cs")], "<AllowUnsafeBlocks>true</AllowUnsafeBlocks><GenerateDocumentationFile>true</GenerateDocumentationFile><NoWarn>$(NoWarn);1591</NoWarn>")
project("IntegrationTests", "IntegrationTests", [
    '<ProjectReference Include="../Introspect/Introspect.csproj" />',
    '<ProjectReference Include="../ExtendedFixtures/ExtendedFixtures.csproj" />',
    source(repo / "tests/IntegrationTests/*.cs"),
], "<OutputType>Exe</OutputType>")
