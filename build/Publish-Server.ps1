[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$Version = "",
    [switch]$Installer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if (!$Version) {
    $Version = "0.0.0-dev"
    try {
        $gitVersion = (& git -C $repoRoot describe --tags --always --dirty 2>$null)
        if ($LASTEXITCODE -eq 0 -and $gitVersion) {
            $Version = $gitVersion.Trim().TrimStart("v")
        }
    } catch {
        $Version = "0.0.0-dev"
    }
}

$publishRoot = Join-Path $repoRoot "artifacts\publish\server"
$output = Join-Path $publishRoot $Runtime
if (Test-Path -LiteralPath $output) {
    $resolvedOutput = (Resolve-Path -LiteralPath $output).Path
    $resolvedPublishRoot = if (Test-Path -LiteralPath $publishRoot) {
        (Resolve-Path -LiteralPath $publishRoot).Path
    } else {
        (Resolve-Path -LiteralPath (Join-Path $repoRoot "artifacts\publish")).Path
    }

    if (!$resolvedOutput.StartsWith($resolvedPublishRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean publish output outside artifacts: $resolvedOutput"
    }

    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}
New-Item -ItemType Directory -Path $output -Force | Out-Null

dotnet publish (Join-Path $repoRoot "OpenFreq.Server\OpenFreq.Server.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -o $output

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Get-ChildItem -LiteralPath $output -Filter "*.pdb" -File | Remove-Item -Force

if ($Installer) {
    $iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $isccPath = if ($iscc) { $iscc.Source } else { $null }
    if (!$iscc) {
        $candidate = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        if (Test-Path -LiteralPath $candidate) {
            $isccPath = $candidate
        }
    }

    if (!$isccPath) {
        throw "Inno Setup 6 ISCC.exe was not found. Install Inno Setup or rerun without -Installer."
    }

    & $isccPath "/DAppVersion=$Version" (Join-Path $repoRoot "installer\windows\OpenFreqDcsServer.iss")
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed with exit code $LASTEXITCODE"
    }
}

Write-Host "Server publish complete: $output"
