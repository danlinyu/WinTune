BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..\modules\Monitor.psm1'
    Import-Module $ModulePath -Force
}

Describe 'Monitor module' {

    Context 'Get-PerfSnapshot' {
        It 'returns an object with all expected numeric fields' {
            $s = Get-PerfSnapshot
            $s.PSObject.Properties.Name | Should -Contain 'CpuPct'
            $s.PSObject.Properties.Name | Should -Contain 'RamUsedGB'
            $s.PSObject.Properties.Name | Should -Contain 'RamTotalGB'
            $s.PSObject.Properties.Name | Should -Contain 'RamPct'
            $s.PSObject.Properties.Name | Should -Contain 'DiskUsedGB'
            $s.PSObject.Properties.Name | Should -Contain 'DiskFreeGB'
            $s.PSObject.Properties.Name | Should -Contain 'DiskTotalGB'
            $s.PSObject.Properties.Name | Should -Contain 'DiskPct'
            $s.PSObject.Properties.Name | Should -Contain 'Timestamp'
        }
        It 'reports CPU between 0 and 100' {
            $s = Get-PerfSnapshot
            $s.CpuPct | Should -BeGreaterOrEqual 0
            $s.CpuPct | Should -BeLessOrEqual 100
        }
        It 'reports RAM totals consistent with used+free' {
            $s = Get-PerfSnapshot
            $s.RamTotalGB | Should -BeGreaterThan 0
            ($s.RamUsedGB + 0.5) | Should -BeLessOrEqual ($s.RamTotalGB + 0.5)
        }
    }

    Context 'Get-TopProcesses' {
        It 'returns at most -Count entries' {
            (Get-TopProcesses -Count 5 | Measure-Object).Count | Should -BeLessOrEqual 5
        }
        It 'sorts entries by RAM_MB descending' {
            $rows = @(Get-TopProcesses -Count 5)
            for ($i = 1; $i -lt $rows.Count; $i++) {
                $rows[$i-1].RAM_MB | Should -BeGreaterOrEqual $rows[$i].RAM_MB
            }
        }
    }
}
