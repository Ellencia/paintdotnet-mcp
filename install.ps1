#requires -Version 5.1
<#
.SYNOPSIS
Build the MCP server and install the Bridge, requesting elevation only for copying.
.EXAMPLE
.\install.ps1
.EXAMPLE
.\install.ps1 -PaintDotNetDir 'D:\Apps\paint.net'
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$PaintDotNetDir = 'C:\Program Files\paint.net',
    [string]$EffectsDir
)

$ErrorActionPreference = 'Stop'
try {
    $PaintDotNetDir = (Resolve-Path -LiteralPath $PaintDotNetDir).ProviderPath
    if (-not (Test-Path -LiteralPath (Join-Path $PaintDotNetDir 'PaintDotNet.Effects.dll') -PathType Leaf)) {
        throw "Paint.NET assemblies not found in $PaintDotNetDir. Use -PaintDotNetDir to specify the installation."
    }
    if (-not $EffectsDir) { $EffectsDir = Join-Path $PaintDotNetDir 'Effects' }
    $EffectsDir = (Resolve-Path -LiteralPath $EffectsDir).ProviderPath
    if (-not (Test-Path -LiteralPath $EffectsDir -PathType Container)) { throw 'EffectsDir must be a directory.' }
    if (Get-Process -Name paintdotnet -ErrorAction SilentlyContinue) {
        throw 'Paint.NET is running. Save your work, close Paint.NET, then run install.ps1 again. Loaded plugin DLLs cannot be safely replaced.'
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 9 SDK, then run install.ps1 again.' }

    # Release build files held by the MCP client before rebuilding this checkout.
    $serverPath = Join-Path $PSScriptRoot "src\PaintDotNetMcp.Server\bin\$Configuration\net9.0\PaintDotNetMcp.Server.exe"
    Get-Process -Name PaintDotNetMcp.Server -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.Path -eq $serverPath) {
            Write-Host "Stopping this checkout's MCP server (PID $($_.Id))..."
            Stop-Process -Id $_.Id -Force -ErrorAction Stop
            $_.WaitForExit()
        }
    }
    Write-Host "Building MCP server and Bridge ($Configuration)..."
    & dotnet build (Join-Path $PSScriptRoot 'PaintDotNetMcp.sln') -c $Configuration "-p:PaintDotNetDir=$PaintDotNetDir"
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE). Installation was not started." }

    $outputDir = Join-Path $PSScriptRoot "src\PaintDotNetMcp.Bridge\bin\$Configuration\net9.0-windows"
    $names = @('PaintDotNetMcp.Bridge.dll', 'PaintDotNetMcp.Contracts.dll', 'SkiaSharp.dll', 'libSkiaSharp.dll', 'System.Drawing.Common.dll')
    foreach ($name in $names) {
        if (-not (Test-Path -LiteralPath (Join-Path $outputDir $name) -PathType Leaf)) { throw "Required build output missing: $name" }
    }

    # Probe the destination without overwriting a plugin. Only access denial needs UAC.
    $needsElevation = $false
    $probe = Join-Path $EffectsDir ([Guid]::NewGuid().ToString() + '.tmp')
    try {
        $stream = [IO.File]::Open($probe, [IO.FileMode]::CreateNew)
        $stream.Dispose()
    } catch [UnauthorizedAccessException] { $needsElevation = $true }
    finally { if (Test-Path -LiteralPath $probe) { Remove-Item -LiteralPath $probe -Force } }

    if ($needsElevation) {
        Write-Host 'Administrator permission is needed to copy the plugin. Accept the Windows UAC prompt.'
        # Encode literal paths, avoiding command-line quoting and interpolation of path contents.
        $sourceLiteral = "'" + $outputDir.Replace("'", "''") + "'"
        $destLiteral = "'" + $EffectsDir.Replace("'", "''") + "'"
        $nameLiterals = ($names | ForEach-Object { "'$_'" }) -join ','
        $copyCommand = @"
`$ErrorActionPreference = 'Stop'
try {
    if (Get-Process -Name paintdotnet -ErrorAction SilentlyContinue) { throw 'Close Paint.NET before installing.' }
    foreach (`$name in @($nameLiterals)) {
        Copy-Item -LiteralPath (Join-Path $sourceLiteral `$name) -Destination (Join-Path $destLiteral `$name) -Force
    }
    exit 0
} catch { Write-Error `$_ -ErrorAction Continue; exit 1 }
"@
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($copyCommand))
        $shellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $child = Start-Process -FilePath $shellPath -Verb RunAs -WindowStyle Hidden -ArgumentList @('-NoProfile', '-EncodedCommand', $encoded) -Wait -PassThru
        if ($child.ExitCode -ne 0) { throw 'Plugin copy failed. Close Paint.NET and check permissions, then run install.ps1 again.' }
    } else {
        if (Get-Process -Name paintdotnet -ErrorAction SilentlyContinue) { throw 'Close Paint.NET before installing.' }
        foreach ($name in $names) {
            Copy-Item -LiteralPath (Join-Path $outputDir $name) -Destination (Join-Path $EffectsDir $name) -Force
        }
    }
    foreach ($name in $names) {
        $sourceHash = (Get-FileHash -LiteralPath (Join-Path $outputDir $name)).Hash
        $installedHash = (Get-FileHash -LiteralPath (Join-Path $EffectsDir $name)).Hash
        if ($sourceHash -ne $installedHash) { throw "Installed file verification failed: $name" }
    }
    Write-Host 'Installed and verified all 5 plugin DLLs.' -ForegroundColor Green
    Write-Host 'Open Paint.NET, open a canvas, then run Effects > Tools > MCP Bridge once.'
    Write-Host 'Reconnect your MCP client to load the updated server. You can close this PowerShell window.'
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
