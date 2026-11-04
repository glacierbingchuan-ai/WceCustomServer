param(
    [string]$GameDir = $env:WCE_GAME_DIR,
    [string]$OutDir = "$PSScriptRoot\bin",
    [string]$DotnetPath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($GameDir)) {
    Write-Host "Game directory is required (the folder containing Worst Class Ever.exe)."
    Write-Host "Usage: ./build.ps1 -GameDir ""D:\Games\Worst Class Ever Demo"""
    Write-Host "Or set environment variable WCE_GAME_DIR."
    exit 1
}

$managed = Join-Path $GameDir "Worst Class Ever_Data\Managed"
$bepInEx = Join-Path $GameDir "BepInEx\core"
if (!(Test-Path $managed)) { throw "Managed folder not found: $managed" }
if (!(Test-Path (Join-Path $bepInEx "BepInEx.dll"))) { throw "BepInEx not found. Extract BepInEx 5.4.x into the game folder first." }

if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) {
        $DotnetPath = $cmd.Source
    }
    elseif (Test-Path "$env:TEMP\dotnet8\dotnet.exe") {
        $DotnetPath = "$env:TEMP\dotnet8\dotnet.exe"
    }
    elseif (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") {
        $DotnetPath = "$env:USERPROFILE\.dotnet\dotnet.exe"
    }
}
if (!(Test-Path $DotnetPath)) { throw "dotnet not found. Install .NET 8 SDK first, or pass -DotnetPath." }

$sdkRoot = Join-Path (Split-Path $DotnetPath) "sdk"
$sdk = Get-ChildItem $sdkRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
$csc = Join-Path $sdk.FullName "Roslyn\bincore\csc.dll"
if (!(Test-Path $csc)) { throw "Roslyn compiler not found: $csc" }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$out = Join-Path $OutDir "CustomServerPlugin.dll"

$compilerArgs = @(
    $csc,
    "-nostdlib",
    "-target:library",
    "-langversion:latest",
    "-nowarn:0618,0649",
    "-out:$out"
)
Get-ChildItem $managed -Filter *.dll | ForEach-Object { $compilerArgs += "-r:$($_.FullName)" }
$compilerArgs += "-r:$(Join-Path $bepInEx 'BepInEx.dll')"
$compilerArgs += "-r:$(Join-Path $bepInEx '0Harmony.dll')"
$compilerArgs += (Join-Path $PSScriptRoot "CustomServerPlugin.cs")

& $DotnetPath @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host "Built: $out"
Write-Host "Copy it to: $(Join-Path $GameDir 'BepInEx\plugins')"
