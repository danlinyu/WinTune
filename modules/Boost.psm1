# Boost.psm1 — free working-set memory, restart Explorer, flush DNS

if (-not ('WinTune.Native' -as [type])) {
    Add-Type -Namespace WinTune -Name Native -MemberDefinition @"
        [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
        public static extern bool EmptyWorkingSet(System.IntPtr hProcess);
"@
}

function Clear-WorkingSets {
    [CmdletBinding()]
    param()

    $trimmed = 0
    $skipped = 0
    $totalBefore = 0L
    $totalAfter  = 0L

    Get-Process | ForEach-Object {
        $totalBefore += $_.WorkingSet64
        try {
            $h = $_.Handle
            $ok = [WinTune.Native]::EmptyWorkingSet($h)
            if ($ok) { $trimmed++ } else { $skipped++ }
        } catch {
            $skipped++
        }
    }

    Start-Sleep -Milliseconds 600
    Get-Process | ForEach-Object { $totalAfter += $_.WorkingSet64 }

    $freedBytes = [math]::Max([long]0, [long]($totalBefore - $totalAfter))

    [pscustomobject]@{
        ProcessesTrimmed = $trimmed
        ProcessesSkipped = $skipped
        BytesFreed       = $freedBytes
    }
}

function Restart-Explorer {
    [CmdletBinding()]
    param()

    $count = 0
    Get-Process -Name explorer -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force -ErrorAction Stop; $count++ } catch {}
    }
    Start-Sleep -Seconds 1
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
        Start-Process explorer.exe
    }
    [pscustomobject]@{ Restarted = $count }
}

function Clear-DNSCacheSafe {
    [CmdletBinding()]
    param()
    try {
        Clear-DnsClientCache -ErrorAction Stop
        [pscustomobject]@{ Success = $true; Error = $null }
    } catch {
        [pscustomobject]@{ Success = $false; Error = $_.Exception.Message }
    }
}

Export-ModuleMember -Function Clear-WorkingSets, Restart-Explorer, Clear-DNSCacheSafe
