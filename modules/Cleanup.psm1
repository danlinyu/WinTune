# Cleanup.psm1 -- safe, well-known cache deletions
# Each target returns: Target, FilesRemoved, BytesFreed, Errors, Skipped

$script:LastLogPath = $null
function Get-LastCleanupLog { $script:LastLogPath }

$script:ValidTargets = @(
    'UserTemp','SystemTemp','Prefetch','WER','WindowsUpdate',
    'EdgeCache','ChromeCache','FirefoxCache','RecycleBin','DNSCache'
)

function Get-CleanupTargets { $script:ValidTargets }

function Test-IsReparsePoint {
    param([System.IO.FileSystemInfo]$Item)
    return [bool]($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)
}

function Remove-DirectoryTreeSafe {
    # Recursive delete that NEVER follows reparse points (junctions, symlinks).
    # Defends against a malicious junction inside a cleanup target redirecting
    # the recursive delete to e.g. C:\Windows\System32. A parent that holds a
    # skipped reparse-point child will fail with "directory not empty" -- that
    # is the intended signal that something unexpected lives there.
    param([string]$Path)

    Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue |
        ForEach-Object {
            if (Test-IsReparsePoint $_) { return }   # skip link, do not recurse
            if ($_.PSIsContainer) {
                Remove-DirectoryTreeSafe -Path $_.FullName
            } else {
                [System.IO.File]::Delete($_.FullName)
            }
        }
    [System.IO.Directory]::Delete($Path, $false)
}

function Remove-PathContents {
    param([string]$Path, [switch]$Recurse)

    $filesRemoved = 0
    $bytesFreed   = 0L
    $errors       = @()

    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{ FilesRemoved = 0; BytesFreed = 0L; Errors = @() }
    }

    Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue |
        ForEach-Object {
            try {
                # Skip reparse-point entries at top level: do not delete the
                # link, do not follow it. Same defense as Remove-DirectoryTreeSafe.
                if (Test-IsReparsePoint $_) { return }

                if ($_.PSIsContainer) {
                    $sizeBefore = (Get-ChildItem -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue |
                        Where-Object {
                            -not $_.PSIsContainer -and
                            -not (Test-IsReparsePoint $_)
                        } |
                        Measure-Object -Property Length -Sum).Sum
                    Remove-DirectoryTreeSafe -Path $_.FullName
                    $bytesFreed   += [long]($sizeBefore | ForEach-Object { if ($_) { $_ } else { 0 } })
                    $filesRemoved += 1
                } else {
                    $bytesFreed   += [long]$_.Length
                    [System.IO.File]::Delete($_.FullName)
                    $filesRemoved += 1
                }
            } catch {
                $errors += "$($_.Exception.Message): $($_.TargetObject)"
            }
        }

    [pscustomobject]@{ FilesRemoved = $filesRemoved; BytesFreed = $bytesFreed; Errors = $errors }
}

function Test-BrowserRunning {
    param([string[]]$Names)
    $running = Get-Process -Name $Names -ErrorAction SilentlyContinue
    return [bool]$running
}

function Invoke-SingleTarget {
    param([string]$Target)

    $result = [pscustomobject]@{
        Target       = $Target
        FilesRemoved = 0
        BytesFreed   = 0L
        Errors       = @()
        Skipped      = $false
        SkipReason   = $null
    }

    try {
        switch ($Target) {
            'UserTemp' {
                $r = Remove-PathContents -Path $env:TEMP
                $result.FilesRemoved = $r.FilesRemoved
                $result.BytesFreed   = $r.BytesFreed
                $result.Errors       = $r.Errors
            }
            'SystemTemp' {
                $r = Remove-PathContents -Path (Join-Path $env:SystemRoot 'Temp')
                $result.FilesRemoved = $r.FilesRemoved
                $result.BytesFreed   = $r.BytesFreed
                $result.Errors       = $r.Errors
            }
            'Prefetch' {
                $r = Remove-PathContents -Path (Join-Path $env:SystemRoot 'Prefetch')
                $result.FilesRemoved = $r.FilesRemoved
                $result.BytesFreed   = $r.BytesFreed
                $result.Errors       = $r.Errors
            }
            'WER' {
                foreach ($p in @(
                    "$env:ProgramData\Microsoft\Windows\WER\ReportArchive",
                    "$env:ProgramData\Microsoft\Windows\WER\ReportQueue",
                    "$env:ProgramData\Microsoft\Windows\WER\Temp"
                )) {
                    $r = Remove-PathContents -Path $p
                    $result.FilesRemoved += $r.FilesRemoved
                    $result.BytesFreed   += $r.BytesFreed
                    $result.Errors       += $r.Errors
                }
            }
            'WindowsUpdate' {
                $stoppedSvcs = @()
                foreach ($svc in 'wuauserv','bits') {
                    try {
                        $s = Get-Service -Name $svc -ErrorAction Stop
                        if ($s.Status -eq 'Running') {
                            Stop-Service -Name $svc -Force -ErrorAction Stop
                            $stoppedSvcs += $svc
                        }
                    } catch { $result.Errors += "stop ${svc}: $($_.Exception.Message)" }
                }
                # Restart in finally so services come back even if delete throws.
                try {
                    $r = Remove-PathContents -Path (Join-Path $env:SystemRoot 'SoftwareDistribution\Download')
                    $result.FilesRemoved = $r.FilesRemoved
                    $result.BytesFreed   = $r.BytesFreed
                    $result.Errors       += $r.Errors
                } finally {
                    foreach ($svc in $stoppedSvcs) {
                        try { Start-Service -Name $svc -ErrorAction Stop }
                        catch { $result.Errors += "start ${svc}: $($_.Exception.Message)" }
                    }
                }
            }
            'EdgeCache' {
                if (Test-BrowserRunning -Names 'msedge') {
                    $result.Skipped = $true
                    $result.SkipReason = 'Edge is running -- close it and re-run.'
                } else {
                    foreach ($sub in 'Default','Profile 1','Profile 2','Profile 3') {
                        foreach ($cache in 'Cache','Code Cache','GPUCache') {
                            $p = Join-Path $env:LOCALAPPDATA "Microsoft\Edge\User Data\$sub\$cache"
                            $r = Remove-PathContents -Path $p
                            $result.FilesRemoved += $r.FilesRemoved
                            $result.BytesFreed   += $r.BytesFreed
                            $result.Errors       += $r.Errors
                        }
                    }
                }
            }
            'ChromeCache' {
                if (Test-BrowserRunning -Names 'chrome') {
                    $result.Skipped = $true
                    $result.SkipReason = 'Chrome is running -- close it and re-run.'
                } else {
                    foreach ($sub in 'Default','Profile 1','Profile 2','Profile 3') {
                        foreach ($cache in 'Cache','Code Cache','GPUCache') {
                            $p = Join-Path $env:LOCALAPPDATA "Google\Chrome\User Data\$sub\$cache"
                            $r = Remove-PathContents -Path $p
                            $result.FilesRemoved += $r.FilesRemoved
                            $result.BytesFreed   += $r.BytesFreed
                            $result.Errors       += $r.Errors
                        }
                    }
                }
            }
            'FirefoxCache' {
                if (Test-BrowserRunning -Names 'firefox') {
                    $result.Skipped = $true
                    $result.SkipReason = 'Firefox is running -- close it and re-run.'
                } else {
                    $profilesRoot = Join-Path $env:LOCALAPPDATA 'Mozilla\Firefox\Profiles'
                    if (Test-Path $profilesRoot) {
                        Get-ChildItem -LiteralPath $profilesRoot -Directory -ErrorAction SilentlyContinue |
                            ForEach-Object {
                                $r = Remove-PathContents -Path (Join-Path $_.FullName 'cache2')
                                $result.FilesRemoved += $r.FilesRemoved
                                $result.BytesFreed   += $r.BytesFreed
                                $result.Errors       += $r.Errors
                            }
                    }
                }
            }
            'RecycleBin' {
                try {
                    Clear-RecycleBin -DriveLetter $env:SystemDrive.TrimEnd(':') -Force -ErrorAction Stop
                    $result.FilesRemoved = 1
                } catch {
                    if ($_.Exception.Message -notmatch 'empty|no items') {
                        $result.Errors += $_.Exception.Message
                    }
                }
            }
            'DNSCache' {
                try {
                    Clear-DnsClientCache -ErrorAction Stop
                    $result.FilesRemoved = 1
                } catch {
                    $result.Errors += $_.Exception.Message
                }
            }
            default {
                $result.Skipped = $true
                $result.SkipReason = "Unknown target: $Target"
            }
        }
    } catch {
        $result.Errors += "Fatal: $($_.Exception.Message)"
    }

    return $result
}

function Invoke-Cleanup {
    [CmdletBinding()]
    param(
        [string[]]$Targets = $script:ValidTargets,
        [scriptblock]$OnProgress
    )

    $results = New-Object System.Collections.ArrayList
    $i = 0
    foreach ($t in $Targets) {
        $i++
        if ($OnProgress) {
            & $OnProgress @{ Phase = 'Start'; Index = $i; Total = $Targets.Count; Target = $t }
        }
        $r = Invoke-SingleTarget -Target $t
        [void]$results.Add($r)
        if ($OnProgress) {
            & $OnProgress @{
                Phase        = 'TargetDone'
                Index        = $i
                Total        = $Targets.Count
                Target       = $t
                FilesRemoved = $r.FilesRemoved
                BytesFreed   = $r.BytesFreed
                Skipped      = $r.Skipped
                ErrorCount   = $r.Errors.Count
            }
        }
    }
    $results = $results.ToArray()

    # Always write a per-run log so users can inspect the actual error messages.
    $logDir = Join-Path $env:LOCALAPPDATA 'WinTune\logs'
    [void](New-Item -ItemType Directory -Path $logDir -Force -ErrorAction SilentlyContinue)
    $stamp   = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $logPath = Join-Path $logDir "cleanup-$stamp.log"

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("WinTune cleanup run -- $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    [void]$sb.AppendLine("Targets requested: $($Targets -join ', ')")
    [void]$sb.AppendLine(("=" * 78))
    foreach ($r in $results) {
        $status = if ($r.Skipped) { 'SKIPPED' } elseif ($r.Errors.Count) { 'PARTIAL' } else { 'OK' }
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("[$status] $($r.Target)")
        [void]$sb.AppendLine("  files removed : $($r.FilesRemoved)")
        [void]$sb.AppendLine("  bytes freed   : $($r.BytesFreed) ($(Format-Bytes -Bytes $r.BytesFreed))")
        if ($r.Skipped) {
            [void]$sb.AppendLine("  skip reason   : $($r.SkipReason)")
        }
        if ($r.Errors.Count) {
            [void]$sb.AppendLine("  errors ($($r.Errors.Count)):")
            foreach ($e in $r.Errors) { [void]$sb.AppendLine("    - $e") }
        }
    }
    Set-Content -LiteralPath $logPath -Value $sb.ToString() -Encoding UTF8
    $script:LastLogPath = $logPath

    return $results
}

function Format-Bytes {
    param([long]$Bytes)
    if ($Bytes -ge 1GB) { return "{0:N2} GB" -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return "{0:N2} MB" -f ($Bytes / 1MB) }
    if ($Bytes -ge 1KB) { return "{0:N2} KB" -f ($Bytes / 1KB) }
    return "$Bytes B"
}

Export-ModuleMember -Function Invoke-Cleanup, Get-CleanupTargets, Format-Bytes, Get-LastCleanupLog
