# Runs the Alcove WPF sample (Windows): builds the host (which packs Alcove, Alcove.Wpf and WpfHost.Sdk), packs the
# sample plugins against the packed Alcove.Sdk and WpfHost.Sdk into a local folder feed, then starts the host on a fresh
# plugin root. At first start it installs Sample.Notes and Sample.Charts from the feed; "Install (hot)" installs
# Sample.Clock while it runs. Everything goes to artifacts/wpf-sample/; the user's NuGet settings and packages aren't
# changed. Run it again with -KeepRoot to see the installed plugins load at startup.
#
# Usage: samples\run-wpf-sample.ps1 [-Configuration Debug|Release] [-KeepRoot]
param(
    [string]$Configuration = "Debug",
    [switch]$KeepRoot
)
$ErrorActionPreference = "Stop"

$repo = (Resolve-Path "$PSScriptRoot/..").Path
$work = Join-Path $repo "artifacts/wpf-sample"
$feed = Join-Path $work "feed"

dotnet build "$repo/samples/WpfHost/WpfHost.csproj" -c $Configuration -nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "The host didn't build." }
$version = (dotnet msbuild "$repo/src/Alcove/Alcove.csproj" -getProperty:Version -p:Configuration=$Configuration).Trim()

$root = Join-Path $work "plugin-root"
if (-not $KeepRoot -and (Test-Path $work)) { Remove-Item -Recurse -Force $work }
foreach ($dir in @($feed, (Join-Path $work "packages"))) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    New-Item -ItemType Directory -Force $dir | Out-Null
}
foreach ($id in "Alcove.Abstractions", "Alcove.Sdk", "Alcove", "Alcove.Wpf", "WpfHost.Sdk") {
    Copy-Item (Join-Path $repo "artifacts/packages/$Configuration/$id.$version.nupkg") $feed
}

$globalPackages = ((dotnet nuget locals global-packages --list) -replace '^global-packages: ', '').Trim()
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="sample-feed" value="$feed" />
    <add key="third-party" value="$globalPackages" />
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
"@ | Set-Content (Join-Path $work "nuget.config")

$env:NUGET_PACKAGES = Join-Path $work "nuget-packages"
try {
    dotnet pack "$repo/samples/wpf-plugins/WpfSamplePlugins.slnx" -c $Configuration -nologo -v quiet --output (Join-Path $work "packages") `
        "-p:AlcoveVersion=$version" "-p:RestoreConfigFile=$(Join-Path $work 'nuget.config')"
    if ($LASTEXITCODE -ne 0) { throw "The sample plugins didn't pack." }
}
finally {
    Remove-Item Env:NUGET_PACKAGES
}

dotnet run --project "$repo/samples/WpfHost/WpfHost.csproj" -c $Configuration --no-build -- `
    $root --feed (Join-Path $work "packages") --install "Sample.Notes,Sample.Charts"
