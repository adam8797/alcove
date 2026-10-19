#!/usr/bin/env bash
# Runs every scenario in its own process; output goes to stdout and results/all.txt
set -uo pipefail
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH"
mkdir -p results
scenarios=(q1-loadfrompath q1-load-override q1-return-existing-lower q1-return-existing-higher q1-same-version-second-plugin
  q2-default-then-private q2-default-then-private-unversioned-first q2-private-then-default
  q3-failure-then-available q3-failure-loadfromname q3-load-throws q3-resolving-event
  q2-null-answer-not-cached q2-split-identity q4-new-assemblies q4-same-version-different-file q4-lower-path-after-higher q5-what-triggers-resolution
  q6-native-remap q6-native-already-in-process q6-native-soname-control q6-native-failure-then-available rule-demo)
for s in "${scenarios[@]}"; do dotnet out/Host/Host.dll out native "$s"; echo; done | tee results/all.txt
