[CmdletBinding()]
param(
    [string]$SourceRoot = "",
    [string[]]$SavedGamesNames = @("DCS", "DCS.openbeta")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-SourceRoot {
    param([string]$ExplicitSourceRoot)

    if ($ExplicitSourceRoot) {
        return (Resolve-Path -LiteralPath $ExplicitSourceRoot).Path
    }

    $candidates = @(
        (Join-Path $PSScriptRoot "..\..\DCS\OpenFreqDCS"),
        (Join-Path $PSScriptRoot "..\DCS\OpenFreqDCS")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "Could not locate DCS\OpenFreqDCS. Pass -SourceRoot explicitly."
}

function Add-OpenFreqExportLine {
    param([string]$ExportLuaPath)

    $exportLine = @'
-- OpenFreqDCS BEGIN
pcall(function()
    local okLfs, lfs = pcall(require, "lfs")
    local writeDir = ""
    if okLfs and lfs and type(lfs.writedir) == "function" then
        writeDir = lfs.writedir()
    end

    local path = writeDir .. [[Mods\Services\OpenFreqDCS\Scripts\OpenFreqDCS.lua]]
    local ok, err = pcall(dofile, path)
    if not ok then
        if log and type(log.write) == "function" and log.ERROR then
            pcall(log.write, "OpenFreqDCS", log.ERROR, tostring(err))
        end
        if writeDir ~= "" then
            local file = io.open(writeDir .. [[Logs\OpenFreqDCS.log]], "ab")
            if file then
                file:write(os.date("!%Y-%m-%dT%H:%M:%SZ "), "loader error: ", tostring(err), "\r\n")
                file:close()
            end
        end
    end
end)
-- OpenFreqDCS END
'@
    $exportLine = $exportLine.Trim()
    $exportToken = 'Mods\Services\OpenFreqDCS\Scripts\OpenFreqDCS.lua'
    $scriptsDir = Split-Path -Parent $ExportLuaPath

    New-Item -ItemType Directory -Path $scriptsDir -Force | Out-Null

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)

    if (!(Test-Path -LiteralPath $ExportLuaPath)) {
        [System.IO.File]::WriteAllText($ExportLuaPath, $exportLine + "`r`n", $utf8NoBom)
        return
    }

    $content = Get-Content -LiteralPath $ExportLuaPath -Raw
    $content = [regex]::Replace($content, "(?ms)^-- OpenFreqDCS BEGIN\r?\n.*?^-- OpenFreqDCS END\r?\n?", "")

    if ($content -like "*$exportToken*") {
        $content = (($content -split "\r?\n") | Where-Object { $_ -notlike "*$exportToken*" }) -join "`r`n"
    }

    if ($content.Length -gt 0 -and !$content.EndsWith("`n")) {
        $content += "`r`n"
    }

    $content += $exportLine + "`r`n"
    [System.IO.File]::WriteAllText($ExportLuaPath, $content, $utf8NoBom)
}

$source = Resolve-SourceRoot -ExplicitSourceRoot $SourceRoot
$savedGamesRoot = Join-Path ([Environment]::GetFolderPath("UserProfile")) "Saved Games"

$targets = foreach ($name in $SavedGamesNames) {
    $path = Join-Path $savedGamesRoot $name
    if (Test-Path -LiteralPath $path) {
        $path
    }
}

if (!$targets) {
    $defaultTarget = Join-Path $savedGamesRoot "DCS"
    New-Item -ItemType Directory -Path $defaultTarget -Force | Out-Null
    $targets = @($defaultTarget)
}

foreach ($target in $targets) {
    $serviceRoot = Join-Path $target "Mods\Services\OpenFreqDCS"
    New-Item -ItemType Directory -Path $serviceRoot -Force | Out-Null

    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $serviceRoot -Recurse -Force

    $exportLua = Join-Path $target "Scripts\Export.lua"
    Add-OpenFreqExportLine -ExportLuaPath $exportLua

    Write-Host "Installed OpenFreq DCS export into $serviceRoot"
    Write-Host "Updated $exportLua"
}
