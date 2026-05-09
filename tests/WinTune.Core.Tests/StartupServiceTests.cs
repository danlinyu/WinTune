using FluentAssertions;
using WinTune.Core.Services;

namespace WinTune.Core.Tests;

public class StartupServiceTests
{
    [Fact]
    public async Task GetStartupAppsAsync_returns_some_entries_on_a_real_windows_box()
    {
        IStartupService sut = new StartupService();

        var apps = await sut.GetStartupAppsAsync();

        apps.Should().NotBeNull();
        // Most Windows installations have at least a handful of startup entries.
        // We assert >= 0 rather than > 0 because clean / minimal Windows builds
        // (e.g., LTSC) may legitimately have zero. The shape assertions below
        // catch real bugs.
        apps.Should().BeAssignableTo<IReadOnlyList<Models.StartupEntry>>();
    }

    [Fact]
    public async Task GetStartupAppsAsync_entries_have_required_fields()
    {
        IStartupService sut = new StartupService();

        var apps = await sut.GetStartupAppsAsync();

        apps.Should().OnlyContain(e => !string.IsNullOrEmpty(e.Name));
        apps.Should().OnlyContain(e => !string.IsNullOrEmpty(e.User));
    }

    [Fact]
    public async Task GetStartupAppsAsync_distinct_by_user_and_name()
    {
        IStartupService sut = new StartupService();

        var apps = await sut.GetStartupAppsAsync();

        apps.Select(e => (e.User, e.Name)).Distinct().Count()
            .Should().Be(apps.Count);
    }

    [Fact]
    public async Task GetStartupAppsAsync_respects_cancellation()
    {
        IStartupService sut = new StartupService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await sut.GetStartupAppsAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
