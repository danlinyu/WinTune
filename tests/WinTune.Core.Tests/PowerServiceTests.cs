using FluentAssertions;
using WinTune.Core.Models;
using WinTune.Core.Services;
using WinTune.Core.Tests.TestHelpers;

namespace WinTune.Core.Tests;

public class PowerServiceTests
{
    private const string ActiveSchemeOutput =
        "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)";

    private static string QueryOutput(string settingGuid, int dcPercent)
        => $@"
Subgroup GUID: 54533251-82be-4824-96c1-47b60b740d00  (Processor power management)
  Power Setting GUID: {settingGuid}  (Setting)
    Possible Setting Index: 000
    Possible Setting Friendly Name: 0
    ...
  Current AC Power Setting Index: 0x00000064
  Current DC Power Setting Index: 0x{dcPercent:X8}";

    private static StubProcessRunner BuildHealthyRunner()
    {
        var r = new StubProcessRunner();
        r.When("/getactivescheme",
            new ProcessResult(0, ActiveSchemeOutput, ""));
        r.When(PowerCfgIds.CpuMaxState,
            new ProcessResult(0, QueryOutput(PowerCfgIds.CpuMaxState, 100), ""));
        r.When(PowerCfgIds.CpuMinState,
            new ProcessResult(0, QueryOutput(PowerCfgIds.CpuMinState, 5), ""));
        r.When(PowerCfgIds.Epp,
            new ProcessResult(0, QueryOutput(PowerCfgIds.Epp, 0), ""));
        r.When(PowerCfgIds.CoolingPolicy,
            new ProcessResult(0, QueryOutput(PowerCfgIds.CoolingPolicy, 1), ""));
        r.When(PowerCfgIds.BatterySaverThresh,
            new ProcessResult(0, QueryOutput(PowerCfgIds.BatterySaverThresh, 20), ""));
        return r;
    }

    [Fact]
    public async Task CaptureCurrentAsync_parses_healthy_dc_state()
    {
        var runner = BuildHealthyRunner();
        var sut    = new PowerService(runner, snapshotPath: Path.GetTempFileName());

        var state = await sut.CaptureCurrentAsync(CancellationToken.None);

        state.ActiveScheme.Should().Be(Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"));
        state.DcCpuMaxPct.Should().Be(100);
        state.DcCpuMinPct.Should().Be(5);
        state.DcEpp.Should().Be(0);
        state.DcCoolingPolicy.Should().Be("Active");
        state.BatterySaverThresholdPct.Should().Be(20);
    }

    [Fact]
    public async Task CaptureCurrentAsync_calls_unhide_once_per_hidden_setting_before_query()
    {
        var runner = BuildHealthyRunner();
        var sut    = new PowerService(runner, snapshotPath: Path.GetTempFileName());

        await sut.CaptureCurrentAsync(CancellationToken.None);

        runner.Calls.Should().Contain(c =>
            c.Args.Contains("-ATTRIB_HIDE") && c.Args.Contains(PowerCfgIds.Epp));
        runner.Calls.Should().Contain(c =>
            c.Args.Contains("-ATTRIB_HIDE") && c.Args.Contains(PowerCfgIds.CoolingPolicy));
    }

    [Fact]
    public async Task CaptureCurrentAsync_maps_cooling_policy_zero_to_Passive()
    {
        var runner = BuildHealthyRunner();
        runner.When(PowerCfgIds.CoolingPolicy,
            new ProcessResult(0, QueryOutput(PowerCfgIds.CoolingPolicy, 0), ""));
        var sut = new PowerService(runner, snapshotPath: Path.GetTempFileName());

        var state = await sut.CaptureCurrentAsync(CancellationToken.None);

        state.DcCoolingPolicy.Should().Be("Passive");
    }

    [Fact]
    public async Task ApplyTierBAsync_writes_snapshot_then_sets_three_dc_values()
    {
        var runner       = BuildHealthyRunner();
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"wintune-{Guid.NewGuid()}.json");
        var sut          = new PowerService(runner, snapshotPath);

        var r = await sut.ApplyTierBAsync(CancellationToken.None);

        r.Success.Should().BeTrue();
        File.Exists(snapshotPath).Should().BeTrue();

        runner.Calls.Should().Contain(c =>
            c.Args.Contains("/setdcvalueindex") &&
            c.Args.Contains(PowerCfgIds.CpuMaxState) &&
            c.Args.Contains("100"));
        runner.Calls.Should().Contain(c =>
            c.Args.Contains("/setdcvalueindex") &&
            c.Args.Contains(PowerCfgIds.Epp) &&
            c.Args.Contains("0"));
        runner.Calls.Should().Contain(c =>
            c.Args.Contains("/setdcvalueindex") &&
            c.Args.Contains(PowerCfgIds.CoolingPolicy) &&
            c.Args.Contains("1"));
        runner.Calls.Should().Contain(c => c.Args.Contains("/setactive"));

        File.Delete(snapshotPath);
    }

    [Fact]
    public async Task ApplyTierBAsync_does_not_overwrite_existing_snapshot()
    {
        var runner       = BuildHealthyRunner();
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"wintune-{Guid.NewGuid()}.json");
        File.WriteAllText(snapshotPath, "{\"sentinel\":42}");
        var originalLen  = new FileInfo(snapshotPath).Length;
        var sut          = new PowerService(runner, snapshotPath);

        await sut.ApplyTierBAsync(CancellationToken.None);

        File.ReadAllText(snapshotPath).Should().Contain("\"sentinel\":42");
        new FileInfo(snapshotPath).Length.Should().Be(originalLen);

        File.Delete(snapshotPath);
    }

    [Fact]
    public async Task ApplyTierBAsync_returns_failure_on_powercfg_nonzero_exit()
    {
        var runner       = BuildHealthyRunner();
        runner.When("/setdcvalueindex",
            new ProcessResult(1, "", "Access denied."));
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"wintune-{Guid.NewGuid()}.json");
        var sut          = new PowerService(runner, snapshotPath);

        var r = await sut.ApplyTierBAsync(CancellationToken.None);

        r.Success.Should().BeFalse();
        r.Errors.Should().NotBeEmpty();

        if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
    }

    [Fact]
    public async Task RestorePriorAsync_reads_snapshot_writes_values_back_deletes_file()
    {
        var runner       = BuildHealthyRunner();
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"wintune-{Guid.NewGuid()}.json");
        var prior = new PowerState(
            ActiveScheme:              Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"),
            DcCpuMaxPct:               50,
            DcCpuMinPct:               5,
            DcEpp:                     80,
            DcCoolingPolicy:           "Passive",
            BatterySaverThresholdPct:  20);
        File.WriteAllText(snapshotPath, System.Text.Json.JsonSerializer.Serialize(prior));
        var sut = new PowerService(runner, snapshotPath);

        var r = await sut.RestorePriorAsync(CancellationToken.None);

        r.Success.Should().BeTrue();
        File.Exists(snapshotPath).Should().BeFalse();

        runner.Calls.Should().Contain(c =>
            c.Args.Contains("/setdcvalueindex") &&
            c.Args.Contains(PowerCfgIds.CpuMaxState) &&
            c.Args.Contains("50"));
        runner.Calls.Should().Contain(c =>
            c.Args.Contains("/setdcvalueindex") &&
            c.Args.Contains(PowerCfgIds.Epp) &&
            c.Args.Contains("80"));
        runner.Calls.Should().Contain(c =>
            c.Args.Contains("/setdcvalueindex") &&
            c.Args.Contains(PowerCfgIds.CoolingPolicy) &&
            c.Args.Contains("0"));
    }

    [Fact]
    public async Task RestorePriorAsync_without_snapshot_returns_failure()
    {
        var runner       = BuildHealthyRunner();
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"wintune-missing-{Guid.NewGuid()}.json");
        var sut          = new PowerService(runner, snapshotPath);

        var r = await sut.RestorePriorAsync(CancellationToken.None);

        r.Success.Should().BeFalse();
        r.Note.Should().Contain("no snapshot");
    }

    [Fact]
    public void SnapshotExists_tracks_file_existence()
    {
        var runner       = BuildHealthyRunner();
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"wintune-exist-{Guid.NewGuid()}.json");
        var sut          = new PowerService(runner, snapshotPath);

        sut.SnapshotExists.Should().BeFalse();

        File.WriteAllText(snapshotPath, "{}");
        sut.SnapshotExists.Should().BeTrue();

        File.Delete(snapshotPath);
        sut.SnapshotExists.Should().BeFalse();
    }

    [Fact]
    public async Task ApplySingleAsync_CpuMax_sets_only_that_knob()
    {
        var runner       = BuildHealthyRunner();
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"wintune-{Guid.NewGuid()}.json");
        var sut          = new PowerService(runner, snapshotPath);

        var r = await sut.ApplySingleAsync(BatteryKnob.CpuMax, CancellationToken.None);

        r.Success.Should().BeTrue();
        runner.Calls.Should().Contain(c =>
            c.Args.Contains("/setdcvalueindex") &&
            c.Args.Contains(PowerCfgIds.CpuMaxState) &&
            c.Args.Contains("100"));
        runner.Calls.Should().NotContain(c =>
            c.Args.Contains("/setdcvalueindex") &&
            c.Args.Contains(PowerCfgIds.Epp));
        File.Delete(snapshotPath);
    }

    [Fact]
    public async Task ApplySingleAsync_Epp_sets_only_Epp()
    {
        var runner       = BuildHealthyRunner();
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"wintune-{Guid.NewGuid()}.json");
        var sut          = new PowerService(runner, snapshotPath);

        await sut.ApplySingleAsync(BatteryKnob.Epp, CancellationToken.None);

        runner.Calls.Should().Contain(c =>
            c.Args.Contains("/setdcvalueindex") &&
            c.Args.Contains(PowerCfgIds.Epp) &&
            c.Args.Contains("0"));
        runner.Calls.Should().NotContain(c =>
            c.Args.Contains("/setdcvalueindex") &&
            c.Args.Contains(PowerCfgIds.CpuMaxState));
        File.Delete(snapshotPath);
    }

    [Fact]
    public async Task ApplySingleAsync_Cooling_writes_snapshot_first()
    {
        var runner       = BuildHealthyRunner();
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"wintune-{Guid.NewGuid()}.json");
        var sut          = new PowerService(runner, snapshotPath);

        await sut.ApplySingleAsync(BatteryKnob.Cooling, CancellationToken.None);

        File.Exists(snapshotPath).Should().BeTrue();
        File.Delete(snapshotPath);
    }
}
