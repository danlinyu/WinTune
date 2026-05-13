using FluentAssertions;
using WinTune.Core.Models;

namespace WinTune.Core.Tests;

public class PowerStateTests
{
    [Fact]
    public void PowerState_records_all_dc_fields()
    {
        var s = new PowerState(
            ActiveScheme:              Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"),
            DcCpuMaxPct:               50,
            DcCpuMinPct:               5,
            DcEpp:                     80,
            DcCoolingPolicy:           "Passive",
            BatterySaverThresholdPct:  20);

        s.DcCpuMaxPct.Should().Be(50);
        s.DcEpp.Should().Be(80);
        s.DcCoolingPolicy.Should().Be("Passive");
    }

    [Fact]
    public void PowerFixResult_failure_carries_errors()
    {
        var r = new PowerFixResult(
            Success: false,
            After:   null,
            Note:    "powercfg failed",
            Errors:  new[] { "exit 1" });

        r.Success.Should().BeFalse();
        r.Errors.Should().ContainSingle();
    }

    [Fact]
    public void BatteryKnob_enum_has_three_members()
    {
        Enum.GetValues<BatteryKnob>().Should().BeEquivalentTo(new[]
        {
            BatteryKnob.CpuMax, BatteryKnob.Epp, BatteryKnob.Cooling
        });
    }
}
