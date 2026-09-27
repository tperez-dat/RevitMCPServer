<#
.SYNOPSIS
    Installs a .NET SDK for the current user only. No administrator rights required.

.DESCRIPTION
    Uses Microsoft's official dotnet-install script, which unpacks into your user profile rather
    than Program Files. This is the supported way to get an SDK on a machine where you cannot
    install software, and it is how the .NET 8 SDK on this machine was most likely installed.

    Your PATH is deliberately left alone. A machine-wide .NET runtime - which Revit installs - sits
    earlier on PATH and would shadow this SDK anyway, so the build scripts locate it directly
    instead of relying on PATH.

.PARAMETER Channel
    Which SDK to install. '10.0' for Revit 2027, '8.0' for Revit 2025 and 2026.

.EXAMPLE
    .\install-dotnet-sdk.ps1                # installs .NET 10 (Revit 2027)
    .\install-dotnet-sdk.ps1 -Channel 8.0   # installs .NET 8  (Revit 2025/2026)
#>
[CmdletBinding()]
param(
    [string] $Channel = '10.0'
)

$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:USERPROFILE '.dotnet'
$scriptPath = Join-Path $env:TEMP 'dotnet-install.ps1'

Write-Host "Installing the .NET $Channel SDK to $installDir" -ForegroundColor Cyan
Write-Host 'This needs no administrator rights and changes nothing outside your profile.' -ForegroundColor DarkGray
Write-Host ''

try {
    Write-Host 'Downloading the official install script...'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $scriptPath -UseBasicParsing
}
catch {
    Write-Host ''
    Write-Host "Could not download the install script: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'If your network blocks it, fetch it manually from https://dot.net/v1/dotnet-install.ps1'
    Write-Host "and run:  powershell -ExecutionPolicy Bypass -File dotnet-install.ps1 -Channel $Channel -InstallDir `"$installDir`""
    exit 1
}

# Invoked through a fresh PowerShell with the policy bypassed for that call only, so a restrictive
# execution policy does not stop it and nothing about the machine's settings is changed.
& powershell -NoProfile -ExecutionPolicy Bypass -File $scriptPath -Channel $Channel -InstallDir $installDir
$installExit = $LASTEXITCODE

Remove-Item -LiteralPath $scriptPath -ErrorAction SilentlyContinue

if ($installExit -ne 0) {
    Write-Host ''
    Write-Host "The install script exited with code $installExit." -ForegroundColor Red
    exit $installExit
}

Write-Host ''
. (Join-Path $PSScriptRoot 'lib\Resolve-Dotnet.ps1')

try {
    $dotnet = Resolve-DotnetPath
    Write-Host "dotnet : $dotnet" -ForegroundColor Green
    Write-Host "SDKs   : $(Get-DotnetSdkSummary $dotnet)" -ForegroundColor Green

    $majors = Get-DotnetSdkMajor $dotnet
    $wanted = [int]($Channel -split '\.')[0]

    Write-Host ''
    if ($majors -contains $wanted) {
        Write-Host "The .NET $wanted SDK is installed and the build scripts will find it." -ForegroundColor Green
        if ($wanted -eq 10) {
            Write-Host 'Close Revit, then run deploy-addin.cmd to build the Revit 2027 add-in.' -ForegroundColor Cyan
        }
    }
    else {
        Write-Host "The .NET $wanted SDK still is not visible. Check the output above." -ForegroundColor Red
        exit 1
    }
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
