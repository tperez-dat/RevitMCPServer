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

Write-Host ''
Write-Host "Published to $OutputPath" -ForegroundColor Green
Write-Host 'Point your MCP client at:' -ForegroundColor Cyan
Write-Host "  $(Join-Path $OutputPath 'RevitMCPServer.exe')"
