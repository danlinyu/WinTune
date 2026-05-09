# WinTune.ps1 -- entry point, builds the WPF window and wires events.
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

Import-Module (Join-Path $ScriptRoot 'modules\Monitor.psm1')  -Force
Import-Module (Join-Path $ScriptRoot 'modules\Cleanup.psm1')  -Force
Import-Module (Join-Path $ScriptRoot 'modules\Boost.psm1')    -Force
Import-Module (Join-Path $ScriptRoot 'modules\Startup.psm1')  -Force
Import-Module (Join-Path $ScriptRoot 'modules\Diagnose.psm1') -Force
Import-Module (Join-Path $ScriptRoot 'modules\Dedup.psm1')    -Force
Import-Module (Join-Path $ScriptRoot 'modules\Async.psm1')    -Force
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
    if ($script:CleanupOp) { Set-Status "Cleanup already running."; return }

    $targets = Get-SelectedTargets
    if (-not $targets -or $targets.Count -eq 0) {
        Set-Status "No cleanup targets selected."
        return
    }

    $ui.CleanRunBtn.IsEnabled    = $false
    $ui.CleanCancelBtn.IsEnabled = $true
    Set-Status "Cleaning $($targets.Count) target(s)..."

    $script:CleanupOp = Start-AsyncOp -Script {
        param($targets, $modPath, $Progress)
        Import-Module $modPath -Force
        Invoke-Cleanup -Targets $targets -OnProgress $Progress
    } -Arguments @{
        targets = $targets
        modPath = (Join-Path $ScriptRoot 'modules\Cleanup.psm1')
    }

    $script:CleanupPoller = New-Object System.Windows.Threading.DispatcherTimer
    $script:CleanupPoller.Interval = [TimeSpan]::FromMilliseconds(150)
    $script:CleanupPoller.Add_Tick({
        try {
            foreach ($info in (Receive-AsyncProgress $script:CleanupOp)) {
                if ($info.Phase -eq 'Start') {
                    Set-Status "[$($info.Index)/$($info.Total)] Cleaning $($info.Target)..."
                }
            }

            if (Test-AsyncOpComplete $script:CleanupOp) {
                $script:CleanupPoller.Stop()
                $r = Receive-AsyncOp $script:CleanupOp
                $script:CleanupOp = $null
                $ui.CleanRunBtn.IsEnabled    = $true
                $ui.CleanCancelBtn.IsEnabled = $false

                if (-not $r.Success) {
                    Set-Status "Cleanup error: $($r.Error)"
                    return
                }

                $results = @($r.Result)
                $totalBytes = ($results | Measure-Object -Property BytesFreed -Sum).Sum
                $display = $results | ForEach-Object {
                    $note = if ($_.Skipped) {
                        $_.SkipReason
                    } elseif ($_.Errors.Count) {
                        $sample = ($_.Errors | Select-Object -First 1) -as [string]
                        if ($sample.Length -gt 90) { $sample = $sample.Substring(0,87) + '...' }
                        "$($_.Errors.Count) error(s): $sample"
                    } else { '' }
                    [pscustomobject]@{
                        Target = $_.Target
                        Status = if ($_.Skipped) { 'Skipped' } elseif ($_.Errors.Count -gt 0) { 'Partial' } else { 'OK' }
                        Items  = $_.FilesRemoved
                        Freed  = (Format-Bytes -Bytes $_.BytesFreed)
                        Note   = $note
                    }
                }
                $ui.CleanResultsGrid.ItemsSource = @($display)
                $ui.CleanTotalLbl.Text = "Freed: $(Format-Bytes -Bytes $totalBytes)"
                $logPath = Get-LastCleanupLog
                if ($logPath) {
                    $ui.CleanOpenLogBtn.IsEnabled = $true
                    $ui.CleanOpenLogBtn.Tag = $logPath
                }
                Set-Status "Cleanup done. Freed $(Format-Bytes -Bytes $totalBytes). Log: $logPath"
            }
        } catch {
            Set-Status "Cleanup poller error: $($_.Exception.Message)"
        }
    })
    $script:CleanupPoller.Start()
})

$ui.CleanCancelBtn.Add_Click({
    if ($script:CleanupOp) {
        Stop-AsyncOp $script:CleanupOp
        $script:CleanupOp = $null
    }
    if ($script:CleanupPoller) { $script:CleanupPoller.Stop(); $script:CleanupPoller = $null }
    $ui.CleanRunBtn.IsEnabled    = $true
    $ui.CleanCancelBtn.IsEnabled = $false
    Set-Status "Cleanup cancelled (in-flight target may still complete)."
})

$ui.CleanOpenLogBtn.Add_Click({
    $p = $ui.CleanOpenLogBtn.Tag
    if ($p -and (Test-Path $p)) {
        Start-Process notepad.exe -ArgumentList "`"$p`""
    } else {
        Set-Status "No log file yet -- run cleanup first."
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

# --- Diagnose tab ---
function Run-Diagnose {
    Set-Status "Running diagnostics..."
    try {
        $f = @(Invoke-Diagnostics)
        $ui.DiagFindingsGrid.ItemsSource = $f
        $red    = ($f | Where-Object Severity -eq 'Red'    | Measure-Object).Count
        $yellow = ($f | Where-Object Severity -eq 'Yellow' | Measure-Object).Count
        $green  = ($f | Where-Object Severity -eq 'Green'  | Measure-Object).Count
        $ui.DiagSummaryLbl.Text = "Findings: $red red, $yellow yellow, $green green"
        $ui.DiagSummaryLbl.Foreground = if ($red -gt 0) { 'Red' } elseif ($yellow -gt 0) { '#FFB58900' } else { '#FF59A14F' }
        Set-Status "Diagnostics done. $red red, $yellow yellow, $green green."
    } catch { Set-Status "Diagnostics error: $($_.Exception.Message)" }
}

$ui.DiagRunBtn.Add_Click({ Run-Diagnose })

$ui.FixResetQABtn.Add_Click({
    Set-Status "Resetting Quick Access..."
    try {
        $r = Reset-QuickAccess
        Set-Status "Quick Access reset: $($r.FilesRemoved) shortcuts removed. $($r.Note)"
        Run-Diagnose
    } catch { Set-Status "Reset error: $($_.Exception.Message)" }
})

$ui.FixDisableTelemetryBtn.Add_Click({
    Set-Status "Disabling DiagTrack..."
    try {
        $r = Disable-Telemetry
        Set-Status ($(if ($r.Success) { "Telemetry disabled. $($r.Note)" } else { "Failed: $($r.Note)" }))
        Run-Diagnose
    } catch { Set-Status "Telemetry fix error: $($_.Exception.Message)" }
})

$ui.FixClassicMenuBtn.Add_Click({
    try {
        $r = Enable-ClassicRightClick
        Set-Status ($(if ($r.Success) { "Classic menu enabled. $($r.Note)" } else { "Failed: $($r.Note)" }))
        Run-Diagnose
    } catch { Set-Status "Classic menu error: $($_.Exception.Message)" }
})

$ui.FixUndoClassicBtn.Add_Click({
    try {
        $r = Disable-ClassicRightClick
        Set-Status ($(if ($r.Success) { "$($r.Note)" } else { "Failed: $($r.Note)" }))
        Run-Diagnose
    } catch { Set-Status "Undo error: $($_.Exception.Message)" }
})

$ui.FixRebuildIndexBtn.Add_Click({
    Set-Status "Triggering Search index rebuild..."
    try {
        $r = Start-SearchIndexRebuild
        Set-Status ($(if ($r.Success) { "Search rebuild: $($r.Note)" } else { "Failed: $($r.Note)" }))
    } catch { Set-Status "Rebuild error: $($_.Exception.Message)" }
})

# --- Dedupe tab ---
# Holds the most recent scan results (groups) so auto-select buttons can act
# without re-scanning.
$script:DedupeGroups = @()

function Reset-DedupePaths {
    $ui.DedupePathsList.Items.Clear()
    foreach ($p in (Get-DefaultScanRoots)) { [void]$ui.DedupePathsList.Items.Add($p) }
}

$ui.DedupeDefaultsBtn.Add_Click({ Reset-DedupePaths })

$ui.DedupeAddPathBtn.Add_Click({
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Pick a folder to scan for duplicates'
    if ($dlg.ShowDialog() -eq 'OK' -and $dlg.SelectedPath) {
        if (-not $ui.DedupePathsList.Items.Contains($dlg.SelectedPath)) {
            [void]$ui.DedupePathsList.Items.Add($dlg.SelectedPath)
        }
    }
})

$ui.DedupeRemovePathBtn.Add_Click({
    $sel = @($ui.DedupePathsList.SelectedItems)
    foreach ($s in $sel) { $ui.DedupePathsList.Items.Remove($s) }
})

$ui.DedupeScanBtn.Add_Click({
    if ($script:DedupeOp) { Set-Status "Dedupe scan already running."; return }

    $paths = @($ui.DedupePathsList.Items)
    if (-not $paths -or $paths.Count -eq 0) {
        Set-Status "No paths to scan. Click 'Defaults' or 'Add path...' first."
        return
    }
    $minSize = [long]$ui.DedupeMinSizeCombo.SelectedItem.Tag

    $ui.DedupeScanBtn.IsEnabled   = $false
    $ui.DedupeCancelBtn.IsEnabled = $true
    $ui.DedupeStatusLbl.Text      = "Scanning..."
    Set-Status "Dedupe scan starting on $($paths.Count) path(s)..."

    $script:DedupeOp = Start-AsyncOp -Script {
        param($paths, $minSize, $modPath, $Progress)
        Import-Module $modPath -Force
        Find-Duplicates -Paths $paths -MinSizeBytes $minSize -OnProgress $Progress
    } -Arguments @{
        paths   = $paths
        minSize = $minSize
        modPath = (Join-Path $ScriptRoot 'modules\Dedup.psm1')
    }

    $script:DedupePoller = New-Object System.Windows.Threading.DispatcherTimer
    $script:DedupePoller.Interval = [TimeSpan]::FromMilliseconds(150)
    $script:DedupePoller.Add_Tick({
        try {
            foreach ($info in (Receive-AsyncProgress $script:DedupeOp)) {
                $msg = switch ($info.Phase) {
                    'Scan'      { "Phase 1/2: scanned $($info.FilesSeen) files..." }
                    'HashStart' { "Phase 2/2: hashing $($info.Candidates) size-collision candidates..." }
                    'Hash'      { "Hashed $($info.Hashed) / $($info.Total)..." }
                    'Done'      { "Done. $($info.Groups) duplicate group(s) found." }
                    default     { '' }
                }
                if ($msg) { $ui.DedupeStatusLbl.Text = $msg }
            }

            if (Test-AsyncOpComplete $script:DedupeOp) {
                $script:DedupePoller.Stop()
                $r = Receive-AsyncOp $script:DedupeOp
                $script:DedupeOp = $null
                $ui.DedupeScanBtn.IsEnabled   = $true
                $ui.DedupeCancelBtn.IsEnabled = $false

                if (-not $r.Success) {
                    $ui.DedupeStatusLbl.Text = "Scan error: $($r.Error)"
                    Set-Status "Dedupe scan error: $($r.Error)"
                    return
                }

                $groups = @($r.Result)
                $script:DedupeGroups = $groups

                $rows = foreach ($g in $groups) {
                    foreach ($f in $g.Files) {
                        [pscustomobject]@{
                            Group     = $g.GroupId
                            Size      = (Format-Bytes -Bytes $g.SizeBytes)
                            Path      = $f.FullName
                            Modified  = $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm')
                            SizeBytes = [long]$g.SizeBytes
                        }
                    }
                }
                $ui.DedupeGrid.ItemsSource = @($rows)

                $totalWasted = ($groups | Measure-Object -Property WastedBytes -Sum).Sum
                if (-not $totalWasted) { $totalWasted = 0 }
                $msg = "Found $($groups.Count) group(s), $((@($rows)).Count) duplicate file(s). Reclaimable: $(Format-Bytes -Bytes $totalWasted)."
                $ui.DedupeStatusLbl.Text = $msg
                Set-Status $msg
            }
        } catch {
            Set-Status "Dedupe poller error: $($_.Exception.Message)"
        }
    })
    $script:DedupePoller.Start()
})

$ui.DedupeCancelBtn.Add_Click({
    if ($script:DedupeOp) {
        Stop-AsyncOp $script:DedupeOp
        $script:DedupeOp = $null
    }
    if ($script:DedupePoller) { $script:DedupePoller.Stop(); $script:DedupePoller = $null }
    $ui.DedupeScanBtn.IsEnabled   = $true
    $ui.DedupeCancelBtn.IsEnabled = $false
    $ui.DedupeStatusLbl.Text      = "Cancelled."
    Set-Status "Dedupe scan cancelled."
})

function Set-DedupeSelection {
    param([scriptblock]$KeepPredicate) # given list of $g.Files, returns the one to KEEP
    if (-not $script:DedupeGroups -or $script:DedupeGroups.Count -eq 0) {
        Set-Status "Run a scan first."
        return
    }
    $ui.DedupeGrid.SelectedItems.Clear()
    $items = @($ui.DedupeGrid.ItemsSource)
    foreach ($g in $script:DedupeGroups) {
        $keep = & $KeepPredicate $g.Files
        $keepPath = $keep.FullName
        foreach ($row in $items) {
            if ($row.Group -eq $g.GroupId -and $row.Path -ne $keepPath) {
                [void]$ui.DedupeGrid.SelectedItems.Add($row)
            }
        }
    }
    $n = $ui.DedupeGrid.SelectedItems.Count
    $totalSelectedBytes = 0L
    foreach ($r in $ui.DedupeGrid.SelectedItems) { $totalSelectedBytes += [long]$r.SizeBytes }
    Set-Status "Selected $n file(s) for deletion ($(Format-Bytes -Bytes $totalSelectedBytes))."
}

$ui.DedupeAutoOldestBtn.Add_Click({
    Set-DedupeSelection { param($files) $files | Sort-Object LastWriteTime | Select-Object -First 1 }
})
$ui.DedupeAutoNewestBtn.Add_Click({
    Set-DedupeSelection { param($files) $files | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }
})
$ui.DedupeAutoShortestPathBtn.Add_Click({
    Set-DedupeSelection { param($files) $files | Sort-Object @{e={$_.FullName.Length}} | Select-Object -First 1 }
})
$ui.DedupeUnselectBtn.Add_Click({ $ui.DedupeGrid.SelectedItems.Clear() })

$ui.DedupeDeleteBtn.Add_Click({
    $sel = @($ui.DedupeGrid.SelectedItems)
    if ($sel.Count -eq 0) {
        Set-Status "No rows selected."
        return
    }

    # Safety: refuse if ALL files in any group are selected.
    $bad = @()
    foreach ($g in $script:DedupeGroups) {
        $groupRows = @($sel | Where-Object { $_.Group -eq $g.GroupId })
        if ($groupRows.Count -ge $g.FileCount) { $bad += $g.GroupId }
    }
    if ($bad.Count -gt 0) {
        [System.Windows.MessageBox]::Show(
            "Cowardly refusing: every file in group(s) $($bad -join ', ') is selected -- you would lose all copies. Untick at least one row per group.",
            "WinTune Dedupe -- safety stop", 'OK', 'Warning') | Out-Null
        return
    }

    $totalBytes = ($sel | Measure-Object -Property SizeBytes -Sum).Sum
    $confirm = [System.Windows.MessageBox]::Show(
        "Send $($sel.Count) file(s) ($(Format-Bytes -Bytes $totalBytes)) to the Recycle Bin?",
        "WinTune Dedupe -- confirm", 'OKCancel', 'Question')
    if ($confirm -ne 'OK') { return }

    $paths = $sel | ForEach-Object { $_.Path }
    Set-Status "Sending $($paths.Count) file(s) to Recycle Bin..."
    try {
        $r = Remove-DuplicateFiles -Paths $paths
        $msg = "Deleted $($r.Deleted) file(s), freed $(Format-Bytes -Bytes $r.BytesFreed)."
        if ($r.Errors.Count) { $msg += " Errors: $($r.Errors.Count)." }
        $ui.DedupeStatusLbl.Text = $msg
        Set-Status $msg

        # Refresh grid: drop deleted rows from view.
        $survivors = @($ui.DedupeGrid.ItemsSource | Where-Object { $paths -notcontains $_.Path })
        $ui.DedupeGrid.ItemsSource = $survivors
    } catch {
        Set-Status "Delete error: $($_.Exception.Message)"
    }
})

# --- Window load ---
$window.Add_Loaded({
    try {
        Update-Dashboard
        $ui.BoostStartupGrid.ItemsSource = @(Get-StartupApps)
        Run-Diagnose
        Reset-DedupePaths
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
