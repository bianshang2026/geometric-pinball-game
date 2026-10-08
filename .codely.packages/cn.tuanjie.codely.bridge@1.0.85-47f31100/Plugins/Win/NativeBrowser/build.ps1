#Requires -Version 5.1
<#
.SYNOPSIS
    Build NativeBrowser.dll and copy to Plugins/Win/

.PARAMETER Config
    Build configuration: Release (default) or Debug

.PARAMETER Clean
    Remove build directory before configuring

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Config Debug
    .\build.ps1 -Clean
#>
param(
    [ValidateSet("Release", "Debug")]
    [string]$Config = "Release",

    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ScriptDir = $PSScriptRoot
$BuildDir  = Join-Path $ScriptDir "build"

# ── Tool detection ─────────────────────────────────────────────────────────────

function Find-CMake {
    $cmake = Get-Command cmake -ErrorAction SilentlyContinue
    if ($cmake) { return $cmake.Source }

    $candidates = @(
        "$env:ProgramFiles\CMake\bin\cmake.exe",
        "${env:ProgramFiles(x86)}\CMake\bin\cmake.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    throw "cmake not found. Install CMake 3.20+ and add it to PATH.`nhttps://cmake.org/download/"
}

function Find-VSGenerator {
    $generators = @(
        @{ Name = "Visual Studio 17 2022"; MinVer = "17" },
        @{ Name = "Visual Studio 16 2019"; MinVer = "16" }
    )

    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        foreach ($g in $generators) {
            $found = & $vswhere -version "[$($g.MinVer),)" -products * `
                -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
                -property installationPath 2>$null
            if ($found) { return $g.Name }
        }
    }

    return "Visual Studio 17 2022"
}

# ── Main ───────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "=======================================" -ForegroundColor Cyan
Write-Host "  NativeBrowser Build  [$Config]" -ForegroundColor Cyan
Write-Host "=======================================" -ForegroundColor Cyan
Write-Host ""

# 1. Check CMake
Write-Host "[1/5] Checking CMake..." -ForegroundColor Yellow
$cmake = Find-CMake
$cmakeVer = (& $cmake --version 2>&1 | Select-Object -First 1) -replace "cmake version ", ""
Write-Host "      $cmake  (v$cmakeVer)"

# 2. Check WebView2 SDK
Write-Host "[2/5] Checking WebView2 SDK..." -ForegroundColor Yellow
$webview2Header = Join-Path $ScriptDir "vendor\webview2\include\WebView2.h"
if (-not (Test-Path $webview2Header)) {
    Write-Host ""
    Write-Host "ERROR: WebView2 SDK not found at:" -ForegroundColor Red
    Write-Host "       $webview2Header" -ForegroundColor Red
    Write-Host ""
    Write-Host "Run the following to fetch it from NuGet cache:" -ForegroundColor Yellow
    Write-Host '  $src = (Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.web.webview2" | Sort-Object Name -Desc | Select -First 1).FullName'
    Write-Host '  Copy-Item "$src\build\native\include\WebView2.h"            "vendor\webview2\include\" -Force'
    Write-Host '  Copy-Item "$src\build\native\x64\WebView2LoaderStatic.lib"  "vendor\webview2\lib\x64\" -Force'
    exit 1
}
Write-Host "      OK ($webview2Header)"

# 3. Clean (optional)
if ($Clean -and (Test-Path $BuildDir)) {
    Write-Host "[3/5] Cleaning build directory..." -ForegroundColor Yellow
    Remove-Item $BuildDir -Recurse -Force
    Write-Host "      Removed: $BuildDir"
} else {
    Write-Host "[3/5] Clean skipped (use -Clean to force)" -ForegroundColor DarkGray
}

# 4. CMake configure
$generator = Find-VSGenerator
Write-Host "[4/5] CMake configure [Generator: $generator]..." -ForegroundColor Yellow

& $cmake -B $BuildDir -G $generator -A x64 -S $ScriptDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: CMake configure failed (exit $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

# 5. Build
Write-Host ""
Write-Host "[5/5] Building [$Config]..." -ForegroundColor Yellow

& $cmake --build $BuildDir --config $Config --parallel
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Build failed (exit $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

# Verify output
$outputDll = [IO.Path]::GetFullPath((Join-Path $ScriptDir "..\NativeBrowser.dll"))

Write-Host ""
if (Test-Path $outputDll) {
    $sizeKB = [math]::Round((Get-Item $outputDll).Length / 1KB, 1)
    Write-Host "Build succeeded!" -ForegroundColor Green
    Write-Host "  Output : $outputDll"
    Write-Host "  Size   : $sizeKB KB"
} else {
    Write-Host "ERROR: Build finished but output DLL not found: $outputDll" -ForegroundColor Red
    exit 1
}

Write-Host ""
