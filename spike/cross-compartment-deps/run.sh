#!/usr/bin/env bash
# Builds, runs every scenario, and runs the static leak scanner. Output: stdout (also saved to results.txt).
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"; export PATH="$DOTNET_ROOT:$PATH"
./build.sh >/dev/null
L=layout/packages; SCAN="dotnet layout/tools/LeakScan/LeakScan.dll"
SDK="$DOTNET_ROOT/sdk/$(dotnet --version)"
RT="$DOTNET_ROOT/shared/Microsoft.NETCore.App/$(dotnet --list-runtimes | awk '/NETCore.App 10\./{print $2}' | tail -1)"
{
  echo "##### Host scenarios"; dotnet layout/host/Host.dll
  echo; echo "##### Q3: public-surface scan of A and B"; $SCAN surface $L/A/1.0.0/A.dll $L/B/1.0.0/B.dll
  echo; echo "##### Q3: consumer scans"
  for c in B BD2 BD3 BOwnD1 C; do $SCAN consumer $L/$c/1.0.0/$c.dll $L/A/1.0.0/A.dll; done
  $SCAN consumer $L/C/1.0.0/C.dll $L/B/1.0.0/B.dll
  echo; echo "##### Q3: cost on real assemblies (first line includes JIT warm-up)"
  $SCAN bench $L/A/1.0.0/A.dll "$SDK/Newtonsoft.Json.dll" "$SDK/NuGet.Protocol.dll" "$SDK/Roslyn/bincore/Microsoft.CodeAnalysis.dll" "$SDK/Roslyn/bincore/Microsoft.CodeAnalysis.CSharp.dll" "$RT/System.Private.CoreLib.dll"
} 2>&1 | tee results.txt
