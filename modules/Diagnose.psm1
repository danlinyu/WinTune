# Diagnose.psm1 -- Explorer-slowness diagnostics + safe one-click fixes.
#
# Each diagnostic returns one row:
#   Severity : 'Red' (action needed), 'Yellow' (worth a look), 'Green' (OK)
#   Title    : short label
#   Detail   : human-readable finding
#   Hint     : suggested fix (free text)

function New-Finding {
    param(
        [ValidateSet('Red','Yellow','Green')]$Severity,
        [string]$Title,
        [string]$Detail,
        [string]$Hint = ''
    )
    [pscustomobject]@{
        Severity = $Severity
        Title    = $Title
        Detail   = $Detail
        Hint     = $Hint
    }
}

function Invoke-Diagnostics {
    [CmdletBinding()]
    param()

    $findings = New-Object System.Collections.Generic.List[object]

    # --- Disk health
    $bad = Get-PhysicalDisk | Where-Object { $_.HealthStatus -ne 'Healthy' -or $_.OperationalStatus -ne 'OK' }
    if ($bad) {
        foreach ($d in $bad) {
            $findings.Add( (New-Finding Red 'Disk health warning' `
                "$($d.FriendlyName): Health=$($d.HealthStatus), Op=$($d.OperationalStatus)" `
                'Back up data immediately. Run vendor diagnostic tool.') )
        }
    } else {
        $findings.Add( (New-Finding Green 'Disk health' 'All physical disks Healthy / OK.' '') )
    }

    # --- Disk free space
    Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Used -and $_.Free } | ForEach-Object {
        $totalGB = [math]::Round(($_.Used + $_.Free) / 1GB, 1)
        $freeGB  = [math]::Round($_.Free / 1GB, 1)
        $pct     = [math]::Round($_.Free / ($_.Used + $_.Free) * 100, 1)
        $sev = if ($pct -lt 10) { 'Red' } elseif ($pct -lt 15) { 'Yellow' } else { 'Green' }
        if ($sev -ne 'Green') {
            $findings.Add( (New-Finding $sev "Drive $($_.Name): low free space" `
                "$freeGB GB free of $totalGB GB ($pct%)" `
                'Win11 throttles disk I/O below ~10% free. Free space or move data.') )
        }
    }

    # --- Cloud sync shell extensions in explorer.exe
    $expl = Get-Process -Name explorer -ErrorAction SilentlyContinue | Select-Object -First 1
    $cloudHits = @()
    if ($expl) {
        try {
            $expl.Modules | ForEach-Object {
                if ($_.FileName -match 'OneDrive|FileSyncShell|drivefsext|GoogleDrive|Dropbox|Box\\') {
                    $cloudHits += $_.ModuleName
                }
            }
        } catch {}
    }
    if ($cloudHits.Count -gt 1) {
        $findings.Add( (New-Finding Red 'Multiple cloud shell extensions loaded' `
            "Explorer has $($cloudHits.Count) cloud DLLs: $($cloudHits -join ', ')" `
            'Pick ONE cloud, set BOTH to online-only / stream mode. Each shell ext queries cloud per file per folder open.') )
    } elseif ($cloudHits.Count -eq 1) {
        $findings.Add( (New-Finding Yellow 'Cloud shell extension loaded' `
            "Explorer has 1 cloud DLL: $($cloudHits[0])" `
            'OK if intentional. Set Files On-Demand / Stream mode to avoid local copies.') )
    }

    # --- Quick Access bloat
    $autoCount = (Get-ChildItem "$env:APPDATA\Microsoft\Windows\Recent\AutomaticDestinations" -ErrorAction SilentlyContinue | Measure-Object).Count
    $recentCount = (Get-ChildItem "$env:APPDATA\Microsoft\Windows\Recent" -ErrorAction SilentlyContinue -File | Measure-Object).Count
    $totalQA = $autoCount + $recentCount
    $sev = if ($totalQA -gt 300) { 'Red' } elseif ($totalQA -gt 100) { 'Yellow' } else { 'Green' }
    if ($sev -eq 'Green') {
        $findings.Add( (New-Finding Green 'Quick Access' "$totalQA recent entries" '') )
    } else {
        $findings.Add( (New-Finding $sev 'Quick Access bloat' `
            "$autoCount AutomaticDestinations + $recentCount Recent shortcuts = $totalQA total" `
            'Stale entries referencing dead network paths cause Explorer to wait on timeout. Use "Reset Quick Access".') )
    }

    # --- Windows Search index
    $idxBase = Join-Path $env:ProgramData 'Microsoft\Search\Data\Applications\Windows\Projects\SystemIndex\Indexer\CiFiles'
    if (Test-Path $idxBase) {
        $idxBytes = (Get-ChildItem $idxBase -Recurse -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum).Sum
        $idxMB = [math]::Round($idxBytes / 1MB)
        if ($idxMB -lt 50) {
            $findings.Add( (New-Finding Red 'Windows Search index empty' `
                "Index size: $idxMB MB (expected: hundreds of MB)" `
                'Quick Access "Frequent" falls back to full FS scan. Use "Rebuild Search Index".') )
        } elseif ($idxMB -gt 5000) {
            $findings.Add( (New-Finding Yellow 'Windows Search index large' `
                "Index size: $idxMB MB" `
                'Trim indexed locations: Settings -> Searching Windows -> Find My Files -> Customize.') )
        } else {
            $findings.Add( (New-Finding Green 'Windows Search index' "$idxMB MB" '') )
        }
    } else {
        $findings.Add( (New-Finding Yellow 'Windows Search index path missing' `
            'CiFiles folder not found.' 'Use "Rebuild Search Index".') )
    }

    # --- DiagTrack telemetry service
    $diag = Get-Service DiagTrack -ErrorAction SilentlyContinue
    if ($diag -and $diag.Status -eq 'Running') {
        $findings.Add( (New-Finding Yellow 'Telemetry (DiagTrack) running' `
            "Status: $($diag.Status), StartType: $($diag.StartType)" `
            'Background disk + network I/O. Use "Disable Telemetry" -- no functional loss.') )
    }

    # --- Win11 classic right-click menu
    $classicKey = 'HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32'
    if (Test-Path $classicKey) {
        $findings.Add( (New-Finding Green 'Right-click menu' 'Classic Win10 menu enabled.' '') )
    } else {
        $findings.Add( (New-Finding Yellow 'Win11 right-click menu uses overlay' `
            'Each right-click loads the new menu PLUS a "Show more options" indirection.' `
            'Use "Apply Classic Right-Click" -- restores Win10-style instant menu.') )
    }

    # --- Pagefile on slow / nearly-full drive
    try {
        $pf = Get-CimInstance Win32_PageFileSetting -ErrorAction SilentlyContinue
        if ($pf) {
            foreach ($p in $pf) {
                $drv = [string]$p.Name -replace '^([A-Z]):.*','$1'
                $psd = Get-PSDrive -Name $drv -ErrorAction SilentlyContinue
                if ($psd -and ($psd.Used + $psd.Free) -gt 0) {
                    $freePct = [math]::Round($psd.Free / ($psd.Used + $psd.Free) * 100, 1)
                    if ($freePct -lt 15) {
                        $findings.Add( (New-Finding Red 'Pagefile on full drive' `
                            "Pagefile $($p.Name) on drive with $freePct% free." `
                            'Move pagefile to drive with >20% free. Settings -> Performance -> Advanced -> Virtual Memory.') )
                    }
                }
            }
        }
    } catch {}

    # --- Heavy startup app count
    try {
        $startup = Get-CimInstance Win32_StartupCommand -ErrorAction SilentlyContinue
        $cnt = ($startup | Measure-Object).Count
        if ($cnt -gt 15) {
            $findings.Add( (New-Finding Yellow 'Many startup programs' `
                "$cnt entries in Win32_StartupCommand" `
                'Open Task Manager Startup tab and disable items you don''t recognize.') )
        }
    } catch {}

    # --- RAM pressure
    $os = Get-CimInstance Win32_OperatingSystem
    $ramFreePct = [math]::Round($os.FreePhysicalMemory / $os.TotalVisibleMemorySize * 100, 1)
    if ($ramFreePct -lt 10) {
        $findings.Add( (New-Finding Red 'RAM pressure high' `
            "Only $ramFreePct% free" `
            'Use Boost tab -> "Free RAM (Empty Working Sets)".') )
    }

    return ,$findings.ToArray()
}

# =====================================================================
# Safe one-click fixes
# =====================================================================

function Reset-QuickAccess {
    [CmdletBinding()]
    param()

    $paths = @(
        "$env:APPDATA\Microsoft\Windows\Recent",
        "$env:APPDATA\Microsoft\Windows\Recent\AutomaticDestinations",
        "$env:APPDATA\Microsoft\Windows\Recent\CustomDestinations"
    )

    $removed = 0
    $errors  = @()
    foreach ($p in $paths) {
        if (Test-Path $p) {
            Get-ChildItem -LiteralPath $p -Force -ErrorAction SilentlyContinue | ForEach-Object {
                if (-not $_.PSIsContainer) {
                    try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop; $removed++ }
                    catch { $errors += $_.Exception.Message }
                }
            }
        }
    }

    [pscustomobject]@{
        FilesRemoved = $removed
        Errors       = $errors
        Note         = 'Restart Explorer (Boost tab) to refresh Quick Access pane.'
    }
}

function Disable-Telemetry {
    [CmdletBinding()]
    param()

    $svc = Get-Service DiagTrack -ErrorAction SilentlyContinue
    if (-not $svc) { return [pscustomobject]@{ Success = $false; Note = 'DiagTrack service not found.' } }

    try {
        if ($svc.Status -eq 'Running') { Stop-Service DiagTrack -Force -ErrorAction Stop }
        Set-Service DiagTrack -StartupType Disabled -ErrorAction Stop
        # Also Connected User Experiences -- the silent half
        $cuat = Get-Service dmwappushservice -ErrorAction SilentlyContinue
        if ($cuat) {
            try {
                if ($cuat.Status -eq 'Running') { Stop-Service dmwappushservice -Force -ErrorAction SilentlyContinue }
                Set-Service dmwappushservice -StartupType Disabled -ErrorAction SilentlyContinue
            } catch {}
        }
        [pscustomobject]@{ Success = $true; Note = 'DiagTrack stopped + disabled. Reversible: Set-Service DiagTrack -StartupType Automatic; Start-Service DiagTrack' }
    } catch {
        [pscustomobject]@{ Success = $false; Note = $_.Exception.Message }
    }
}

function Enable-ClassicRightClick {
    [CmdletBinding()]
    param()

    $key   = 'HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32'
    $parent = Split-Path $key -Parent
    try {
        if (-not (Test-Path $parent)) { New-Item -Path $parent -Force | Out-Null }
        if (-not (Test-Path $key))    { New-Item -Path $key    -Force | Out-Null }
        # Default value blank: disables the new Win11 menu, falls back to classic.
        Set-ItemProperty -Path $key -Name '(default)' -Value '' -ErrorAction Stop
        [pscustomobject]@{ Success = $true; Note = 'Classic right-click menu enabled. Restart Explorer to apply.' }
    } catch {
        [pscustomobject]@{ Success = $false; Note = $_.Exception.Message }
    }
}

function Disable-ClassicRightClick {
    [CmdletBinding()]
    param()
    $key = 'HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}'
    try {
        if (Test-Path $key) { Remove-Item -Path $key -Recurse -Force -ErrorAction Stop }
        [pscustomobject]@{ Success = $true; Note = 'Win11 right-click menu restored. Restart Explorer to apply.' }
    } catch {
        [pscustomobject]@{ Success = $false; Note = $_.Exception.Message }
    }
}

function Start-SearchIndexRebuild {
    [CmdletBinding()]
    param()

    try {
        # Reset-WindowsSearchIndex requires admin and triggers a full rebuild.
        if (Get-Command Reset-WindowsSearchIndex -ErrorAction SilentlyContinue) {
            Reset-WindowsSearchIndex -ErrorAction Stop
            return [pscustomobject]@{ Success = $true; Note = 'Search index rebuild triggered. Indexing will run in background; can take 30+ min on large profiles.' }
        }

        # Fallback: poke the registry value SetupCompletedSuccessfully = 0, restart WSearch.
        Stop-Service WSearch -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows Search' -Name 'SetupCompletedSuccessfully' -Value 0 -ErrorAction Stop
        Start-Service WSearch -ErrorAction Stop
        [pscustomobject]@{ Success = $true; Note = 'Search index marked for rebuild via registry. Service restarted.' }
    } catch {
        [pscustomobject]@{ Success = $false; Note = $_.Exception.Message }
    }
}

Export-ModuleMember -Function Invoke-Diagnostics, Reset-QuickAccess, Disable-Telemetry, Enable-ClassicRightClick, Disable-ClassicRightClick, Start-SearchIndexRebuild
