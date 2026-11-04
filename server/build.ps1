param(
    [string]$DotnetPath = ""
)

$ErrorActionPreference = "Stop"

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

Push-Location $PSScriptRoot
try {
    & $DotnetPath publish WceRoomServer.csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o dist
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE" }
    Write-Host ""
    Write-Host "Published: $(Join-Path $PSScriptRoot 'dist\WceRoomServer.exe')"
}
finally {
    Pop-Location
}
