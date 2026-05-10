using System.Diagnostics;
using WinTune.Core.Models;
using WinTune.Core.NativeInterop;

namespace WinTune.Core.Services;

public sealed class BoostService : IBoostService
{
    public Task<WorkingSetResult> ClearWorkingSetsAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            int trimmed = 0;
            int skipped = 0;
            long beforeTotal = 0;
            long afterTotal = 0;

            foreach (var p in Process.GetProcesses())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    beforeTotal += p.WorkingSet64;
                    if (PsApi.EmptyWorkingSet(p.Handle))
                    {
                        trimmed++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
                catch
                {
                    // Protected processes deny handle access; count as skipped.
                    skipped++;
                }
                finally
                {
                    p.Dispose();
                }
            }

            Thread.Sleep(600);

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    afterTotal += p.WorkingSet64;
                }
                catch
                {
                    // Ignored: working-set reads can fail mid-enumeration.
                }
                finally
                {
                    p.Dispose();
                }
            }

            long freed = Math.Max(0, beforeTotal - afterTotal);
            return new WorkingSetResult(trimmed, skipped, freed);
        }, ct);

    public Task<ExplorerRestartResult> RestartExplorerAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            int killed = 0;
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    p.Kill();
                    killed++;
                }
                catch
                {
                    // Some explorer instances may already be exiting; skip.
                }
                finally
                {
                    p.Dispose();
                }
            }

            Thread.Sleep(1000);

            if (Process.GetProcessesByName("explorer").Length == 0)
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            }

            return new ExplorerRestartResult(killed);
        }, ct);

    public Task<DnsFlushResult> FlushDnsCacheAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                bool ok = DnsApi.DnsFlushResolverCache();
                return ok
                    ? new DnsFlushResult(true, null)
                    : new DnsFlushResult(false, "DnsFlushResolverCache returned false");
            }
            catch (Exception ex)
            {
                return new DnsFlushResult(false, ex.Message);
            }
        }, ct);
}
