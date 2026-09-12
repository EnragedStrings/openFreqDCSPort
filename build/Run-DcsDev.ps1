[CmdletBinding()]
param(
    [switch]$Watch,
    [switch]$SkipSync,
    [switch]$SkipServer,
    [switch]$SkipClient
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Quote-ForSingleQuotedCommand {
    param([string]$Value)

    return "'" + $Value.Replace("'", "''") + "'"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$serverProject = Join-Path $repoRoot "OpenFreq.Server\OpenFreq.Server.csproj"
$clientProject = Join-Path $repoRoot "OpenFreq.Client\OpenFreq.Client.csproj"

if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK was not found on PATH. Install the .NET 10 SDK first."
}

if (!$SkipSync) {
    # Same code path production uses (DcsExportInstaller, via the client's own headless CLI mode) --
    # dotnet run rebuilds against whatever's currently in DCS/OpenFreqDCS, so this always syncs the
    # export scripts you're actively editing, not a stale copy.
    & dotnet run --project $clientProject -- --dcs-export install
}

$runner = if ($Watch) { "watch run" } else { "run" }
$processes = @()

if (!$SkipServer) {
    $serverCommand = @(
        "Set-Location $(Quote-ForSingleQuotedCommand $repoRoot)",
        "dotnet $runner --project $(Quote-ForSingleQuotedCommand $serverProject)"
    ) -join "; "

    $processes += Start-Process `
        -FilePath "powershell.exe" `
        -ArgumentList @("-NoExit", "-ExecutionPolicy", "Bypass", "-Command", $serverCommand) `
        -PassThru
}

if (!$SkipClient) {
    $clientCommand = @(
        "Set-Location $(Quote-ForSingleQuotedCommand $repoRoot)",
        "dotnet $runner --project $(Quote-ForSingleQuotedCommand $clientProject)"
    ) -join "; "

    $processes += Start-Process `
        -FilePath "powershell.exe" `
        -ArgumentList @("-NoExit", "-ExecutionPolicy", "Bypass", "-Command", $clientCommand) `
        -PassThru
}

if ($processes.Count -eq 0) {
    Write-Host "Nothing to launch. Use without -SkipServer/-SkipClient, or run with -SkipSync if you only want export sync."
    exit 0
}

Write-Host "OpenFreq DCS dev workflow started from source."
Write-Host "Repo: $repoRoot"
Write-Host "Mode: $(if ($Watch) { 'dotnet watch run' } else { 'dotnet run' })"
foreach ($process in $processes) {
    Write-Host ("Started PID {0}" -f $process.Id)
}
