#!/usr/bin/env bash
# Builds everything into ./layout (packages/<id>/<ver>/ + host/). Order matters: plugins reference each other by HintPath.
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"; export PATH="$DOTNET_ROOT:$PATH"
rm -rf layout
for p in Contracts Dep.v1 Dep.v2 Dep.v3 A B BD2 BD3 BOwnD1 C CChain Host LeakScan; do
  dotnet build "src/$p/$p.csproj" -c Release -nologo -v q -clp:NoSummary
done
echo "build ok"
