using FluentAssertions;
using WinTune.Core.Services;

namespace WinTune.Core.Tests;

public class BoostServiceTests
{
    [Fact]
    public async Task ClearWorkingSetsAsync_trims_at_least_some_processes_without_throwing()
    {
        IBoostService sut = new BoostService();

        var result = await sut.ClearWorkingSetsAsync();

        result.ProcessesTrimmed.Should().BeGreaterThan(0,
            "EmptyWorkingSet succeeds on the calling process at minimum");
        (result.ProcessesTrimmed + result.ProcessesSkipped).Should().BeGreaterThan(0);
        result.BytesFreedEstimate.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task FlushDnsCacheAsync_returns_success_true()
    {
        IBoostService sut = new BoostService();

        var result = await sut.FlushDnsCacheAsync();

        result.Success.Should().BeTrue("DnsFlushResolverCache succeeds for any user on a healthy DNS Client service");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task ClearWorkingSetsAsync_respects_cancellation()
    {
        IBoostService sut = new BoostService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await sut.ClearWorkingSetsAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact(Skip = "destructive: kills explorer.exe — run manually")]
    public async Task RestartExplorerAsync_smoke_local_only()
    {
        IBoostService sut = new BoostService();
        var result = await sut.RestartExplorerAsync();
        result.ProcessesRestarted.Should().BeGreaterThanOrEqualTo(0);
    }
}
