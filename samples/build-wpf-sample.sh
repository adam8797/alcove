#!/usr/bin/env bash
# Builds the Alcove WPF sample on any OS: the host (which packs Alcove, Alcove.Wpf and WpfHost.Sdk) and the sample
# plugins, packed into artifacts/wpf-sample/packages/. WPF only runs on Windows: run it there with run-wpf-sample.ps1.
#
# Usage: samples/build-wpf-sample.sh [Debug|Release]
set -euo pipefail

configuration="${1:-Debug}"
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$repo/artifacts/wpf-sample"
feed="$work/feed"

dotnet build "$repo/samples/WpfHost/WpfHost.csproj" -c "$configuration" -nologo -v quiet
version="$(dotnet msbuild "$repo/src/Alcove/Alcove.csproj" -getProperty:Version -p:Configuration="$configuration")"

rm -rf "$work"
mkdir -p "$feed"
for id in Alcove.Abstractions Alcove.Sdk Alcove Alcove.Wpf WpfHost.Sdk; do
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
      <package pattern="WpfHost.*" />
    </packageSource>
    <packageSource key="third-party">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
CONFIG

NUGET_PACKAGES="$work/nuget-packages" dotnet pack "$repo/samples/wpf-plugins/WpfSamplePlugins.slnx" \
  -c "$configuration" -nologo -v quiet --output "$work/packages" \
  -p:AlcoveVersion="$version" -p:RestoreConfigFile="$work/nuget.config"

echo "Packed the WPF sample plugins into $work/packages. Run the sample on Windows: samples\\run-wpf-sample.ps1"
