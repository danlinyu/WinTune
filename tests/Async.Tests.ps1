BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..\modules\Async.psm1'
    Import-Module $ModulePath -Force
}

Describe 'Async helper' {

    Context 'Start-AsyncOp + Receive-AsyncOp basic flow' {
        It 'returns Success=true and the script return value for a normal script' {
            $op = Start-AsyncOp -Script {
                param($x, $Progress)
                return $x * 2
            } -Arguments @{ x = 21 }

            # Wait for completion (test scripts complete in <1s).
            $sw = [Diagnostics.Stopwatch]::StartNew()
            while (-not (Test-AsyncOpComplete $op) -and $sw.Elapsed.TotalSeconds -lt 5) {
                Start-Sleep -Milliseconds 25
            }
            $sw.Stop()

            Test-AsyncOpComplete $op | Should -BeTrue
            $r = Receive-AsyncOp $op
            $r.Success | Should -BeTrue
            $r.Result  | Should -Be 42
            $r.Error   | Should -BeNullOrEmpty
        }

        It 'returns Success=false and propagates the error message when the script throws' {
            $op = Start-AsyncOp -Script {
                param($Progress)
                throw "intentional failure"
            } -Arguments @{}

            $sw = [Diagnostics.Stopwatch]::StartNew()
            while (-not (Test-AsyncOpComplete $op) -and $sw.Elapsed.TotalSeconds -lt 5) {
                Start-Sleep -Milliseconds 25
            }
            $sw.Stop()

            $r = Receive-AsyncOp $op
            $r.Success | Should -BeFalse
            $r.Error   | Should -Match 'intentional failure'
        }
    }

    Context 'Progress queue' {
        It 'delivers progress events posted from inside the runspace' {
            $op = Start-AsyncOp -Script {
                param($n, $Progress)
                for ($i = 1; $i -le $n; $i++) {
                    & $Progress @{ step = $i; of = $n }
                    Start-Sleep -Milliseconds 30
                }
                return $n
            } -Arguments @{ n = 4 }

            $events = New-Object System.Collections.ArrayList
            $sw = [Diagnostics.Stopwatch]::StartNew()
            while (-not (Test-AsyncOpComplete $op) -and $sw.Elapsed.TotalSeconds -lt 5) {
                foreach ($e in (Receive-AsyncProgress $op)) { [void]$events.Add($e) }
                Start-Sleep -Milliseconds 20
            }
            foreach ($e in (Receive-AsyncProgress $op)) { [void]$events.Add($e) }
            $r = Receive-AsyncOp $op

            $r.Success | Should -BeTrue
            $events.Count | Should -Be 4
            ($events | ForEach-Object { $_.step }) | Should -Be @(1,2,3,4)
        }

        It 'returns an empty array when no progress has been posted' {
            $op = Start-AsyncOp -Script { param($Progress) Start-Sleep -Milliseconds 200 } -Arguments @{}
            $items = Receive-AsyncProgress $op
            $items -is [array] | Should -BeTrue
            $items.Count | Should -Be 0
            # Drain the running op
            while (-not (Test-AsyncOpComplete $op)) { Start-Sleep -Milliseconds 50 }
            $null = Receive-AsyncOp $op
        }
    }

    Context 'Stop-AsyncOp' {
        It 'cancels a running op without raising' {
            $op = Start-AsyncOp -Script {
                param($Progress)
                for ($i = 0; $i -lt 100; $i++) {
                    & $Progress $i
                    Start-Sleep -Milliseconds 100
                }
            } -Arguments @{}

            Start-Sleep -Milliseconds 200   # let it produce a few items
            { Stop-AsyncOp $op } | Should -Not -Throw
        }

        It 'is a no-op when given $null' {
            { Stop-AsyncOp $null } | Should -Not -Throw
        }
    }

    Context 'Defensive null handling' {
        It 'Test-AsyncOpComplete returns true for $null' {
            Test-AsyncOpComplete $null | Should -BeTrue
        }
        It 'Receive-AsyncProgress returns @() for $null' {
            $r = Receive-AsyncProgress $null
            $r -is [array] | Should -BeTrue
            @($r).Count | Should -Be 0
        }
        It 'Receive-AsyncOp returns Success=false for $null' {
            (Receive-AsyncOp $null).Success | Should -BeFalse
        }
    }
}
