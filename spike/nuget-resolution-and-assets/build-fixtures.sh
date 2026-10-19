#!/usr/bin/env bash
# Packs the local fixture packages into work/feed (a flat local folder feed).
set -euo pipefail
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p work/feed
rm -rf work/gpf/libshared work/gpf/plugin* work/gpf/assetslib
for v in 1.0.0 2.0.0 3.0.0; do
  rm -rf fixtures/LibShared/obj fixtures/LibShared/bin
  dotnet pack fixtures/LibShared -c Release -o work/feed -p:Version=$v -v q
done
for p in LibMid PluginA PluginB PluginC PluginD PluginE AssetsLib; do
  dotnet pack fixtures/$p -c Release -o work/feed -v q
done
ls -1 work/feed
