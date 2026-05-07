# Startup.psm1 -- read-only enumeration of Windows startup entries

function Get-StartupApps {
    [CmdletBinding()]
    param()

    $items = New-Object System.Collections.Generic.List[object]

    try {
        Get-CimInstance Win32_StartupCommand -ErrorAction SilentlyContinue |
            ForEach-Object {
                $items.Add([pscustomobject]@{
                    Name     = $_.Name
                    Command  = $_.Command
                    Location = $_.Location
                    User     = $_.User
                    Source   = 'WMI'
                })
            }
    } catch {}

    $regKeys = @(
        @{ Hive = 'HKLM'; Path = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run';         User = 'AllUsers' },
        @{ Hive = 'HKLM'; Path = 'SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce';     User = 'AllUsers' },
        @{ Hive = 'HKCU'; Path = 'Software\Microsoft\Windows\CurrentVersion\Run';         User = $env:USERNAME },
        @{ Hive = 'HKCU'; Path = 'Software\Microsoft\Windows\CurrentVersion\RunOnce';     User = $env:USERNAME }
    )

    foreach ($k in $regKeys) {
        $regPath = "$($k.Hive):\$($k.Path)"
        if (Test-Path $regPath) {
            try {
                $props = Get-ItemProperty -Path $regPath -ErrorAction Stop
                $props.PSObject.Properties |
                    Where-Object { $_.Name -notmatch '^PS' } |
                    ForEach-Object {
                        $items.Add([pscustomobject]@{
                            Name     = $_.Name
                            Command  = $_.Value
                            Location = $regPath
                            User     = $k.User
                            Source   = 'Registry'
                        })
                    }
            } catch {}
        }
    }

    foreach ($folder in @(
        [Environment]::GetFolderPath('Startup'),
        [Environment]::GetFolderPath('CommonStartup')
    )) {
        if (Test-Path $folder) {
            Get-ChildItem -LiteralPath $folder -ErrorAction SilentlyContinue |
                ForEach-Object {
                    $items.Add([pscustomobject]@{
                        Name     = $_.BaseName
                        Command  = $_.FullName
                        Location = $folder
                        User     = if ($folder -match 'Common') { 'AllUsers' } else { $env:USERNAME }
                        Source   = 'StartupFolder'
                    })
                }
        }
    }

    $items | Sort-Object User, Name -Unique
}

function Open-StartupTaskManager {
    Start-Process -FilePath 'taskmgr.exe' -ArgumentList '/0','/startup'
}

Export-ModuleMember -Function Get-StartupApps, Open-StartupTaskManager
