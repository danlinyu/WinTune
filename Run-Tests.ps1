# Run-Tests.ps1 -- runs PSScriptAnalyzer + Pester. Used locally and by CI.
#
# Usage:
#   .\Run-Tests.ps1               # both
#   .\Run-Tests.ps1 -NoAnalyze    # Pester only
#   .\Run-Tests.ps1 -NoPester     # PSSA only
#
# Exits with non-zero code if either gate fails (suitable for CI).

[CmdletBinding()]
param(
    [switch]$NoAnalyze,
    [switch]$NoPester
)

$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$failed = 0

if (-not $NoAnalyze) {
    Write-Host '=== PSScriptAnalyzer ===' -ForegroundColor Cyan
    Import-Module PSScriptAnalyzer -ErrorAction Stop
    $settings = Join-Path $root 'PSScriptAnalyzerSettings.psd1'
    $results = Invoke-ScriptAnalyzer -Path $root -Recurse -Settings $settings
    if ($results) {
        $results | Format-Table -AutoSize Severity, RuleName, ScriptName, Line, Message
        Write-Host "PSScriptAnalyzer: $($results.Count) issue(s)" -ForegroundColor Red
        $failed = 1
    } else {
        Write-Host 'PSScriptAnalyzer: clean' -ForegroundColor Green
    }
}

if (-not $NoPester) {
    Write-Host ''
    Write-Host '=== Pester ===' -ForegroundColor Cyan
    Import-Module Pester -MinimumVersion 5.5.0 -ErrorAction Stop
    $config = New-PesterConfiguration
    $config.Run.Path                = (Join-Path $root 'tests')
    $config.Output.Verbosity        = 'Detailed'
    $config.TestResult.Enabled      = $true
    $config.TestResult.OutputPath   = (Join-Path $root 'tests\results.xml')
    $config.TestResult.OutputFormat = 'NUnitXml'
    $r = Invoke-Pester -Configuration $config
    if ($r.FailedCount -gt 0) {
        Write-Host "Pester: $($r.FailedCount) failure(s)" -ForegroundColor Red
        $failed = 1
    }
}

exit $failed
