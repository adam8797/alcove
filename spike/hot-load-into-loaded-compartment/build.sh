#!/usr/bin/env bash
# Builds the native libs and every managed project into ./out/<Project>/
set -euo pipefail
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
rm -rf out native/v1 native/v2
mkdir -p native/v1 native/v2
for v in 1 2; do
  gcc -shared -fPIC -DNAT_VERSION=$v -Wl,-soname,libnat.so -o native/v$v/libnat.so native/nat.c
done
# libnatuser.so (v2 dir) depends on libnat.so by soname and finds it next to itself via RUNPATH $ORIGIN.
gcc -shared -fPIC -o native/v2/libnatuser.so native/natuser.c -Lnative/v2 -lnat -Wl,-rpath,'$ORIGIN'
for p in D1 D1b D2 E F PluginA PluginB PluginB1 PluginC PluginC2 PluginG PluginN1 PluginN2 PluginN3 Host; do
  dotnet build "src/$p" -c Release -o "out/$p" -v q -nologo -clp:NoSummary
done
echo "build ok"
