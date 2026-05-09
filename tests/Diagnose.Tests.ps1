BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..\modules\Diagnose.psm1'
    Import-Module $ModulePath -Force
}

Describe 'Diagnose module' {

    Context 'Module surface' {
        It 'exports the documented functions' {
            $exports = (Get-Command -Module Diagnose).Name
            $exports | Should -Contain 'Invoke-Diagnostics'
            $exports | Should -Contain 'Reset-QuickAccess'
            $exports | Should -Contain 'Disable-Telemetry'
            $exports | Should -Contain 'Enable-ClassicRightClick'
            $exports | Should -Contain 'Disable-ClassicRightClick'
            $exports | Should -Contain 'Start-SearchIndexRebuild'
        }
    }

    Context 'Invoke-Diagnostics' {
        It 'returns an array of findings with the documented shape' {
            $f = @(Invoke-Diagnostics)
            $f.Count | Should -BeGreaterThan 0
            $first = $f[0]
            $first.PSObject.Properties.Name | Should -Contain 'Severity'
            $first.PSObject.Properties.Name | Should -Contain 'Title'
            $first.PSObject.Properties.Name | Should -Contain 'Detail'
            $first.PSObject.Properties.Name | Should -Contain 'Hint'
        }
        It 'tags every finding with a valid severity' {
            $f = @(Invoke-Diagnostics)
            foreach ($x in $f) {
                $x.Severity | Should -BeIn @('Red','Yellow','Green')
            }
        }
    }
}
