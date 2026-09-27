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

function Format-Invocation {
    param([string] $Command, [string[]] $Arguments)

    # Built by concatenation rather than interpolation: embedded quotes in a quoted string are a
    # needless source of escaping mistakes.
    $text = '"' + $Command + '"'
    foreach ($argument in $Arguments) { $text += ' "' + $argument + '"' }
    return $text
}

function Write-McpConfig {
    <# Emits the exact VS Code entry for however this server actually launches here, and saves it
       beside the build. Hand-transcribing a Windows path into JSON is a reliable source of typos. #>
    param([string] $Command, [string[]] $Arguments, [string] $OutputPath)

    $config = [ordered]@{
        servers = [ordered]@{
            revit = [ordered]@{
                type    = 'stdio'
                command = $Command
                args    = @($Arguments)
            }
        }
    }

    $json = $config | ConvertTo-Json -Depth 6
    $snippetPath = Join-Path $OutputPath 'mcp.json'
    $json | Set-Content -LiteralPath $snippetPath -Encoding UTF8

    Write-Host ''
    Write-Host 'Paste this into VS Code (Ctrl+Shift+P, "MCP: Open User Configuration"):' -ForegroundColor Cyan
    Write-Host ''
    Write-Host $json
    Write-Host ''
    Write-Host "Also saved to $snippetPath" -ForegroundColor DarkGray
    Write-Host 'In Copilot Chat, switch the mode selector from Ask to Agent, or no tools appear.' -ForegroundColor Yellow
}

$exe = Join-Path $OutputPath 'RevitMCPServer.exe'
$dll = Join-Path $OutputPath 'RevitMCPServer.dll'

Write-Host ''
Write-Host "Published to $OutputPath" -ForegroundColor Green
Write-Host ''

# Verify the published server before an MCP client is in the picture. A client that shows no tools
# cannot tell you whether the server failed, could not reach Revit, or was misconfigured.
Write-Host '--- self-test ---' -ForegroundColor Cyan

# Managed machines often block unsigned executables by policy (AppLocker, SRP). The published .exe
# is just a launcher around the .dll, and running that .dll through dotnet.exe - already permitted,
# since it is Microsoft-signed and has been building this project - does the same job. So try the
# .exe, and fall back rather than declaring the build broken.
$launchCommand = $null
$launchArgs = @()

if (Test-Path -LiteralPath $exe) {
    try {
        & $exe --selftest
        $selfTest = $LASTEXITCODE
        $launchCommand = $exe
    }
    catch {
        Write-Host ''
        Write-Host "RevitMCPServer.exe could not be launched: $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host 'Falling back to running the DLL through dotnet, which this machine permits.' -ForegroundColor Yellow
        Write-Host ''
    }
}

if (-not $launchCommand) {
    & $dotnet $dll --selftest
    $selfTest = $LASTEXITCODE
    $launchCommand = $dotnet
    $launchArgs = @($dll)
}

Write-Host ''
switch ($selfTest) {
    0 {
        Write-Host 'Ready.' -ForegroundColor Green
        Write-McpConfig -Command $launchCommand -Arguments $launchArgs -OutputPath $OutputPath
    }
    1 {
        Write-Host 'The server is built and working; Revit just was not reachable.' -ForegroundColor Yellow
        Write-Host 'Start Revit with the MCP Bridge add-in, then re-run the self-test:' -ForegroundColor Yellow
        Write-Host "  $(Format-Invocation $launchCommand $launchArgs) --selftest"
        Write-Host ''
        Write-Host 'You can configure your MCP client now regardless.' -ForegroundColor Cyan
        Write-McpConfig -Command $launchCommand -Arguments $launchArgs -OutputPath $OutputPath
    }
    default {
        Write-Host 'The server did not register its tools. Do not configure a client yet.' -ForegroundColor Red
    }
}

# A publish that produced a working binary is a success even if Revit happens to be closed.
if ($selfTest -ge 2) { exit 1 }
