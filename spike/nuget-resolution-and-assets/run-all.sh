#!/usr/bin/env bash
# Rebuilds everything and runs every experiment; output goes to work/logs/*.txt (and the console).
set -euo pipefail
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p work/logs
./build-fixtures.sh > work/logs/build-fixtures.txt 2>&1
compare/build-compare.sh > work/logs/build-compare.txt 2>&1
dotnet build src/Spike -v q > work/logs/build-spike.txt 2>&1
dotnet build src/WebHost -v q > work/logs/build-webhost.txt 2>&1
spike() { dotnet src/Spike/bin/Debug/net10.0/Spike.dll "$@"; }
spike inventory --verbose                    | tee work/logs/q1-inventory.txt
dotnet src/WebHost/bin/Debug/net10.0/WebHost.dll | tee work/logs/q1-inventory-web.txt
{ spike resolve Serilog.Sinks.File; spike resolve Microsoft.Extensions.Http; spike resolve "Microsoft.Extensions.Http" "[8.0.0]";
  spike resolve NuGet.Packaging "[6.0.0]"; spike resolve Polly; }  | tee work/logs/q2-resolve.txt
compare/restore-oracle.sh                    | tee work/logs/q2-restore-oracle.txt
spike diamond                                | tee work/logs/q3-diamond.txt
spike assets compare                         | tee work/logs/q4-assets.txt
spike download                               | tee work/logs/q5-download.txt
spike search                                 | tee work/logs/q6-search.txt
size/measure.sh                              | tee work/logs/q7-size.txt
