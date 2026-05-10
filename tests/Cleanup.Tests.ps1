BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..\modules\Cleanup.psm1'
    Import-Module $ModulePath -Force
}

Describe 'Cleanup module' {

    Context 'Format-Bytes' {
        It 'formats bytes under 1 KB as B' {
            Format-Bytes -Bytes 512 | Should -Be '512 B'
            Format-Bytes -Bytes 0   | Should -Be '0 B'
        }
        It 'formats KB / MB / GB with two decimals' {
            Format-Bytes -Bytes 2048              | Should -Be '2.00 KB'
            Format-Bytes -Bytes (5 * 1MB)         | Should -Be '5.00 MB'
            Format-Bytes -Bytes ([long](2.5 * 1GB)) | Should -Be '2.50 GB'
        }
    }

    Context 'Get-CleanupTargets' {
        It 'returns the canonical 10 targets' {
            $t = Get-CleanupTargets
            $t.Count | Should -Be 10
            $t | Should -Contain 'UserTemp'
            $t | Should -Contain 'WindowsUpdate'
            $t | Should -Contain 'DNSCache'
        }
    }

    Context 'Invoke-Cleanup -OnProgress' {
        It 'fires Start + TargetDone events for each target' {
            $events = New-Object System.Collections.ArrayList
            $cb = { param($info) [void]$events.Add($info) }.GetNewClosure()

            $null = Invoke-Cleanup -Targets @('DNSCache') -OnProgress $cb

            $events.Count    | Should -Be 2
            $events[0].Phase | Should -Be 'Start'
            $events[1].Phase | Should -Be 'TargetDone'
            $events[0].Target | Should -Be 'DNSCache'
        }
    }

    Context 'Reparse-point safety' {
        BeforeAll {
            $script:SrcRoot = Join-Path $env:TEMP "wintune-cleanup-test-src-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $script:TgtRoot = Join-Path $env:TEMP "wintune-cleanup-test-tgt-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            New-Item -ItemType Directory -Path $script:SrcRoot, $script:TgtRoot | Out-Null

            'junk1' | Set-Content (Join-Path $script:SrcRoot 'a.txt')
            'junk2' | Set-Content (Join-Path $script:SrcRoot 'b.txt')
            'PRECIOUS' | Set-Content (Join-Path $script:TgtRoot 'survivor.txt')

            # Create the junction. cmd /c mklink is the simplest cross-PS-version path.
            $junctionPath = Join-Path $script:SrcRoot 'junction-into-target'
            cmd /c mklink /J "$junctionPath" "$script:TgtRoot" 2>&1 | Out-Null
        }
        AfterAll {
            Remove-Item $script:SrcRoot, $script:TgtRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
        It 'deletes regular files but never follows the junction' {
            # Reach into the module to call the internal helper.
            $remove = Get-Command -Module Cleanup -Name Remove-PathContents -ErrorAction SilentlyContinue
            if (-not $remove) {
                # Helper is not exported; invoke via a script block bound to the module's session state.
                $r = & (Get-Module Cleanup) { param($p) Remove-PathContents -Path $p } $script:SrcRoot
            } else {
                $r = Remove-PathContents -Path $script:SrcRoot
            }

            $r.FilesRemoved | Should -Be 2     # only a.txt and b.txt
            $r.Errors.Count | Should -Be 0
            (Test-Path (Join-Path $script:TgtRoot 'survivor.txt')) | Should -BeTrue
            (Get-Content (Join-Path $script:TgtRoot 'survivor.txt')) | Should -Be 'PRECIOUS'
        }
    }
}
