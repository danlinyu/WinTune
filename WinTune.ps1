# WinTune.ps1 — entry point, builds the WPF window and wires events.
# Launched via Launch-WinTune.cmd (forces STA + Windows PowerShell 5.1).

#region Self-elevation
$currentPrincipal = [Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    $argList = @('-NoProfile','-STA','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"")
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList | Out-Null
    exit
}
#endregion

#region Imports
$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

Import-Module (Join-Path $ScriptRoot 'modules\Monitor.psm1') -Force
Import-Module (Join-Path $ScriptRoot 'modules\Cleanup.psm1') -Force
Import-Module (Join-Path $ScriptRoot 'modules\Boost.psm1')   -Force
Import-Module (Join-Path $ScriptRoot 'modules\Startup.psm1') -Force
#endregion

#region Load XAML
$xamlPath = Join-Path $ScriptRoot 'ui\MainWindow.xaml'
$xamlText = [System.IO.File]::ReadAllText($xamlPath, [System.Text.UTF8Encoding]::new($false))
[xml]$xaml = $xamlText
$reader = New-Object System.Xml.XmlNodeReader $xaml
try {
    $window = [Windows.Markup.XamlReader]::Load($reader)
} catch {
    [System.Windows.MessageBox]::Show(
        "Failed to load XAML:`n$($_.Exception.Message)",
        "WinTune startup error", 'OK', 'Error') | Out-Null
    exit 1
}

# Helper: pull every named element into a hashtable.
$ui = @{}
$xaml.SelectNodes("//*[@*[local-name()='Name']]") | ForEach-Object {
    $name = $_.GetAttribute('Name','http://schemas.microsoft.com/winfx/2006/xaml')
    if ($name) { $ui[$name] = $window.FindName($name) }
}
#endregion

#region Helpers
function Set-Status {
    param([string]$Message)
    $stamp = (Get-Date).ToString('HH:mm:ss')
    $ui.StatusBarText.Text = "[$stamp] $Message"
}

function Update-Dashboard {
    $snap = Get-PerfSnapshot
    $ui.DashCpuBar.Value  = [math]::Min(100, $snap.CpuPct)
    $ui.DashCpuLbl.Text   = "{0}%" -f $snap.CpuPct
    $ui.DashRamBar.Value  = [math]::Min(100, $snap.RamPct)
    $ui.DashRamLbl.Text   = "{0:N1} / {1:N1} GB" -f $snap.RamUsedGB, $snap.RamTotalGB
    $ui.DashDiskBar.Value = [math]::Min(100, $snap.DiskPct)
    $ui.DashDiskLbl.Text  = "{0:N1} / {1:N1} GB free" -f $snap.DiskFreeGB, $snap.DiskTotalGB

    $ui.DashProcessGrid.ItemsSource = @(Get-TopProcesses -Count 10)
}

function Get-SelectedTargets {
    $map = @{
        ChkUserTemp      = 'UserTemp'
        ChkSystemTemp    = 'SystemTemp'
        ChkPrefetch      = 'Prefetch'
        ChkWER           = 'WER'
        ChkWindowsUpdate = 'WindowsUpdate'
        ChkEdgeCache     = 'EdgeCache'
        ChkChromeCache   = 'ChromeCache'
        ChkFirefoxCache  = 'FirefoxCache'
        ChkRecycleBin    = 'RecycleBin'
        ChkDNSCache      = 'DNSCache'
    }
    $picked = foreach ($k in $map.Keys) {
        if ($ui[$k].IsChecked) { $map[$k] }
    }
    return @($picked)
}
#endregion

#region Wire events

# --- Dashboard auto-refresh ---
$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromSeconds(2)
$timer.Add_Tick({ try { Update-Dashboard } catch { Set-Status "Dashboard refresh failed: $($_.Exception.Message)" } })

# --- Cleanup tab ---
$ui.CleanSelectAllBtn.Add_Click({
    foreach ($n in 'ChkUserTemp','ChkSystemTemp','ChkPrefetch','ChkWER','ChkWindowsUpdate','ChkEdgeCache','ChkChromeCache','ChkFirefoxCache','ChkRecycleBin','ChkDNSCache') {
        $ui[$n].IsChecked = $true
    }
})
$ui.CleanSelectNoneBtn.Add_Click({
    foreach ($n in 'ChkUserTemp','ChkSystemTemp','ChkPrefetch','ChkWER','ChkWindowsUpdate','ChkEdgeCache','ChkChromeCache','ChkFirefoxCache','ChkRecycleBin','ChkDNSCache') {
        $ui[$n].IsChecked = $false
    }
})

$ui.CleanRunBtn.Add_Click({
    $targets = Get-SelectedTargets
    if (-not $targets -or $targets.Count -eq 0) {
        Set-Status "No cleanup targets selected."
        return
    }
    $ui.CleanRunBtn.IsEnabled = $false
    Set-Status "Cleaning $($targets.Count) target(s)..."
    try {
        $results = Invoke-Cleanup -Targets $targets
        $totalBytes = ($results | Measure-Object -Property BytesFreed -Sum).Sum
        $display = $results | ForEach-Object {
            [pscustomobject]@{
                Target     = $_.Target
                Status     = if ($_.Skipped) { 'Skipped' } elseif ($_.Errors.Count -gt 0) { 'Partial' } else { 'OK' }
                Items      = $_.FilesRemoved
                Freed      = (Format-Bytes -Bytes $_.BytesFreed)
                Note       = if ($_.Skipped) { $_.SkipReason } elseif ($_.Errors.Count) { "$($_.Errors.Count) error(s)" } else { '' }
            }
        }
        $ui.CleanResultsGrid.ItemsSource = @($display)
        $ui.CleanTotalLbl.Text = "Freed: $(Format-Bytes -Bytes $totalBytes)"
        Set-Status "Cleanup done. Freed $(Format-Bytes -Bytes $totalBytes)."
    } catch {
        Set-Status "Cleanup error: $($_.Exception.Message)"
    } finally {
        $ui.CleanRunBtn.IsEnabled = $true
    }
})

# --- Boost tab ---
$ui.BoostFreeRamBtn.Add_Click({
    Set-Status "Trimming working sets..."
    $ui.BoostFreeRamBtn.IsEnabled = $false
    try {
        $r = Clear-WorkingSets
        $msg = "Trimmed $($r.ProcessesTrimmed) process(es) (skipped $($r.ProcessesSkipped)). Freed approx $(Format-Bytes -Bytes $r.BytesFreed)."
        $ui.BoostResultLbl.Text = $msg
        Set-Status $msg
    } catch {
        Set-Status "Free RAM error: $($_.Exception.Message)"
    } finally {
        $ui.BoostFreeRamBtn.IsEnabled = $true
    }
})

$ui.BoostRestartExplorerBtn.Add_Click({
    Set-Status "Restarting Explorer..."
    try {
        $r = Restart-Explorer
        $ui.BoostResultLbl.Text = "Restarted $($r.Restarted) Explorer instance(s)."
        Set-Status "Explorer restarted."
    } catch { Set-Status "Restart error: $($_.Exception.Message)" }
})

$ui.BoostFlushDnsBtn.Add_Click({
    $r = Clear-DNSCacheSafe
    if ($r.Success) {
        $ui.BoostResultLbl.Text = "DNS resolver cache flushed."
        Set-Status "DNS cache flushed."
    } else {
        Set-Status "DNS flush failed: $($r.Error)"
    }
})

$ui.BoostOpenTaskMgrBtn.Add_Click({ Open-StartupTaskManager; Set-Status "Task Manager opened." })

# --- Window load ---
$window.Add_Loaded({
    try {
        Update-Dashboard
        $ui.BoostStartupGrid.ItemsSource = @(Get-StartupApps)
        $timer.Start()
        Set-Status "WinTune ready. Running as Administrator."
    } catch {
        Set-Status "Startup error: $($_.Exception.Message)"
    }
})

$window.Add_Closed({ try { $timer.Stop() } catch {} })
#endregion

# Show the window. Suppress null output when WPF returns Nullable<bool>.
$null = $window.ShowDialog()
