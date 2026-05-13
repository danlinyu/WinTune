using FluentAssertions;
using WinTune.Core.Models;
using WinTune.Core.Services;
using WinTune.Core.Tests.TestHelpers;

namespace WinTune.Core.Tests;

public class DiagnoseServiceBatteryTests
{
    private static PowerState Healthy() => new(
        ActiveScheme:              Guid.Empty,
        DcCpuMaxPct:               100,
        DcCpuMinPct:               5,
        DcEpp:                     0,
        DcCoolingPolicy:           "Active",
        BatterySaverThresholdPct:  20);

    [Fact]
    public async Task healthy_state_emits_no_battery_finding()
    {
        var power = new FakePowerService { NextState = Healthy() };
        var sut   = new DiagnoseService(power);

        var findings = await sut.InvokeDiagnosticsAsync();

        findings.Should().NotContain(f => f.Id == "diag.battery");
    }

    [Fact]
    public async Task cpu_max_below_100_emits_red_finding_with_cpu_action()
    {
        var power = new FakePowerService { NextState = Healthy() with { DcCpuMaxPct = 50 } };
        var sut   = new DiagnoseService(power);

        var findings = await sut.InvokeDiagnosticsAsync();
        var bat      = findings.Single(f => f.Id == "diag.battery");

        bat.Severity.Should().Be(Severity.Red);
        bat.Detail.Should().Contain("CPU max");
        bat.Actions.Should().Contain(a => a.ActionId == "battery.unleash-all");
        bat.Actions.Should().Contain(a => a.ActionId == "battery.fix-cpu-max");
        bat.Actions.Should().NotContain(a => a.ActionId == "battery.fix-epp");
    }

    [Fact]
    public async Task epp_above_31_emits_red_finding_with_epp_action()
    {
        var power = new FakePowerService { NextState = Healthy() with { DcEpp = 80 } };
        var sut   = new DiagnoseService(power);

        var findings = await sut.InvokeDiagnosticsAsync();
        var bat      = findings.Single(f => f.Id == "diag.battery");

        bat.Detail.Should().Contain("EPP");
        bat.Actions.Should().Contain(a => a.ActionId == "battery.fix-epp");
    }

    [Fact]
    public async Task passive_cooling_emits_red_finding_with_cooling_action()
    {
        var power = new FakePowerService { NextState = Healthy() with { DcCoolingPolicy = "Passive" } };
        var sut   = new DiagnoseService(power);

        var findings = await sut.InvokeDiagnosticsAsync();
        var bat      = findings.Single(f => f.Id == "diag.battery");

        bat.Detail.Should().Contain("Cooling");
        bat.Actions.Should().Contain(a => a.ActionId == "battery.fix-cooling");
    }

    [Fact]
    public async Task all_three_throttled_includes_all_three_per_symptom_actions_plus_unleash()
    {
        var power = new FakePowerService
        {
            NextState = Healthy() with
            {
                DcCpuMaxPct     = 50,
                DcEpp           = 80,
                DcCoolingPolicy = "Passive"
            }
        };
        var sut = new DiagnoseService(power);

        var findings = await sut.InvokeDiagnosticsAsync();
        var bat      = findings.Single(f => f.Id == "diag.battery");

        bat.Title.Should().Contain("3 issue");
        bat.Actions.Select(a => a.ActionId).Should().Contain(new[]
        {
            "battery.unleash-all",
            "battery.fix-cpu-max",
            "battery.fix-epp",
            "battery.fix-cooling"
        });
    }

    [Fact]
    public async Task snapshot_present_adds_restore_action_even_when_healthy()
    {
        var power = new FakePowerService
        {
            NextState           = Healthy(),
            SnapshotExistsValue = true
        };
        var sut = new DiagnoseService(power);

        var findings = await sut.InvokeDiagnosticsAsync();

        var bat = findings.SingleOrDefault(f => f.Id == "diag.battery");
        bat.Should().NotBeNull();
        bat!.Actions.Should().Contain(a => a.ActionId == "battery.restore");
    }
}
