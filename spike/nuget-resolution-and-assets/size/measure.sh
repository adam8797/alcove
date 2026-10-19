#!/usr/bin/env bash
# Publishes each variant (framework-dependent) and sums the size of the NuGet-related assemblies it brings in.
set -euo pipefail
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
for p in Versioning Packaging Protocol Core CoreResolver Commands; do
  dotnet publish $p -c Release -v q -o out/$p >/dev/null
  files=$(cd out/$p && ls *.dll | grep -v "^$p.dll$")
  total=$(cd out/$p && du -cb $files | tail -1 | cut -f1)
  printf "%-13s %8.0f KB  %2d dlls: %s\n" $p $(echo "$total/1024" | bc -l) $(echo "$files" | wc -l) "$(echo $files | tr '\n' ' ')"
done
