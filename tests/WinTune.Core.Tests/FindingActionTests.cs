using FluentAssertions;
using WinTune.Core.Models;

namespace WinTune.Core.Tests;

public class FindingActionTests
{
    [Fact]
    public void FindingAction_carries_id_label_and_optional_confirm()
    {
        var a = new FindingAction("battery.unleash-all", "Unleash all", Confirm: null);

        a.ActionId.Should().Be("battery.unleash-all");
        a.Label.Should().Be("Unleash all");
        a.Confirm.Should().BeNull();
    }

    [Fact]
    public void FindingAction_with_confirm_text_round_trips()
    {
        var a = new FindingAction(
            "battery.restore",
            "Restore prior",
            Confirm: "Revert DC power settings to your prior values. Continue?");

        a.Confirm.Should().StartWith("Revert");
    }

    [Fact]
    public void FindingActionResult_success_has_no_errors()
    {
        var r = new FindingActionResult(Success: true, Note: "done", Errors: null);

        r.Success.Should().BeTrue();
        r.Errors.Should().BeNull();
    }

    [Fact]
    public void FindingActionResult_failure_carries_errors()
    {
        var r = new FindingActionResult(
            Success: false,
            Note: "powercfg failed",
            Errors: new[] { "exit code 1", "Access denied." });

        r.Success.Should().BeFalse();
        r.Errors.Should().HaveCount(2);
    }
}
