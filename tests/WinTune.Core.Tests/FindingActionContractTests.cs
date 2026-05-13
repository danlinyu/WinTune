using FluentAssertions;
using WinTune.App.ViewModels;
using WinTune.Core.Models;
using WinTune.Core.Services;
using WinTune.Core.Tests.TestHelpers;

namespace WinTune.Core.Tests;

public class FindingActionContractTests
{
    [Fact]
    public async Task every_emitted_ActionId_resolves_in_registry()
    {
        var power = new FakePowerService
        {
            // Force one of every kind: all three throttled, snapshot present.
            NextState = new PowerState(
                ActiveScheme:              Guid.Empty,
                DcCpuMaxPct:               50,
                DcCpuMinPct:               5,
                DcEpp:                     80,
                DcCoolingPolicy:           "Passive",
                BatterySaverThresholdPct:  20),
            SnapshotExistsValue = true
        };
        var diag = new DiagnoseService(power);

        var findings = await diag.InvokeDiagnosticsAsync();

        var emittedIds = findings.SelectMany(f => f.Actions).Select(a => a.ActionId).ToHashSet();
        emittedIds.IsSubsetOf(DiagnoseViewModel.KnownActionIds).Should().BeTrue(
            "every emitted ActionId must be in DiagnoseViewModel.KnownActionIds — " +
            "missing: " + string.Join(", ", emittedIds.Except(DiagnoseViewModel.KnownActionIds)));
    }
}
