<#
.SYNOPSIS
    Builds the Revit MCP Bridge add-in and installs it for the Revit versions on this machine.

.DESCRIPTION
    Copies the built assemblies and the .addin manifest into the per-user Revit Addins folder.
    Revit must be closed: it locks the add-in assembly while running.

    With no -RevitVersions, this installs for whichever Revit releases are actually installed.
    A release whose .NET SDK is missing is skipped with an explanation rather than failing the run,
    so having only the .NET 8 SDK still gets you a working Revit 2025/2026 install.

.PARAMETER RevitVersions
    Which Revit releases to build for. Defaults to those detected on this machine.

.PARAMETER Configuration
    Debug or Release. Defaults to Release.

.EXAMPLE
    .\deploy-addin.ps1
    .\deploy-addin.ps1 -RevitVersions 2026
#>
[CmdletBinding()]
param(
    [ValidateSet('2025', '2026', '2027')]
    [string[]] $RevitVersions,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

. (Join-Path $PSScriptRoot 'lib\Resolve-Dotnet.ps1')
$dotnet = Resolve-DotnetPath
$sdkMajors = Get-DotnetSdkMajor $dotnet
Write-Host "dotnet : $dotnet  (SDKs: $(Get-DotnetSdkSummary $dotnet))" -ForegroundColor DarkGray

# Revit 2027 moved to .NET 10; 2025 and 2026 are .NET 8.
$sdkForVersion = @{ '2025' = 8; '2026' = 8; '2027' = 10 }

if (Get-Process -Name 'Revit' -ErrorAction SilentlyContinue) {
    throw 'Revit is running and locks the add-in assembly. Close Revit and run this again.'
}

if (-not $RevitVersions) {
    $RevitVersions = Get-InstalledRevitVersion
    if ($RevitVersions) {
        Write-Host "Revit  : found $($RevitVersions -join ', ')" -ForegroundColor DarkGray
    }
    else {
        # A non-default install location defeats detection, so build everything the SDKs allow
        # rather than refusing to do anything.
        $RevitVersions = @('2025', '2026', '2027') | Where-Object { $sdkMajors -contains $sdkForVersion[$_] }
        Write-Host 'Revit  : none detected in the default location; building every version this SDK supports.' -ForegroundColor Yellow
    }
}

if (-not $RevitVersions) {
    throw 'Nothing to build: no Revit installation was detected and no supported SDK is present.'
}

$installed = [System.Collections.Generic.List[string]]::new()
$skipped   = [System.Collections.Generic.List[string]]::new()

foreach ($version in $RevitVersions) {
    $neededSdk = $sdkForVersion[$version]

    if ($sdkMajors -notcontains $neededSdk) {
        Write-Host ''
        Write-Host "=== Revit $version - SKIPPED ===" -ForegroundColor Yellow
        Write-Host "  Needs the .NET $neededSdk SDK, which is not installed (found: $($sdkMajors -join ', '))."
        Write-Host "  To add it for your user only, no admin rights required:"
        Write-Host "    .\dotnet-install.ps1 -Channel $neededSdk.0"
        $skipped.Add("$version (no .NET $neededSdk SDK)")
        continue
    }

    Write-Host ''
    Write-Host "=== Revit $version (.NET $neededSdk) ===" -ForegroundColor Cyan

    $project = Join-Path $repoRoot 'src\RevitMCPBridge\RevitMCPBridge.csproj'
    & $dotnet build $project -c $Configuration "-p:RevitVersion=$version" --nologo
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
    $installed.Add($version)
}

Write-Host ''
if ($installed.Count -gt 0) {
    Write-Host "Installed for Revit $($installed -join ', ')." -ForegroundColor Green
    Write-Host 'Start Revit and look for the "MCP Bridge" ribbon tab.' -ForegroundColor Green
    Write-Host 'Writes are off until you enable them there.' -ForegroundColor Yellow
}
else {
    Write-Host 'Nothing was installed.' -ForegroundColor Red
}

if ($skipped.Count -gt 0) {
    Write-Host ''
    Write-Host "Skipped: $($skipped -join '; ')" -ForegroundColor Yellow
}

# Skipping a version the machine cannot build is not a failure of this run.
if ($installed.Count -eq 0) { exit 1 }
