# Dedup.psm1 -- duplicate-file finder and Recycle-Bin remover.
#
# Two-pass strategy:
#   1. Enumerate files, group by exact byte size.
#   2. Hash only groups with >=2 candidates (SHA1, fast; collisions unrealistic
#      for personal files).
# Skip reparse points (OneDrive / Google Drive cloud-only files would otherwise
# trigger downloads).

if (-not ('Microsoft.VisualBasic.FileIO.FileSystem' -as [type])) {
    Add-Type -AssemblyName Microsoft.VisualBasic
}

function _IsSkippablePath {
    param([System.IO.FileInfo]$File)

    # Skip reparse points (junctions, symlinks, OneDrive cloud-only).
    if ($File.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { return $true }
    # Skip files marked Offline (cloud-only, would trigger download).
    if ($File.Attributes -band [System.IO.FileAttributes]::Offline)      { return $true }
    # Skip system files.
    if ($File.Attributes -band [System.IO.FileAttributes]::System)       { return $true }
    return $false
}

function Find-Duplicates {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Paths,
        [long]$MinSizeBytes = 1MB,
        [switch]$IncludeHidden,
        [string[]]$ExcludeExtensions = @('.lnk','.url','.tmp','.crdownload','.partial'),
        [scriptblock]$OnProgress
    )

    # ---------- Pass 1: enumerate and group by size ----------
    $bySize = @{}
    $totalScanned = 0
    foreach ($root in $Paths) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        try {
            Get-ChildItem -LiteralPath $root -Recurse -File -Force -ErrorAction SilentlyContinue |
                Where-Object {
                    -not (_IsSkippablePath $_) -and
                    $_.Length -ge $MinSizeBytes -and
                    ($IncludeHidden -or -not ($_.Attributes -band [System.IO.FileAttributes]::Hidden)) -and
                    ($ExcludeExtensions -notcontains $_.Extension.ToLower())
                } |
                ForEach-Object {
                    $totalScanned++
                    if (-not $bySize.ContainsKey($_.Length)) { $bySize[$_.Length] = New-Object System.Collections.Generic.List[System.IO.FileInfo] }
                    $bySize[$_.Length].Add($_)
                    if ($OnProgress -and ($totalScanned % 500 -eq 0)) {
                        & $OnProgress @{ Phase = 'Scan'; FilesSeen = $totalScanned; CurrentRoot = $root }
                    }
                }
        } catch {
            Write-Verbose "Skip root $root : $($_.Exception.Message)"
        }
    }

    # Discard size-buckets with only 1 file -- cannot be duplicates.
    $candidates = @($bySize.GetEnumerator() | Where-Object { $_.Value.Count -ge 2 })
    $candidateFiles = ($candidates | ForEach-Object { $_.Value.Count } | Measure-Object -Sum).Sum
    if (-not $candidateFiles) { $candidateFiles = 0 }

    if ($OnProgress) {
        & $OnProgress @{ Phase = 'HashStart'; FilesSeen = $totalScanned; Candidates = $candidateFiles }
    }

    # ---------- Pass 2: hash candidates ----------
    $byHash = @{}
    $hashed = 0
    foreach ($entry in $candidates) {
        foreach ($file in $entry.Value) {
            try {
                $h = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA1 -ErrorAction Stop).Hash
                $key = "$($file.Length)::$h"
                if (-not $byHash.ContainsKey($key)) { $byHash[$key] = New-Object System.Collections.Generic.List[object] }
                $byHash[$key].Add([pscustomobject]@{
                    FullName      = $file.FullName
                    Length        = $file.Length
                    LastWriteTime = $file.LastWriteTime
                    Hash          = $h
                })
                $hashed++
                if ($OnProgress -and ($hashed % 25 -eq 0)) {
                    & $OnProgress @{ Phase = 'Hash'; Hashed = $hashed; Total = $candidateFiles }
                }
            } catch {
                Write-Verbose "Hash failed: $($file.FullName): $($_.Exception.Message)"
            }
        }
    }

    # ---------- Build duplicate groups ----------
    $groupId = 0
    $groups = foreach ($pair in $byHash.GetEnumerator()) {
        if ($pair.Value.Count -lt 2) { continue }
        $groupId++
        $size = $pair.Value[0].Length
        [pscustomobject]@{
            GroupId     = $groupId
            Hash        = $pair.Value[0].Hash
            SizeBytes   = $size
            FileCount   = $pair.Value.Count
            WastedBytes = $size * ($pair.Value.Count - 1)
            Files       = @($pair.Value | Sort-Object FullName)
        }
    }

    if ($OnProgress) {
        & $OnProgress @{ Phase = 'Done'; Groups = ($groups | Measure-Object).Count; Hashed = $hashed }
    }

    # Sort biggest waste first.
    return @($groups | Sort-Object -Property WastedBytes -Descending)
}

function Remove-DuplicateFiles {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Paths,
        [switch]$Permanent
    )

    $deleted   = 0
    $bytesGone = 0L
    $errors    = @()

    foreach ($p in $Paths) {
        try {
            if (-not (Test-Path -LiteralPath $p)) {
                $errors += "Not found: $p"
                continue
            }
            $size = (Get-Item -LiteralPath $p -Force).Length
            if ($Permanent) {
                Remove-Item -LiteralPath $p -Force -ErrorAction Stop
            } else {
                [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile(
                    $p,
                    [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,
                    [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin
                )
            }
            $deleted   += 1
            $bytesGone += $size
        } catch {
            $errors += "$p : $($_.Exception.Message)"
        }
    }

    [pscustomobject]@{
        Deleted    = $deleted
        BytesFreed = $bytesGone
        Errors     = $errors
        Permanent  = [bool]$Permanent
    }
}

function Get-DefaultScanRoots {
    @(
        [Environment]::GetFolderPath('Desktop'),
        [Environment]::GetFolderPath('MyDocuments'),
        [Environment]::GetFolderPath('MyPictures'),
        [Environment]::GetFolderPath('MyVideos'),
        [Environment]::GetFolderPath('MyMusic'),
        (Join-Path $env:USERPROFILE 'Downloads')
    ) | Where-Object { Test-Path -LiteralPath $_ }
}

Export-ModuleMember -Function Find-Duplicates, Remove-DuplicateFiles, Get-DefaultScanRoots
