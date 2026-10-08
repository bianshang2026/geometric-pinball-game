param(
    [switch]$Check,
    [switch]$NoStage
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $repoRoot

$mode = if ($Check) { "check" } else { "fix" }
$autoStage = -not $NoStage

function New-GuidHex {
    return [Guid]::NewGuid().ToString("N")
}

$invalidCount = 0
$fixedCount = 0

$metaFiles = Get-ChildItem -Path . -Recurse -Filter *.meta -File |
    Where-Object {
        $_.FullName -notmatch '\\\.git\\' -and
        $_.FullName -notmatch '\\build\\' -and
        $_.FullName -notmatch '\\obj\\'
    }

foreach ($file in $metaFiles) {
    $content = Get-Content -Path $file.FullName -Raw
    $guidMatch = [regex]::Match($content, '(?m)^guid:\s*(\S+)\s*$')
    if (-not $guidMatch.Success) {
        continue
    }

    $guid = $guidMatch.Groups[1].Value
    if ($guid -match '^[a-f0-9]{32}$') {
        continue
    }

    $invalidCount++
    $newGuid = New-GuidHex

    if ($mode -eq "fix") {
        $newContent = [regex]::Replace($content, '(?m)^guid:\s*\S+\s*$', "guid: $newGuid", 1)
        [System.IO.File]::WriteAllText($file.FullName, $newContent)

        if ($autoStage) {
            git add -- $file.FullName | Out-Null
        }

        Write-Output "FIXED $($file.FullName): $guid -> $newGuid"
        $fixedCount++
    }
    else {
        Write-Output "INVALID $($file.FullName): $guid (would fix to $newGuid)"
    }
}

if ($invalidCount -eq 0) {
    Write-Output "All .meta GUIDs are valid."
    exit 0
}

if ($mode -eq "check") {
    Write-Output ""
    Write-Output "Found $invalidCount invalid .meta GUID(s)."
    Write-Output "Run to fix:"
    Write-Output "  .\scripts\fix-meta-guids.ps1"
    exit 1
}

Write-Output ""
Write-Output "Fixed $fixedCount invalid .meta GUID(s)."
if ($autoStage) {
    Write-Output "Staged fixed .meta files automatically."
}
else {
    Write-Output "Remember to stage fixed files manually."
}
Write-Output "You can run git commit again."
