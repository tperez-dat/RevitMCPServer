<#
    Finds a dotnet that actually has an SDK.

    Revit installs the .NET runtime machine-wide, which puts C:\Program Files\dotnet on the system
    PATH. That dotnet can run apps but cannot build, and because Windows composes PATH as system
    entries first and user entries second, it shadows a per-user SDK install in %USERPROFILE%\.dotnet
    no matter where in the user PATH that appears. A plain `dotnet build` then fails with a message
    about the SDK that does not mention the real cause.

    So rather than trusting PATH, probe each candidate and take the first that reports an SDK.
#>

function Resolve-DotnetPath {
    [CmdletBinding()]
    param()

    $candidates = [System.Collections.Generic.List[string]]::new()

    # Every dotnet on PATH, in PATH order - not just the first. The first may be the runtime-only
    # one, while a usable SDK sits further along, so all of them are worth probing.
    $onPath = Get-Command dotnet -CommandType Application -All -ErrorAction SilentlyContinue
    foreach ($command in @($onPath)) {
        if ($command) { $candidates.Add($command.Source) }
    }

    # The per-user install locations dotnet-install.ps1 uses, which need no admin rights.
    if ($env:DOTNET_ROOT)   { $candidates.Add((Join-Path $env:DOTNET_ROOT   'dotnet.exe')) }
    if ($env:USERPROFILE)   { $candidates.Add((Join-Path $env:USERPROFILE   '.dotnet\dotnet.exe')) }
    if ($env:LOCALAPPDATA)  { $candidates.Add((Join-Path $env:LOCALAPPDATA  'Microsoft\dotnet\dotnet.exe')) }

    # Machine-wide installs last: this is the one Revit's runtime leaves behind.
    $candidates.Add('C:\Program Files\dotnet\dotnet.exe')
    $candidates.Add('C:\Program Files (x86)\dotnet\dotnet.exe')

    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $runtimeOnly = [System.Collections.Generic.List[string]]::new()

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if (-not $seen.Add($candidate)) { continue }
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }

        # --list-sdks exits 0 and prints nothing when no SDK is present, so test the output.
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) {
            Write-Verbose "Using dotnet at $candidate"
            return $candidate
        }

        $runtimeOnly.Add($candidate)
    }

    $message = [System.Text.StringBuilder]::new()
    [void]$message.AppendLine('No .NET SDK was found, so nothing can be built.')

    if ($runtimeOnly.Count -gt 0) {
        [void]$message.AppendLine('')
        [void]$message.AppendLine('These dotnet installs were found but contain only a runtime, not an SDK:')
        foreach ($path in $runtimeOnly) { [void]$message.AppendLine("  $path") }
        [void]$message.AppendLine('A runtime can run .NET apps but cannot compile them.')
    }

    [void]$message.AppendLine('')
    [void]$message.AppendLine('Install the SDK for your user only - no admin rights needed:')
    [void]$message.AppendLine('  Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1')
    [void]$message.AppendLine('  powershell -NoProfile -ExecutionPolicy Bypass -File .\dotnet-install.ps1 -Channel 8.0')
    [void]$message.AppendLine('')
    [void]$message.AppendLine('Use -Channel 10.0 as well if you are targeting Revit 2027.')

    throw $message.ToString()
}

function Get-DotnetSdkMajor {
    <# The major versions available, e.g. @(8, 10). Used to skip a Revit release whose SDK is absent
       rather than failing the whole install for it. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $DotnetPath)

    $sdks = & $DotnetPath --list-sdks 2>$null
    $majors = foreach ($line in $sdks) {
        if ($line -match '^\s*(\d+)\.') { [int]$Matches[1] }
    }

    return @($majors | Sort-Object -Unique)
}

function Get-InstalledRevitVersion {
    <# Revit releases present on this machine, newest first. Building for a Revit that is not
       installed wastes time and produces output nobody will load. #>
    [CmdletBinding()]
    param([string[]] $Supported = @('2025', '2026', '2027'))

    # Absent off Windows, and conceivably redirected on it.
    $programFiles = ${env:ProgramFiles}
    if ([string]::IsNullOrWhiteSpace($programFiles)) { return @() }

    $found = foreach ($version in $Supported) {
        $exe = Join-Path $programFiles "Autodesk\Revit $version\Revit.exe"
        if (Test-Path -LiteralPath $exe -PathType Leaf) { $version }
    }

    return @($found)
}

function Get-DotnetSdkSummary {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $DotnetPath)

    $sdks = & $DotnetPath --list-sdks 2>$null
    return ($sdks | ForEach-Object { ($_ -split ' ')[0] }) -join ', '
}
