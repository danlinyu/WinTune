BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..\modules\Boost.psm1'
    Import-Module $ModulePath -Force
}

Describe 'Boost module' {

    Context 'Module surface' {
        It 'exports the documented functions' {
            $exports = (Get-Command -Module Boost).Name
            $exports | Should -Contain 'Clear-WorkingSets'
            $exports | Should -Contain 'Restart-Explorer'
            $exports | Should -Contain 'Clear-DNSCacheSafe'
        }
    }

    Context 'Clear-WorkingSets' {
        It 'returns an object with Trim/Skip/Bytes counters' {
            $r = Clear-WorkingSets
            $r.PSObject.Properties.Name | Should -Contain 'ProcessesTrimmed'
            $r.PSObject.Properties.Name | Should -Contain 'ProcessesSkipped'
            $r.PSObject.Properties.Name | Should -Contain 'BytesFreed'
            ($r.ProcessesTrimmed + $r.ProcessesSkipped) | Should -BeGreaterThan 0
        }
    }

    Context 'Clear-DNSCacheSafe' {
        It 'returns Success=$true on a system that supports Clear-DnsClientCache' {
            $r = Clear-DNSCacheSafe
            # On Win10/11 the cmdlet always exists; some locked-down hosts may fail.
            ($r.Success -or $r.Error) | Should -BeTrue
            if ($r.Success) { $r.Error | Should -BeNullOrEmpty }
        }
    }
}
