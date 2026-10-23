#!/usr/bin/env pwsh
# Environment check for ScreenFlow (task T00).
#
# Requires a .NET 8 SDK and Python 3.11 or newer. A missing prerequisite is not a code problem, so
# this script only reports: it never installs anything and never relaxes the requirement.
# Exit code 0 = ready, 1 = something is missing.

$ErrorActionPreference = 'Continue'
$missing = @()

Write-Host '== dotnet --list-sdks =='
$sdks = @()
try {
    $sdks = & dotnet --list-sdks 2>$null
} catch {
    $sdks = @()
}

if (-not $sdks -or $sdks.Count -eq 0) {
    Write-Host '  (dotnet not found on PATH)'
    $missing += '.NET 8 SDK (dotnet CLI not found on PATH)'
} else {
    $sdks | ForEach-Object { Write-Host "  $_" }
    $hasNet8 = $false
    foreach ($line in $sdks) {
        if ($line -match '^\s*8\.') { $hasNet8 = $true }
    }
    if (-not $hasNet8) {
        $installed = ($sdks | ForEach-Object { ($_ -split ' ')[0] }) -join ', '
        $missing += ".NET 8 SDK (installed SDKs: $installed)"
    }
}

Write-Host ''
Write-Host '== python --version =='
$pythonVersion = $null
foreach ($candidate in @('python', 'python3', 'py')) {
    try {
        $output = & $candidate --version 2>&1
    } catch {
        continue
    }
    if ($LASTEXITCODE -eq 0 -and $output -match 'Python\s+(\d+)\.(\d+)') {
        $pythonVersion = [pscustomobject]@{
            Command = $candidate
            Text    = ($output | Out-String).Trim()
            Major   = [int]$Matches[1]
            Minor   = [int]$Matches[2]
        }
        break
    }
}

if ($null -eq $pythonVersion) {
    Write-Host '  (python not found on PATH)'
    $missing += 'Python 3.11+ (python not found on PATH)'
} else {
    Write-Host "  $($pythonVersion.Text)  [$($pythonVersion.Command)]"
    $tooOld = ($pythonVersion.Major -lt 3) -or
              ($pythonVersion.Major -eq 3 -and $pythonVersion.Minor -lt 11)
    if ($tooOld) {
        $missing += "Python 3.11+ (found $($pythonVersion.Major).$($pythonVersion.Minor))"
    }
}

Write-Host ''
if ($missing.Count -gt 0) {
    Write-Host 'Environment check FAILED. Missing:' -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host 'Environment check passed.' -ForegroundColor Green
exit 0
