#!/usr/bin/env bash
# Builds the reference apps (portable + RID-specific) so their deps.json can be compared with our asset selection.
set -euo pipefail
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build AssetsApp -v q -o out/portable
for rid in linux-x64 win-x64 osx-arm64 linux-musl-x64; do
  dotnet build AssetsApp -v q -r $rid --self-contained false -o out/$rid
done
dotnet build WinApp -v q -f net10.0-windows -o out/win-portable
dotnet build WinApp -v q -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained false -o out/win10-x64
ls out/*/*.deps.json out/*/*.runtimeconfig.json
