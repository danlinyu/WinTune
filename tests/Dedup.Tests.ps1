BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..\modules\Dedup.psm1'
    Import-Module $ModulePath -Force

    function New-TestFile {
        param([string]$Path, [byte]$Fill, [int]$SizeMB)
        $bytes = [byte[]]::new($SizeMB * 1MB)
        for ($i = 0; $i -lt $bytes.Length; $i++) { $bytes[$i] = $Fill }
        [IO.File]::WriteAllBytes($Path, $bytes)
    }
}

Describe 'Dedup module' {

    BeforeAll {
        $script:Root = Join-Path $env:TEMP "wintune-dedup-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $script:Root | Out-Null
    }
    AfterAll {
        Remove-Item $script:Root -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context 'Find-Duplicates' {
        It 'returns empty when nothing duplicates' {
            $sub = Join-Path $script:Root 'unique'
            New-Item -ItemType Directory -Path $sub | Out-Null
            New-TestFile -Path (Join-Path $sub 'a.bin') -Fill 0xAA -SizeMB 2
            New-TestFile -Path (Join-Path $sub 'b.bin') -Fill 0xBB -SizeMB 2

            $r = Find-Duplicates -Paths @($sub) -MinSizeBytes 1MB
            @($r).Count | Should -Be 0
        }
        It 'groups byte-identical files' {
            $sub = Join-Path $script:Root 'identical'
            New-Item -ItemType Directory -Path $sub | Out-Null
            1..3 | ForEach-Object { New-TestFile -Path (Join-Path $sub "dup$_.bin") -Fill 0xCC -SizeMB 2 }

            $r = @(Find-Duplicates -Paths @($sub) -MinSizeBytes 1MB)
            $r.Count | Should -Be 1
            $r[0].FileCount   | Should -Be 3
            $r[0].SizeBytes   | Should -Be (2 * 1MB)
            $r[0].WastedBytes | Should -Be (2 * (2 * 1MB))
        }
        It 'skips files smaller than MinSizeBytes' {
            $sub = Join-Path $script:Root 'small-skipped'
            New-Item -ItemType Directory -Path $sub | Out-Null
            'tiny' | Set-Content (Join-Path $sub 'a.txt')
            'tiny' | Set-Content (Join-Path $sub 'b.txt')

            $r = @(Find-Duplicates -Paths @($sub) -MinSizeBytes 1MB)
            $r.Count | Should -Be 0
        }
        It 'excludes the default extension list' {
            $sub = Join-Path $script:Root 'excluded-ext'
            New-Item -ItemType Directory -Path $sub | Out-Null
            New-TestFile -Path (Join-Path $sub 'a.tmp') -Fill 0xDD -SizeMB 2
            New-TestFile -Path (Join-Path $sub 'b.tmp') -Fill 0xDD -SizeMB 2

            # Default ExcludeExtensions includes .tmp
            $r = @(Find-Duplicates -Paths @($sub) -MinSizeBytes 1MB)
            $r.Count | Should -Be 0
        }
        It 'fires progress callback at HashStart and Done at minimum' {
            $sub = Join-Path $script:Root 'progress'
            New-Item -ItemType Directory -Path $sub | Out-Null
            1..2 | ForEach-Object { New-TestFile -Path (Join-Path $sub "p$_.bin") -Fill 0xEE -SizeMB 2 }

            $events = New-Object System.Collections.ArrayList
            $cb = { param($info) [void]$events.Add($info) }.GetNewClosure()
            $null = Find-Duplicates -Paths @($sub) -MinSizeBytes 1MB -OnProgress $cb

            ($events | Where-Object Phase -eq 'HashStart').Count | Should -BeGreaterThan 0
            ($events | Where-Object Phase -eq 'Done').Count      | Should -BeGreaterThan 0
        }
    }

    Context 'Get-DefaultScanRoots' {
        It 'returns paths that all exist' {
            $roots = Get-DefaultScanRoots
            $roots.Count | Should -BeGreaterThan 0
            foreach ($r in $roots) { (Test-Path -LiteralPath $r) | Should -BeTrue }
        }
    }

    Context 'Reparse-point safety in scan' {
        It 'does not follow a junction during enumeration' {
            $src = Join-Path $script:Root "src-with-junction-$([guid]::NewGuid().ToString('N').Substring(0,6))"
            $tgt = Join-Path $script:Root "junction-target-$([guid]::NewGuid().ToString('N').Substring(0,6))"
            New-Item -ItemType Directory -Path $src, $tgt | Out-Null
            New-TestFile -Path (Join-Path $src 'real.bin') -Fill 0xFF -SizeMB 2
            New-TestFile -Path (Join-Path $tgt 'inside.bin') -Fill 0xFF -SizeMB 2
            cmd /c mklink /J "$src\junction" "$tgt" 2>&1 | Out-Null

            $r = @(Find-Duplicates -Paths @($src) -MinSizeBytes 1MB)
            # Without reparse-point skip, both files would be discovered and grouped.
            # With the skip, only $src\real.bin is seen so no duplicate group exists.
            $r.Count | Should -Be 0
        }
    }
}
