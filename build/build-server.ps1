<#
.SYNOPSIS
    Publishes the MCP server to a self-contained folder for an MCP client to launch.

.PARAMETER OutputPath
    Where to publish. Defaults to artifacts\server-publish under the repo root.
#>
[CmdletBinding()]
param(
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

. (Join-Path $PSScriptRoot 'lib\Resolve-Dotnet.ps1')
$dotnet = Resolve-DotnetPath
Write-Host "dotnet : $dotnet  (SDKs: $(Get-DotnetSdkSummary $dotnet))" -ForegroundColor DarkGray

if (-not $OutputPath) {
    $OutputPath = Join-Path $repoRoot 'artifacts\server-publish'
}

& $dotnet publish (Join-Path $repoRoot 'src\RevitMCPServer\RevitMCPServer.csproj') `
    -c Release -o $OutputPath --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$exe = Join-Path $OutputPath 'RevitMCPServer.exe'

Write-Host ''
Write-Host "Published to $OutputPath" -ForegroundColor Green
Write-Host ''

# Verify the published executable before an MCP client is in the picture. A client that shows no
# tools cannot tell you whether the server failed, could not reach Revit, or was misconfigured.
Write-Host '--- self-test ---' -ForegroundColor Cyan
& $exe --selftest
$selfTest = $LASTEXITCODE

Write-Host ''
switch ($selfTest) {
    0 {
        Write-Host 'Ready. Point your MCP client at:' -ForegroundColor Green
        Write-Host "  $exe"
        Write-Host ''
        Write-Host 'See docs\mcp.json.example for a VS Code entry to copy.' -ForegroundColor Cyan
    }
    1 {
        Write-Host 'The server is built and working; Revit just was not reachable.' -ForegroundColor Yellow
        Write-Host 'Start Revit with the MCP Bridge add-in, then re-run the self-test:' -ForegroundColor Yellow
        Write-Host "  $exe --selftest"
        Write-Host ''
        Write-Host 'You can configure your MCP client now regardless - point it at:' -ForegroundColor Cyan
        Write-Host "  $exe"
    }
    default {
        Write-Host 'The server did not register its tools. Do not configure a client yet.' -ForegroundColor Red
    }
}

# A publish that produced a working binary is a success even if Revit happens to be closed.
if ($selfTest -ge 2) { exit 1 }
