#!/usr/bin/env bash
# Runs the harness sample (development plugins, ADR 0017). First the console sample (run-console-sample.sh) packs
# the sample plugins and installs them into artifacts/sample/plugin-root. Then:
#   1. Style 1, a harness project: builds samples/ConsoleHost.Harness, whose PluginUnderTest builds Sample.Greetings
#      first, and runs it: Sample.Greetings loads from its bin/ instead of the installed copy (with a warning),
#      next to the installed plugins.
#   2. Style 2, F5 on the plugin: `dotnet run` on samples/plugins/Sample.Time (a class library that sets
#      AlcoveHarnessExecutable) starts the harness with Sample.Time loaded from its build output.
# Like the console sample, it doesn't change your NuGet settings or packages.
#
# Usage: samples/run-harness-sample.sh [Debug|Release]
set -euo pipefail

configuration="${1:-Debug}"
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$repo/artifacts/sample"
log="$repo/artifacts/sample-console.log"

echo "== Console sample: packing the sample plugins and installing them into $work/plugin-root (log: $log)"
mkdir -p "$repo/artifacts"
"$repo/samples/run-console-sample.sh" "$configuration" > "$log"

version="$(dotnet msbuild "$repo/src/Alcove/Alcove.csproj" -getProperty:Version -p:Configuration="$configuration")"
export NUGET_PACKAGES="$work/nuget-packages"
properties=(-c "$configuration" -nologo -v quiet -p:AlcoveVersion="$version" -p:RestoreConfigFile="$work/nuget.config")

echo
echo "== Style 1: the harness project, with Sample.Greetings as its PluginUnderTest"
dotnet build "$repo/samples/ConsoleHost.Harness/ConsoleHost.Harness.csproj" "${properties[@]}"
dotnet "$repo/samples/ConsoleHost.Harness/bin/$configuration/net10.0/ConsoleHost.Harness.dll" --root "$work/plugin-root"

echo
echo "== Style 2: dotnet run on the class library Sample.Time (AlcoveHarnessExecutable)"
dotnet run --project "$repo/samples/plugins/Sample.Time/Sample.Time.csproj" "${properties[@]}"
