using FluentAssertions;
using WinTune.Core.Models;
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

    [Fact]
    public async Task FindDuplicatesAsync_does_not_descend_into_managed_directories_by_default()
    {
        using var dir = new TempDirectory();
        var content = new byte[2048];
        for (int i = 0; i < content.Length; i++) content[i] = 0x77;

        // Two duplicates inside a managed directory (node_modules) — must NOT be reported.
        dir.CreateFile("node_modules/lib-a/dup.bin", content);
        dir.CreateFile("node_modules/lib-b/dup.bin", content);

        // Two duplicates inside .git — must NOT be reported.
        dir.CreateFile(".git/objects/pack/dup1.bin", content);
        dir.CreateFile(".git/objects/pack/dup2.bin", content);

        // Two duplicates in a regular subdirectory — SHOULD be reported.
        dir.CreateFile("photos/vacation/img.bin", content);
        dir.CreateFile("photos/backup/img.bin", content);

        IDedupService sut = new DedupService();

        var groups = await sut.FindDuplicatesAsync(new[] { dir.Path }, minSizeBytes: 1024);

        groups.Should().HaveCount(1, "only the photos/ duplicates count; node_modules and .git are excluded by default");
        groups[0].Files.Should().HaveCount(2);
        groups[0].Files.Should().OnlyContain(f => f.FullPath.Contains("photos"));
    }

    [Fact]
    public async Task FindDuplicatesAsync_distinguishes_files_with_matching_head_but_different_tail()
    {
        // Two files with identical first 64 KB (the head-hash window) but different
        // bytes after 64 KB MUST NOT be reported as duplicates. This locks in the
        // correctness of the two-stage hash optimisation.
        using var dir = new TempDirectory();
        const int Head = 64 * 1024;
        var common = new byte[Head];
        for (int i = 0; i < common.Length; i++) common[i] = 0xA5;

        var fileA = new byte[Head + 4096];
        var fileB = new byte[Head + 4096];
        Array.Copy(common, fileA, Head);
        Array.Copy(common, fileB, Head);
        for (int i = Head; i < fileA.Length; i++) { fileA[i] = 0x11; fileB[i] = 0x22; }

        // And one true duplicate of fileA in another dir, to confirm hashes still detect actual matches.
        dir.CreateFile("a/file.bin", fileA);
        dir.CreateFile("b/different.bin", fileB);
        dir.CreateFile("c/file.bin", fileA);

        IDedupService sut = new DedupService();
        var groups = await sut.FindDuplicatesAsync(new[] { dir.Path }, minSizeBytes: 1024);

        groups.Should().HaveCount(1, "files differing only past the head-hash window must not group");
        groups[0].Files.Should().HaveCount(2);
        groups[0].Files.Select(f => Path.GetFileName(f.FullPath))
            .Should().BeEquivalentTo(new[] { "file.bin", "file.bin" });
    }

    [Fact]
    public async Task FindDuplicatesAsync_categorises_files_in_user_content_folders()
    {
        using var dir = new TempDirectory();
        var content = new byte[2048];
        for (int i = 0; i < content.Length; i++) content[i] = 0x99;
        dir.CreateFile("Documents/My Project/photo.bin", content);
        dir.CreateFile("Pictures/2025/photo.bin", content);

        IDedupService sut = new DedupService();
        var groups = await sut.FindDuplicatesAsync(new[] { dir.Path }, minSizeBytes: 1024);

        groups.Should().HaveCount(1);
        groups[0].Files.Should().OnlyContain(f => f.Category == FileCategory.UserContent);
        groups[0].DominantCategory.Should().Be(FileCategory.UserContent);
    }

    [Fact]
    public void Categorize_classifies_build_output_segments_as_BuildOutput()
    {
        DedupService.Categorize(@"C:\Users\u\repos\app\bin\Release\app.dll").Should().Be(FileCategory.BuildOutput);
        DedupService.Categorize(@"C:\Users\u\repos\app\obj\Debug\app.dll").Should().Be(FileCategory.BuildOutput);
        DedupService.Categorize(@"C:\Users\u\repos\rust\target\release\bin").Should().Be(FileCategory.BuildOutput);
        DedupService.Categorize(@"C:\Users\u\.gradle\caches\transforms\x.jar").Should().Be(FileCategory.BuildOutput);
        DedupService.Categorize(@"C:\Users\u\miniconda3\pkgs\libblas\Library\bin\libblas.dll").Should().Be(FileCategory.BuildOutput);
    }

    [Fact]
    public void Categorize_classifies_user_content_paths_as_UserContent()
    {
        DedupService.Categorize(@"C:\Users\u\Documents\report.pdf").Should().Be(FileCategory.UserContent);
        DedupService.Categorize(@"C:\Users\u\Pictures\trip\img.jpg").Should().Be(FileCategory.UserContent);
        DedupService.Categorize(@"C:\Users\u\Downloads\installer.exe").Should().Be(FileCategory.UserContent);
        DedupService.Categorize(@"C:\Users\u\OneDrive - Personal\Notes\note.md").Should().Be(FileCategory.UserContent);
    }

    [Fact]
    public void Categorize_classifies_appdata_paths_as_AppManaged_when_no_other_signal()
    {
        DedupService.Categorize(@"C:\Users\u\AppData\Roaming\SomeApp\settings.bin").Should().Be(FileCategory.AppManaged);
        DedupService.Categorize(@"C:\Users\u\AppData\Local\SomeApp\cache\file.bin").Should().Be(FileCategory.AppManaged);
    }

    [Fact]
    public void Categorize_classifies_backup_filename_patterns_as_Backup()
    {
        DedupService.Categorize(@"C:\Users\u\Documents\notes.bak").Should().Be(FileCategory.Backup);
        DedupService.Categorize(@"C:\Users\u\Documents\notes.docx.old").Should().Be(FileCategory.Backup);
        DedupService.Categorize(@"C:\Users\u\Downloads\report (1).pdf").Should().Be(FileCategory.Backup);
        DedupService.Categorize(@"C:\Users\u\Documents\Copy of plan.docx").Should().Be(FileCategory.Backup);
    }

    [Fact]
    public void Categorize_falls_back_to_Other_for_unrecognised_paths()
    {
        DedupService.Categorize(@"C:\custom\store\thing.bin").Should().Be(FileCategory.Other);
    }

    [Fact]
    public async Task FindDuplicatesAsync_excludes_gradle_and_miniconda_caches_by_default()
    {
        using var dir = new TempDirectory();
        var content = new byte[2048];
        for (int i = 0; i < content.Length; i++) content[i] = 0x66;

        dir.CreateFile(".gradle/caches/8.13/transforms/x/library.jar", content);
        dir.CreateFile(".gradle/caches/9.0/transforms/y/library.jar", content);
        dir.CreateFile("miniconda3/pkgs/numpy/Library/bin/lib.dll", content);
        dir.CreateFile("miniconda3/Library/bin/lib.dll", content);
        dir.CreateFile("bin/Debug/app.dll", content);
        dir.CreateFile("obj/Release/app.dll", content);

        // Only this pair lives outside the new exclusion list — it should be the only group reported.
        dir.CreateFile("photos/a/img.bin", content);
        dir.CreateFile("photos/b/img.bin", content);

        IDedupService sut = new DedupService();
        var groups = await sut.FindDuplicatesAsync(new[] { dir.Path }, minSizeBytes: 1024);

        groups.Should().HaveCount(1, "all .gradle, miniconda3, bin/, and obj/ duplicates are excluded by default");
        groups[0].Files.Should().OnlyContain(f => f.FullPath.Contains("photos"));
    }

    [Fact]
    public async Task FindDuplicatesAsync_respects_maxSizeBytes_skipping_oversized_files()
    {
        using var dir = new TempDirectory();
        var smallContent = new byte[2048];
        var largeContent = new byte[4096];
        for (int i = 0; i < smallContent.Length; i++) smallContent[i] = 0x11;
        for (int i = 0; i < largeContent.Length; i++) largeContent[i] = 0x22;

        // Two of each — duplicates within their own size class.
        dir.CreateFile("small/a.bin", smallContent);
        dir.CreateFile("small/b.bin", smallContent);
        dir.CreateFile("big/a.bin", largeContent);
        dir.CreateFile("big/b.bin", largeContent);

        IDedupService sut = new DedupService();

        var groups = await sut.FindDuplicatesAsync(
            new[] { dir.Path },
            minSizeBytes: 1024,
            maxSizeBytes: 3000); // excludes the 4096-byte pair

        groups.Should().HaveCount(1, "files larger than maxSizeBytes are skipped");
        groups[0].SizeBytes.Should().Be(2048);
    }

    [Fact]
    public async Task FindDuplicatesAsync_uses_hash_cache_to_skip_re_reading_unchanged_files()
    {
        using var dir = new TempDirectory();
        const int Size = 80 * 1024; // larger than head-hash window so pass 2b runs
        var content = new byte[Size];
        for (int i = 0; i < content.Length; i++) content[i] = 0x33;
        dir.CreateFile("a/x.bin", content);
        dir.CreateFile("b/x.bin", content);

        var cachePath = Path.Combine(dir.Path, "cache.json");
        var cache = new DedupHashCache(cachePath);

        IDedupService sut = new DedupService();

        // First scan — cold cache. Should populate.
        var first = await sut.FindDuplicatesAsync(
            new[] { dir.Path }, minSizeBytes: 1024, hashCache: cache);
        first.Should().HaveCount(1);
        cache.HitCount.Should().Be(0, "cold cache cannot have hits");
        cache.MissCount.Should().BeGreaterThan(0);
        cache.Count.Should().BeGreaterThan(0);

        // Second scan — same files unchanged. Cache hits should replace I/O.
        cache.ResetStats();
        var second = await sut.FindDuplicatesAsync(
            new[] { dir.Path }, minSizeBytes: 1024, hashCache: cache);
        second.Should().HaveCount(1);
        cache.HitCount.Should().BeGreaterThan(0, "warm cache should hit on unchanged files");
    }

    [Fact]
    public void DedupHashCache_invalidates_entry_when_size_or_mtime_changes()
    {
        using var dir = new TempDirectory();
        var path = dir.CreateFile("file.bin", new byte[100]);
        var cache = new DedupHashCache(Path.Combine(dir.Path, "cache.json"));
        var fi = new FileInfo(path);

        cache.Update(path, fi.Length, fi.LastWriteTime, headHash: "HEAD", fullHash: "FULL");

        cache.TryGetHead(path, fi.Length, fi.LastWriteTime, out var head).Should().BeTrue();
        head.Should().Be("HEAD");
        cache.TryGetFull(path, fi.Length, fi.LastWriteTime, out var full).Should().BeTrue();
        full.Should().Be("FULL");

        // Size mismatch
        cache.TryGetHead(path, fi.Length + 1, fi.LastWriteTime, out _).Should().BeFalse();
        // Mtime mismatch
        cache.TryGetHead(path, fi.Length, fi.LastWriteTime.AddSeconds(1), out _).Should().BeFalse();
    }

    [Fact]
    public void DedupHashCache_persists_and_reloads_entries_across_instances()
    {
        using var dir = new TempDirectory();
        var cachePath = Path.Combine(dir.Path, "cache.json");
        var first = new DedupHashCache(cachePath);

        first.Update(@"C:\foo\bar.bin", 1024, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), "HEAD123", "FULL123");
        first.Save();

        var second = new DedupHashCache(cachePath);
        second.Load();
        second.Count.Should().Be(1);
        second.TryGetHead(@"C:\foo\bar.bin", 1024, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), out var head)
            .Should().BeTrue();
        head.Should().Be("HEAD123");
        second.TryGetFull(@"C:\foo\bar.bin", 1024, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), out var full)
            .Should().BeTrue();
        full.Should().Be("FULL123");
    }

    [Fact]
    public void DedupHashCache_Update_merges_head_and_full_hashes_for_same_file()
    {
        using var dir = new TempDirectory();
        var cache = new DedupHashCache(Path.Combine(dir.Path, "cache.json"));
        var path = @"C:\foo\bar.bin";
        var size = 1024L;
        var mtime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        cache.Update(path, size, mtime, headHash: "HEAD", fullHash: null);
        cache.Update(path, size, mtime, headHash: null, fullHash: "FULL");

        cache.TryGetHead(path, size, mtime, out var head).Should().BeTrue();
        head.Should().Be("HEAD");
        cache.TryGetFull(path, size, mtime, out var full).Should().BeTrue();
        full.Should().Be("FULL");
    }

    [Fact]
    public async Task FindDuplicatesAsync_skips_locked_files_without_throwing()
    {
        using var dir = new TempDirectory();
        var content = new byte[2048];
        for (int i = 0; i < content.Length; i++) content[i] = 0x42;
        var lockedPath = dir.CreateFile("locked/a.bin", content);
        var openPath1 = dir.CreateFile("open/b.bin", content);
        var openPath2 = dir.CreateFile("open/c.bin", content);

        // Hold an exclusive lock on the first file for the duration of the scan.
        using var lockStream = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

        IDedupService sut = new DedupService();

        var act = async () => await sut.FindDuplicatesAsync(
            new[] { dir.Path },
            minSizeBytes: 1024);

        await act.Should().NotThrowAsync("locked files must be skipped silently, not abort the scan");

        var groups = await sut.FindDuplicatesAsync(new[] { dir.Path }, minSizeBytes: 1024);
        groups.Should().HaveCount(1, "the two unlocked files should still group");
        groups[0].Files.Should().HaveCount(2, "the locked file's hash failed and is excluded; only the two open copies remain");
        groups[0].Files.Select(f => Path.GetFileName(f.FullPath))
            .Should().BeEquivalentTo(new[] { "b.bin", "c.bin" });
    }
}
