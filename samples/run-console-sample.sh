#!/usr/bin/env bash
# Runs the Alcove console sample: builds the host (which packs Alcove), packs the sample plugins against the
# packed Alcove.Sdk and ConsoleHost.Contracts into a local folder feed, then starts the host on a fresh plugin
# root, which installs them from that feed while running (PluginHost.InstallAsync). Everything goes to artifacts/sample/; the user's NuGet settings and packages aren't changed
# (third-party packages are read from the global packages folder, so restore needs no network once the
# solution has been restored).
#
# Usage: samples/run-console-sample.sh [Debug|Release]
set -euo pipefail

configuration="${1:-Debug}"
# pwd -W: on Git Bash (Windows), the C:/... form, since the paths also go into files (nuget.config) that NuGet reads.
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && { pwd -W 2>/dev/null || pwd; })"
work="$repo/artifacts/sample"
feed="$work/feed"

dotnet build "$repo/samples/ConsoleHost/ConsoleHost.csproj" -c "$configuration" -nologo -v quiet
version="$(dotnet msbuild "$repo/src/Alcove/Alcove.csproj" -getProperty:Version -p:Configuration="$configuration")"

rm -rf "$work"
mkdir -p "$feed"
for id in Alcove Alcove.Abstractions Alcove.Sdk ConsoleHost.Contracts; do
  cp "$repo/artifacts/packages/$configuration/$id.$version.nupkg" "$feed/"
done

global_packages="$(dotnet nuget locals global-packages --list | sed 's/^global-packages: //')"
cat > "$work/nuget.config" <<CONFIG
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="sample-feed" value="$feed" />
    <add key="third-party" value="$global_packages" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="sample-feed">
      <package pattern="Alcove" />
      <package pattern="Alcove.*" />
      <package pattern="ConsoleHost.*" />
    </packageSource>
    <packageSource key="third-party">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
CONFIG

NUGET_PACKAGES="$work/nuget-packages" dotnet pack "$repo/samples/plugins/SamplePlugins.slnx" \
  -c "$configuration" -nologo -v quiet --output "$work/packages" \
  -p:AlcoveVersion="$version" -p:RestoreConfigFile="$work/nuget.config"

dotnet run --project "$repo/samples/ConsoleHost/ConsoleHost.csproj" -c "$configuration" --no-build -- \
  "$work/plugin-root" --feed "$work/packages"
