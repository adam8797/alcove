#!/usr/bin/env bash
# "dotnet restore" oracle: what package set does the real SDK restore pick for a net10.0 project
# referencing exactly <id> <version>? Compare with `Spike resolve <id> [<version>] --nopin`.
set -euo pipefail
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
run() {
  local name="oracle/$1_$2"; mkdir -p "$name"
  cat > "$name/o.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><NoWarn>NU1903;NU1902</NoWarn></PropertyGroup>
  <ItemGroup><PackageReference Include="$1" Version="[$2]" /></ItemGroup>
</Project>
XML
  dotnet restore "$name" -v q >/dev/null
  echo "== $1 $2"
  python3 -I -c "import json,sys; d=json.load(open(sys.argv[1])); print('  ' + ', '.join(sorted(k for k,v in d['libraries'].items() if v['type']=='package')))" "$name/obj/project.assets.json"
}
run Microsoft.Extensions.Http 10.0.13
run Microsoft.Extensions.Http 8.0.0
run NuGet.Packaging 6.0.0
run Serilog.Sinks.File 7.0.0
