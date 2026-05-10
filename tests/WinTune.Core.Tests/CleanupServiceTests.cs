using FluentAssertions;
using WinTune.Core.Models;
using WinTune.Core.Services;
using WinTune.Core.Tests.TestHelpers;

namespace WinTune.Core.Tests;

public class CleanupServiceTests
{
    [Fact]
    public void GetAvailableTargets_returns_all_ten_enum_values()
    {
        var sut = new CleanupService();

        var targets = sut.GetAvailableTargets();

        targets.Should().HaveCount(10);
        targets.Should().Contain(CleanupTarget.UserTemp);
        targets.Should().Contain(CleanupTarget.WindowsUpdate);
        targets.Should().Contain(CleanupTarget.RecycleBin);
        targets.Should().Contain(CleanupTarget.DnsCache);
    }

    [Fact]
    public void FormatBytes_uses_two_decimal_places_for_KB_MB_GB_and_zero_for_B()
    {
        var sut = new CleanupService();

        sut.FormatBytes(0).Should().Be("0 B");
        sut.FormatBytes(1023).Should().Be("1023 B");
        sut.FormatBytes(1024).Should().Be("1.00 KB");
        sut.FormatBytes(1536).Should().Be("1.50 KB");
        sut.FormatBytes(1024L * 1024).Should().Be("1.00 MB");
        sut.FormatBytes(1024L * 1024 * 1024).Should().Be("1.00 GB");
    }

    [Fact]
    public void RemoveDirectoryContentsSafe_does_NOT_follow_reparse_points()
    {
        // Arrange — sentinel directory holds a file that must SURVIVE the cleanup.
        // Target directory holds a file (deletable) and a junction pointing at
        // the sentinel. If the recursive deleter follows the junction, the sentinel
        // file gets eaten. Defense: skip reparse points, never recurse into them.
        using var sentinelDir = new TempDirectory();
        var sentinelFile = sentinelDir.CreateFile("DO-NOT-DELETE.txt", "important");
        using var targetDir = new TempDirectory();
        targetDir.CreateFile("trash.txt", "ok to delete");
        try
        {
            targetDir.CreateJunction("link-to-sentinel", sentinelDir.Path);
        }
        catch (UnauthorizedAccessException)
        {
            // CreateSymbolicLink requires Developer Mode or admin on some Windows configs.
            // If we can't create the link, skip — the test isn't applicable on this box.
            return;
        }
        catch (IOException)
        {
            return;
        }

        // Act — recursively clean the target dir via the test seam.
        var (filesRemoved, _, _) = CleanupService.RemoveDirectoryContentsSafeForTest(targetDir.Path);

        // Assert — sentinel survived, target's regular file was deleted.
        File.Exists(sentinelFile).Should().BeTrue("reparse-point junction must NOT be followed");
        filesRemoved.Should().BeGreaterThan(0, "the regular file inside target must have been deleted");
    }

    [Fact]
    public async Task InvokeCleanupAsync_emits_Start_and_TargetDone_progress_per_target()
    {
        var sut = new CleanupService();
        var events = new List<CleanupProgress>();
        var progress = new Progress<CleanupProgress>(p => events.Add(p));

        // DnsCache is the safest real target to exercise (no filesystem mutation,
        // works without admin, no service restarts).
        await sut.InvokeCleanupAsync(new[] { CleanupTarget.DnsCache }, progress);

        // Wait briefly for Progress<T> to deliver back to the test context.
        await Task.Delay(50);

        events.Should().Contain(p => p.Phase == "Start" && p.Target == CleanupTarget.DnsCache);
        events.Should().Contain(p => p.Phase == "TargetDone" && p.Target == CleanupTarget.DnsCache);
    }

    [Fact]
    public async Task InvokeCleanupAsync_writes_a_run_log_to_LOCALAPPDATA_WinTune_logs()
    {
        var sut = new CleanupService();

        var results = await sut.InvokeCleanupAsync(new[] { CleanupTarget.DnsCache });

        results.Should().HaveCount(1);
        var logPath = sut.GetLastCleanupLogPath();
        logPath.Should().NotBeNullOrEmpty();
        File.Exists(logPath!).Should().BeTrue();
        File.ReadAllText(logPath!).Should().Contain("WinTune cleanup run");
    }

    [Fact]
    public async Task InvokeCleanupAsync_respects_cancellation()
    {
        var sut = new CleanupService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await sut.InvokeCleanupAsync(new[] { CleanupTarget.DnsCache }, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
