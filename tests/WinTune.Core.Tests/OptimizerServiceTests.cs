using FluentAssertions;
using WinTune.Core.Models;
using WinTune.Core.Services;
using WinTune.Core.Tests.TestHelpers;

namespace WinTune.Core.Tests;

public class OptimizerServiceTests
{
    [Fact]
    public void BuildMachineProfile_adapts_memory_threshold_to_ram_tier()
    {
        var sut = CreateSut();

        var constrained = sut.BuildMachineProfile(Snapshot(ramTotalGB: 7.5, diskTotalGB: 250));
        var workstation = sut.BuildMachineProfile(Snapshot(ramTotalGB: 64, diskTotalGB: 1000));

        constrained.Tier.Should().Be(MachineCapabilityTier.Constrained);
        workstation.Tier.Should().Be(MachineCapabilityTier.Workstation);
        constrained.MemoryPressureThresholdPct.Should().BeLessThan(workstation.MemoryPressureThresholdPct);
    }

    [Fact]
    public void Assess_healthy_machine_has_no_automatic_action()
    {
        var sut = CreateSut();

        var assessment = sut.Assess(
            Snapshot(cpuPct: 20, ramPct: 40, ramUsedGB: 6, ramTotalGB: 16, diskFreeGB: 200, diskTotalGB: 500),
            Array.Empty<ProcessSnapshot>(),
            consecutiveMemoryPressureSamples: 0,
            lastAutomaticActionUtc: null,
            nowUtc: DateTime.UtcNow);

        assessment.Severity.Should().Be(OptimizationSeverity.Green);
        assessment.SuggestedAutomaticAction.Should().Be(OptimizationActionKind.None);
        assessment.Recommendations.Should().ContainSingle(r => r.Severity == OptimizationSeverity.Green);
    }

    [Fact]
    public void Assess_sustained_memory_pressure_suggests_working_set_trim()
    {
        var sut = CreateSut();

        var assessment = sut.Assess(
            Snapshot(cpuPct: 35, ramPct: 91, ramUsedGB: 14.6, ramTotalGB: 16, diskFreeGB: 200, diskTotalGB: 500),
            new[] { new ProcessSnapshot("editor", 123, 2048, 30, "10:00:00") },
            consecutiveMemoryPressureSamples: 2,
            lastAutomaticActionUtc: null,
            nowUtc: DateTime.UtcNow);

        assessment.Severity.Should().Be(OptimizationSeverity.Red);
        assessment.IsMemoryPressureSustained.Should().BeTrue();
        assessment.SuggestedAutomaticAction.Should().Be(OptimizationActionKind.ClearWorkingSets);
    }

    [Fact]
    public void Assess_respects_automatic_action_cooldown()
    {
        var sut = CreateSut();
        var now = DateTime.UtcNow;

        var assessment = sut.Assess(
            Snapshot(cpuPct: 35, ramPct: 91, ramUsedGB: 14.6, ramTotalGB: 16, diskFreeGB: 200, diskTotalGB: 500),
            Array.Empty<ProcessSnapshot>(),
            consecutiveMemoryPressureSamples: 3,
            lastAutomaticActionUtc: now.AddMinutes(-1),
            nowUtc: now);

        assessment.SuggestedAutomaticAction.Should().Be(OptimizationActionKind.None);
        assessment.AutomaticActionCooldownRemaining.Should().NotBeNull();
    }

    [Fact]
    public void Assess_low_disk_space_recommends_manual_cleanup_without_automatic_action()
    {
        var sut = CreateSut();

        var assessment = sut.Assess(
            Snapshot(cpuPct: 20, ramPct: 45, ramUsedGB: 7, ramTotalGB: 16, diskFreeGB: 8, diskTotalGB: 500),
            Array.Empty<ProcessSnapshot>(),
            consecutiveMemoryPressureSamples: 0,
            lastAutomaticActionUtc: null,
            nowUtc: DateTime.UtcNow);

        assessment.Severity.Should().Be(OptimizationSeverity.Red);
        assessment.SuggestedAutomaticAction.Should().Be(OptimizationActionKind.None);
        assessment.Recommendations.Should().Contain(r =>
            r.Area == "System drive" &&
            r.Action.Contains("automatic deletion is intentionally not enabled", StringComparison.Ordinal));
    }

    [Fact]
    public void Assess_low_secondary_drive_recommends_manual_cleanup_for_that_drive()
    {
        var sut = CreateSut();
        var drives = new[]
        {
            new DriveProfile("C:\\", "C:", DriveMediaKind.Ssd, 120, 500, 76, true),
            new DriveProfile("D:\\", "D: Data", DriveMediaKind.Hdd, 6, 1000, 99.4, false),
        };

        var assessment = sut.Assess(
            Snapshot(cpuPct: 20, ramPct: 45, ramUsedGB: 7, ramTotalGB: 16, diskFreeGB: 120, diskTotalGB: 500),
            Array.Empty<ProcessSnapshot>(),
            consecutiveMemoryPressureSamples: 0,
            lastAutomaticActionUtc: null,
            nowUtc: DateTime.UtcNow,
            fixedDrives: drives,
            gpus: Array.Empty<GpuProfile>());

        assessment.Severity.Should().Be(OptimizationSeverity.Red);
        assessment.Recommendations.Should().Contain(r => r.Area == "Drive D:\\");
    }

    [Fact]
    public void Assess_busy_ssd_recommends_resource_monitor_without_automatic_action()
    {
        var sut = CreateSut();
        var drives = new[]
        {
            new DriveProfile(
                "C:\\",
                "C:",
                DriveMediaKind.Ssd,
                FreeGB: 180,
                TotalGB: 1000,
                UsedPct: 82,
                IsSystemDrive: true,
                ActiveTimePct: 97,
                QueueLength: 3.2,
                ThroughputMBps: 450),
        };

        var assessment = sut.Assess(
            Snapshot(cpuPct: 20, ramPct: 45, ramUsedGB: 7, ramTotalGB: 16, diskFreeGB: 180, diskTotalGB: 1000),
            Array.Empty<ProcessSnapshot>(),
            consecutiveMemoryPressureSamples: 0,
            lastAutomaticActionUtc: null,
            nowUtc: DateTime.UtcNow,
            fixedDrives: drives,
            gpus: Array.Empty<GpuProfile>());

        assessment.Severity.Should().Be(OptimizationSeverity.Red);
        assessment.SuggestedAutomaticAction.Should().Be(OptimizationActionKind.None);
        assessment.Recommendations.Should().Contain(r =>
            r.Area == "System drive I/O" &&
            r.Action.Contains("Resource Monitor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyActionAsync_clear_working_sets_delegates_to_boost_service()
    {
        var boost = new FakeBoostService();
        var sut = CreateSut(boost);

        var result = await sut.ApplyActionAsync(OptimizationActionKind.ClearWorkingSets);

        boost.ClearWorkingSetCalls.Should().Be(1);
        result.Success.Should().BeTrue();
        result.Note.Should().Contain("Trimmed 12 processes");
    }

    [Fact]
    public async Task OptimizeDriveAsync_uses_windows_media_aware_defrag_switch()
    {
        var proc = new StubProcessRunner();
        proc.When("D: /O /U /V", new ProcessResult(0, "The operation completed successfully.", ""));
        var sut = CreateSut(proc: proc);

        var result = await sut.OptimizeDriveAsync("D:\\");

        result.Success.Should().BeTrue();
        proc.Calls.Should().ContainSingle(call =>
            call.FileName == "defrag.exe" &&
            call.Args.SequenceEqual(new[] { "D:", "/O", "/U", "/V" }));
    }

    private static PerfSnapshot Snapshot(
        int cpuPct = 20,
        double ramPct = 40,
        double ramUsedGB = 4,
        double ramTotalGB = 8,
        double diskFreeGB = 100,
        double diskTotalGB = 250)
    {
        double diskUsedGB = diskTotalGB - diskFreeGB;
        double diskPct = diskTotalGB == 0 ? 0 : Math.Round(diskUsedGB * 100 / diskTotalGB, 1);
        return new PerfSnapshot(
            cpuPct,
            ramUsedGB,
            ramTotalGB,
            ramPct,
            diskUsedGB,
            diskFreeGB,
            diskTotalGB,
            diskPct,
            DateTime.UtcNow);
    }

    private static OptimizerService CreateSut(
        FakeBoostService? boost = null,
        StubProcessRunner? proc = null) =>
        new(boost ?? new FakeBoostService(), proc ?? new StubProcessRunner());

    private sealed class FakeBoostService : IBoostService
    {
        public int ClearWorkingSetCalls { get; private set; }

        public Task<WorkingSetResult> ClearWorkingSetsAsync(CancellationToken ct = default)
        {
            ClearWorkingSetCalls++;
            return Task.FromResult(new WorkingSetResult(12, 2, 128L * 1024 * 1024));
        }

        public Task<ExplorerRestartResult> RestartExplorerAsync(CancellationToken ct = default) =>
            Task.FromResult(new ExplorerRestartResult(0));

        public Task<DnsFlushResult> FlushDnsCacheAsync(CancellationToken ct = default) =>
            Task.FromResult(new DnsFlushResult(true, null));
    }
}
