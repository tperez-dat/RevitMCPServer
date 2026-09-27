<#
.SYNOPSIS
    Builds the Revit MCP Bridge add-in and installs it for one or more Revit versions.

.DESCRIPTION
    Copies the built assemblies and the .addin manifest into the per-user Revit Addins folder.
    Revit must be closed: it locks the add-in assembly while running.

.PARAMETER RevitVersions
    Which Revit releases to install for. Defaults to all supported (2025, 2026, 2027).

.PARAMETER Configuration
    Debug or Release. Defaults to Release.

.EXAMPLE
    .\deploy-addin.ps1
    .\deploy-addin.ps1 -RevitVersions 2026
#>
[CmdletBinding()]
param(
    [ValidateSet('2025', '2026', '2027')]
    [string[]] $RevitVersions = @('2025', '2026', '2027'),

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# Revit 2027 needs the .NET 10 SDK; 2025 and 2026 need .NET 8.
$sdkForVersion = @{ '2025' = '8'; '2026' = '8'; '2027' = '10' }

if (Get-Process -Name 'Revit' -ErrorAction SilentlyContinue) {
    throw 'Revit is running and locks the add-in assembly. Close Revit and run this again.'
}

foreach ($version in $RevitVersions) {
    Write-Host "=== Revit $version (.NET $($sdkForVersion[$version])) ===" -ForegroundColor Cyan

    $project = Join-Path $repoRoot 'src\RevitMCPBridge\RevitMCPBridge.csproj'
    & dotnet build $project -c $Configuration "-p:RevitVersion=$version" --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed for Revit $version." }

    $source = Join-Path $repoRoot "artifacts\bridge\$version"
    $target = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$version"

    if (-not (Test-Path $source)) { throw "No build output at $source." }
    New-Item -ItemType Directory -Force -Path $target | Out-Null

    # The manifest must sit at the Addins root; Revit does not search subfolders for it.
    Copy-Item (Join-Path $source 'RevitMCPBridge.addin') $target -Force

    # Assemblies go in a subfolder so they cannot collide with another vendor's add-in.
    $assemblyTarget = Join-Path $target 'RevitMCPBridge'
    New-Item -ItemType Directory -Force -Path $assemblyTarget | Out-Null
    Copy-Item (Join-Path $source '*.dll') $assemblyTarget -Force

    # Point the manifest at that subfolder.
    $manifest = Join-Path $target 'RevitMCPBridge.addin'
    (Get-Content $manifest -Raw).Replace(
        '<Assembly>RevitMCPBridge.dll</Assembly>',
        '<Assembly>RevitMCPBridge\RevitMCPBridge.dll</Assembly>') |
        Set-Content $manifest -Encoding UTF8

    Write-Host "  installed to $target" -ForegroundColor Green
}

Write-Host ''
Write-Host 'Done. Start Revit and look for the "MCP Bridge" ribbon tab.' -ForegroundColor Green
Write-Host 'Writes are off until you enable them there.' -ForegroundColor Yellow
