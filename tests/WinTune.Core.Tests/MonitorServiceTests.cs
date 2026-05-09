using FluentAssertions;
using WinTune.Core.Services;

namespace WinTune.Core.Tests;

public class MonitorServiceTests
{
    [Fact]
    public async Task GetPerfSnapshotAsync_returns_plausible_values()
    {
        IMonitorService sut = new MonitorService();

        var snap = await sut.GetPerfSnapshotAsync();

        snap.CpuPct.Should().BeInRange(0, 100);
        snap.RamPct.Should().BeInRange(0, 100);
        snap.DiskPct.Should().BeInRange(0, 100);
        snap.RamTotalGB.Should().BeGreaterThan(0);
        snap.DiskTotalGB.Should().BeGreaterThan(0);
        snap.Timestamp.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GetTopProcessesAsync_returns_at_most_count_entries_sorted_desc_by_ram()
    {
        IMonitorService sut = new MonitorService();

        var top = await sut.GetTopProcessesAsync(5);

        top.Should().NotBeEmpty();
        top.Count.Should().BeLessThanOrEqualTo(5);
        top.Should().BeInDescendingOrder(p => p.RamMB);
        top.Should().OnlyContain(p => p.Id > 0);
        top.Should().OnlyContain(p => !string.IsNullOrEmpty(p.Name));
    }

    [Fact]
    public async Task GetTopProcessesAsync_respects_cancellation()
    {
        IMonitorService sut = new MonitorService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await sut.GetTopProcessesAsync(10, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetPerfSnapshotAsync_respects_cancellation()
    {
        IMonitorService sut = new MonitorService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await sut.GetPerfSnapshotAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
