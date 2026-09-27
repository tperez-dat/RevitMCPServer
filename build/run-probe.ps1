<#
.SYNOPSIS
    Checks a running Revit MCP bridge by exercising every read-only tool against the open model.

.DESCRIPTION
    Run this after installing the add-in and before configuring an MCP client. It separates
    "is the bridge working" from "is my MCP client configured", which are otherwise easy to confuse.

    Revit must be running with a model open. Read checks never modify the model.

.PARAMETER Writes
    Also run a write check: it creates a level, renames it, and deletes it again. Needs
    'Allow MCP Writes' enabled on the MCP Bridge ribbon panel. It cleans up after itself even
    if a step fails.

.PARAMETER Verbose
    Show which element ids the checks followed.

.EXAMPLE
    .\run-probe.ps1
    .\run-probe.ps1 -Writes
#>
[CmdletBinding()]
param(
    [switch] $Writes
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

& dotnet build (Join-Path $repoRoot 'tools\RevitMCPProbe\RevitMCPProbe.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }

$probeArgs = @()
if ($Writes) { $probeArgs += '--writes' }
if ($PSBoundParameters['Verbose']) { $probeArgs += '--verbose' }

& dotnet (Join-Path $repoRoot 'artifacts\probe\RevitMCPProbe.dll') @probeArgs
$probeExit = $LASTEXITCODE

Write-Host ''
if ($probeExit -eq 0) {
    Write-Host 'Bridge looks healthy. Next: .\build\build-server.ps1, then point VS Code at it.' -ForegroundColor Green
} else {
    Write-Host 'Some checks failed. See the lines above and %LOCALAPPDATA%\RevitMCPBridge\bridge.log' -ForegroundColor Red
}

exit $probeExit
