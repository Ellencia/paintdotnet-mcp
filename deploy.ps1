#requires -Version 5.1
<#
.SYNOPSIS
  Build paintdotnet-mcp and deploy the Bridge plugin DLLs into Paint.NET's Effects folder.

.DESCRIPTION
  One-shot script that:
    1. Kills PaintDotNetMcp.Server.exe if running (Claude Desktop's MCP host) so the build can
       overwrite locked DLLs.
    2. Warns if Paint.NET is running (won't kill it because the user may have unsaved work).
    3. Runs `dotnet build -c <Configuration>` on the solution.
    4. Copies the Bridge output DLLs (PaintDotNetMcp.Bridge.dll, PaintDotNetMcp.Contracts.dll,
       SkiaSharp.dll, libSkiaSharp.dll, System.Drawing.Common.dll) to the Effects folder.
    5. Reminds the user to restart Paint.NET and Claude Desktop.

.PARAMETER Configuration
  MSBuild configuration. Default: Release.

.PARAMETER PaintDotNetDir
  Paint.NET install directory. Default: C:\Program Files\paint.net

.PARAMETER EffectsDir
  Override target Effects folder. Default: <PaintDotNetDir>\Effects

.PARAMETER SkipBuild
  Skip the dotnet build step (only deploy current bin output).

.EXAMPLE
  .\deploy.ps1
  .\deploy.ps1 -Configuration Debug
  .\deploy.ps1 -PaintDotNetDir 'D:\Apps\paint.net'
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$PaintDotNetDir = 'C:\Program Files\paint.net',
    [string]$EffectsDir,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $EffectsDir) {
    $EffectsDir = Join-Path $PaintDotNetDir 'Effects'
}

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Warn($msg) { Write-Host "    [warn] $msg" -ForegroundColor Yellow }
function Write-OK  ($msg) { Write-Host "    $msg" -ForegroundColor Green }

# 1. Kill MCP server if running so the build can overwrite locked DLLs.
Write-Step 'Checking for running PaintDotNetMcp.Server.exe (Claude Desktop spawn)...'
$serverProcs = Get-Process -Name 'PaintDotNetMcp.Server' -ErrorAction SilentlyContinue
if ($serverProcs) {
    foreach ($p in $serverProcs) {
        Write-Warn "killing PID $($p.Id)"
        try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { Write-Warn $_.Exception.Message }
    }
    Start-Sleep -Milliseconds 300
} else {
    Write-OK 'none running.'
}

# 2. Warn if Paint.NET is running. Don't kill — user may have unsaved work.
$pdnProc = Get-Process -Name 'paintdotnet' -ErrorAction SilentlyContinue
if ($pdnProc) {
    Write-Warn 'Paint.NET is running. The plugin will be reloaded only after Paint.NET restarts.'
    Write-Warn 'Consider closing Paint.NET before re-running this script if you hit file-lock errors.'
}

# 3. Build (unless -SkipBuild).
if (-not $SkipBuild) {
    Write-Step "Building solution ($Configuration)..."
    Push-Location $ScriptDir
    try {
        & dotnet build -c $Configuration -p:PaintDotNetDir="$PaintDotNetDir"
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }
    }
    finally { Pop-Location }
} else {
    Write-Step 'Skipping build (-SkipBuild).'
}

# 4. Locate the Bridge output and the files to deploy.
$bridgeOut = Join-Path $ScriptDir "src\PaintDotNetMcp.Bridge\bin\$Configuration\net9.0-windows"
if (-not (Test-Path $bridgeOut)) {
    throw "Bridge output not found: $bridgeOut. Build may have failed or used a different TFM."
}

# Files we always want to ship. SkiaSharp + native are copied if present.
$wanted = @(
    'PaintDotNetMcp.Bridge.dll',
    'PaintDotNetMcp.Contracts.dll',
    'SkiaSharp.dll',
    'libSkiaSharp.dll',
    'System.Drawing.Common.dll'
)
$toCopy = @()
foreach ($name in $wanted) {
    $src = Join-Path $bridgeOut $name
    if (Test-Path $src) { $toCopy += $src }
    else { Write-Warn "missing in build output: $name (skipping)" }
}

if (-not (Test-Path $EffectsDir)) {
    throw "Effects folder does not exist: $EffectsDir"
}

# 5. Copy. May require admin if EffectsDir is under Program Files.
Write-Step "Copying $($toCopy.Count) file(s) to $EffectsDir ..."
foreach ($src in $toCopy) {
    $name = Split-Path -Leaf $src
    $dst  = Join-Path $EffectsDir $name
    try {
        Copy-Item -Path $src -Destination $dst -Force
        Write-OK $name
    }
    catch {
        if ($_.Exception.Message -match 'Access') {
            throw "Access denied copying to $EffectsDir. Re-run this script in an elevated PowerShell, or pass -EffectsDir to a writable folder."
        }
        throw
    }
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Write-Host '  - Restart Paint.NET so it picks up the new Effects DLLs.'
Write-Host '  - Restart Claude Desktop (or just trigger a new MCP call) so PaintDotNetMcp.Server.exe respawns.'
Write-Host '  - In Paint.NET, run "Effects > Tools > MCP Bridge" once to seed the snapshot.'
