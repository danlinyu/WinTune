# Monitor.psm1 — live performance snapshot + top processes

function Get-PerfSnapshot {
    [CmdletBinding()]
    param()

    try {
        $cpu = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
    } catch { $cpu = 0 }

    $os = Get-CimInstance Win32_OperatingSystem
    $totalRamGB = [math]::Round($os.TotalVisibleMemorySize / 1MB, 2)
    $freeRamGB  = [math]::Round($os.FreePhysicalMemory  / 1MB, 2)
    $usedRamGB  = [math]::Round($totalRamGB - $freeRamGB, 2)
    $ramPct     = if ($totalRamGB -gt 0) { [math]::Round(($usedRamGB / $totalRamGB) * 100, 1) } else { 0 }

    $sysDriveLetter = $env:SystemDrive.TrimEnd(':')
    $drive = Get-PSDrive -Name $sysDriveLetter -ErrorAction SilentlyContinue
    if ($drive) {
        $diskFreeGB  = [math]::Round($drive.Free / 1GB, 2)
        $diskUsedGB  = [math]::Round($drive.Used / 1GB, 2)
        $diskTotalGB = [math]::Round(($drive.Free + $drive.Used) / 1GB, 2)
        $diskPct     = if ($diskTotalGB -gt 0) { [math]::Round(($diskUsedGB / $diskTotalGB) * 100, 1) } else { 0 }
    } else {
        $diskFreeGB = $diskUsedGB = $diskTotalGB = $diskPct = 0
    }

    [pscustomobject]@{
        CpuPct      = [int]$cpu
        RamUsedGB   = $usedRamGB
        RamTotalGB  = $totalRamGB
        RamPct      = $ramPct
        DiskUsedGB  = $diskUsedGB
        DiskFreeGB  = $diskFreeGB
        DiskTotalGB = $diskTotalGB
        DiskPct     = $diskPct
        Timestamp   = Get-Date
    }
}

function Get-TopProcesses {
    [CmdletBinding()]
    param([int]$Count = 10)

    Get-Process |
        Where-Object { $_.WorkingSet64 -gt 0 } |
        Sort-Object -Property WorkingSet64 -Descending |
        Select-Object -First $Count `
            Name,
            Id,
            @{n='RAM_MB';     e={ [math]::Round($_.WorkingSet64 / 1MB, 1) }},
            @{n='Threads';    e={ $_.Threads.Count }},
            @{n='StartTime';  e={ try { $_.StartTime.ToString('HH:mm:ss') } catch { '-' } }}
}

Export-ModuleMember -Function Get-PerfSnapshot, Get-TopProcesses
