# Builds the spike and runs every scenario (each in a fresh child process). Windows only.
# Usage (PowerShell, from anywhere):  .\spike\wpf-across-load-contexts\run-windows.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host "dotnet SDKs:"; dotnet --list-sdks
dotnet build src\WpfHost\WpfHost.csproj -c Debug
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$bin = Join-Path $PSScriptRoot 'src\WpfHost\bin\Debug\net10.0-windows'
Push-Location $bin
try {
    if (Test-Path results) { Remove-Item results -Recurse -Force }
    .\WpfHost.exe --all
    $code = $LASTEXITCODE
} finally { Pop-Location }

$zip = Join-Path $PSScriptRoot 'wpf-spike-results.zip'
Compress-Archive -Path (Join-Path $bin 'results\*') -DestinationPath $zip -Force
Write-Host ""
Write-Host "Exit code: $code  (0 = every check matched its prediction)"
Write-Host "Paste back: $bin\results\summary.txt"
Write-Host "Screenshots + summary zipped to: $zip"
