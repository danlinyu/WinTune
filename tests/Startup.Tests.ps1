BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..\modules\Startup.psm1'
    Import-Module $ModulePath -Force
}

Describe 'Startup module' {

    Context 'Get-StartupApps' {
        It 'returns objects with the documented shape' {
            $items = @(Get-StartupApps)
            # On a default Win10/11 install there are always >= a handful.
            $items.Count | Should -BeGreaterThan 0
            $first = $items[0]
            $first.PSObject.Properties.Name | Should -Contain 'Name'
            $first.PSObject.Properties.Name | Should -Contain 'Command'
            $first.PSObject.Properties.Name | Should -Contain 'Location'
            $first.PSObject.Properties.Name | Should -Contain 'User'
            $first.PSObject.Properties.Name | Should -Contain 'Source'
        }
        It 'tags each item with a known Source' {
            $sources = @(Get-StartupApps) | Select-Object -ExpandProperty Source -Unique
            foreach ($s in $sources) { $s | Should -BeIn @('WMI','Registry','StartupFolder') }
        }
    }
}
