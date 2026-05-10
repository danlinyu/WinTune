using System.Diagnostics;
using System.IO;
using System.Management;
using Microsoft.Win32;
using WinTune.Core.Models;

namespace WinTune.Core.Services;

public sealed class StartupService : IStartupService
{
    public Task<IReadOnlyList<StartupEntry>> GetStartupAppsAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<StartupEntry>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var items = new List<StartupEntry>();
            items.AddRange(EnumerateWmi(ct));
            items.AddRange(EnumerateRegistry(ct));
            items.AddRange(EnumerateStartupFolders(ct));

            return items
                .GroupBy(e => (e.User, e.Name))
                .Select(g => g.First())
                .OrderBy(e => e.User, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct);

    public void OpenStartupTaskManager()
    {
        Process.Start(new ProcessStartInfo("taskmgr.exe", "/0 /startup") { UseShellExecute = true });
    }

    private static List<StartupEntry> EnumerateWmi(CancellationToken ct)
    {
        var entries = new List<StartupEntry>();
        try
        {
            using var search = new ManagementObjectSearcher("SELECT Name, Command, Location, User FROM Win32_StartupCommand");
            foreach (ManagementObject mo in search.Get())
            {
                ct.ThrowIfCancellationRequested();
                using (mo)
                {
                    entries.Add(new StartupEntry(
                        Name: mo["Name"] as string ?? "",
                        Command: mo["Command"] as string ?? "",
                        Location: mo["Location"] as string ?? "",
                        User: mo["User"] as string ?? "AllUsers",
                        Source: StartupSource.Wmi));
                }
            }
        }
        catch
        {
            // WMI provider may be unavailable in restricted environments; ignore.
        }
        return entries;
    }

    private static List<StartupEntry> EnumerateRegistry(CancellationToken ct)
    {
        var entries = new List<StartupEntry>();
        var keys = new (RegistryHive Hive, string Path, string User)[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "AllUsers"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "AllUsers"),
            (RegistryHive.CurrentUser,  @"Software\Microsoft\Windows\CurrentVersion\Run", Environment.UserName),
            (RegistryHive.CurrentUser,  @"Software\Microsoft\Windows\CurrentVersion\RunOnce", Environment.UserName),
        };

        foreach (var (hive, path, user) in keys)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var subKey = baseKey.OpenSubKey(path);
                if (subKey is null) continue;

                foreach (var name in subKey.GetValueNames())
                {
                    var value = subKey.GetValue(name)?.ToString() ?? "";
                    var hivePrefix = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
                    entries.Add(new StartupEntry(
                        Name: name,
                        Command: value,
                        Location: $"{hivePrefix}:\\{path}",
                        User: user,
                        Source: StartupSource.Registry));
                }
            }
            catch
            {
                // Inaccessible key; skip.
            }
        }
        return entries;
    }

    private static List<StartupEntry> EnumerateStartupFolders(CancellationToken ct)
    {
        var entries = new List<StartupEntry>();
        var folders = new (string Path, string User)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.UserName),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "AllUsers"),
        };

        foreach (var (folder, user) in folders)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    entries.Add(new StartupEntry(
                        Name: Path.GetFileNameWithoutExtension(file),
                        Command: file,
                        Location: folder,
                        User: user,
                        Source: StartupSource.StartupFolder));
                }
            }
            catch
            {
                // Folder access may fail; skip.
            }
        }
        return entries;
    }
}
