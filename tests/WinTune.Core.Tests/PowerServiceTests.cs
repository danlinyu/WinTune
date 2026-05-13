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
}
