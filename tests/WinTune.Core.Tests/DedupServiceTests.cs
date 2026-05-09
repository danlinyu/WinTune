using FluentAssertions;
using WinTune.Core.Services;
using WinTune.Core.Tests.TestHelpers;

namespace WinTune.Core.Tests;

public class DedupServiceTests
{
    [Fact]
    public async Task FindDuplicatesAsync_groups_three_identical_files_and_excludes_unique()
    {
        using var dir = new TempDirectory();
        // Three identical files (same content, different names) + one unique.
        var contentA = new byte[2048]; // exceeds default 1MB? no — for the test we drop minSize.
        for (int i = 0; i < contentA.Length; i++) contentA[i] = 0xAB;
        dir.CreateFile("a/dup1.bin", contentA);
        dir.CreateFile("b/dup2.bin", contentA);
        dir.CreateFile("c/dup3.bin", contentA);

        var contentB = new byte[2048];
        for (int i = 0; i < contentB.Length; i++) contentB[i] = 0xCD;
        dir.CreateFile("d/unique.bin", contentB);

        IDedupService sut = new DedupService();

        var groups = await sut.FindDuplicatesAsync(
            new[] { dir.Path },
            minSizeBytes: 1024,
            includeHidden: false,
            excludeExtensions: null);

        groups.Should().HaveCount(1);
        var g = groups[0];
        g.Files.Should().HaveCount(3);
        g.SizeBytes.Should().Be(2048);
        g.WastedBytes.Should().Be(2 * 2048L);
        g.Hash.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task FindDuplicatesAsync_skips_files_below_minSize()
    {
        using var dir = new TempDirectory();
        dir.CreateFile("small/a.bin", new byte[100]);
        dir.CreateFile("small/b.bin", new byte[100]);

        IDedupService sut = new DedupService();

        var groups = await sut.FindDuplicatesAsync(
            new[] { dir.Path },
            minSizeBytes: 1024);

        groups.Should().BeEmpty();
    }

    [Fact]
    public async Task FindDuplicatesAsync_skips_excluded_extensions()
    {
        using var dir = new TempDirectory();
        var content = new byte[2048];
        dir.CreateFile("links/a.lnk", content);
        dir.CreateFile("links/b.lnk", content);

        IDedupService sut = new DedupService();

        var groups = await sut.FindDuplicatesAsync(
            new[] { dir.Path },
            minSizeBytes: 1024);

        groups.Should().BeEmpty(".lnk is in the default exclude list");
    }

    [Fact]
    public async Task FindDuplicatesAsync_respects_cancellation()
    {
        using var dir = new TempDirectory();
        IDedupService sut = new DedupService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await sut.FindDuplicatesAsync(new[] { dir.Path }, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RemoveDuplicateFilesAsync_permanent_deletes_files_and_reports_bytes()
    {
        using var dir = new TempDirectory();
        var path1 = dir.CreateFile("a.bin", new byte[1024]);
        var path2 = dir.CreateFile("b.bin", new byte[2048]);

        IDedupService sut = new DedupService();

        var result = await sut.RemoveDuplicateFilesAsync(new[] { path1, path2 }, permanent: true);

        result.Deleted.Should().Be(2);
        result.BytesFreed.Should().Be(1024 + 2048);
        result.Errors.Should().BeEmpty();
        result.Permanent.Should().BeTrue();
        File.Exists(path1).Should().BeFalse();
        File.Exists(path2).Should().BeFalse();
    }

    [Fact]
    public void GetDefaultScanRoots_returns_existing_user_profile_folders()
    {
        IDedupService sut = new DedupService();

        var roots = sut.GetDefaultScanRoots();

        roots.Should().NotBeEmpty();
        roots.Should().OnlyContain(p => Directory.Exists(p));
    }
}
