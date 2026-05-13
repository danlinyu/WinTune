using FluentAssertions;
using WinTune.Core.Models;
using WinTune.Core.Services;
using WinTune.Core.Tests.TestHelpers;

namespace WinTune.Core.Tests;

public class DiagnoseServiceTests
{
    [Fact]
    public async Task InvokeDiagnosticsAsync_returns_findings_with_required_fields()
    {
        IDiagnoseService sut = new DiagnoseService(new FakePowerService());

        var findings = await sut.InvokeDiagnosticsAsync();

        findings.Should().NotBeNull();
        // On a real Windows box at least the disk-health Green finding fires
        // unless MSFT_PhysicalDisk is unavailable (LTSC / restricted images).
        // Don't assert non-empty; do assert shape.
        findings.Should().OnlyContain(f => !string.IsNullOrEmpty(f.Title));
        findings.Should().OnlyContain(f => !string.IsNullOrEmpty(f.Detail));
    }

    [Fact]
    public async Task InvokeDiagnosticsAsync_severities_are_in_known_set()
    {
        IDiagnoseService sut = new DiagnoseService(new FakePowerService());

        var findings = await sut.InvokeDiagnosticsAsync();

        findings.Should().OnlyContain(f =>
            f.Severity == Severity.Red ||
            f.Severity == Severity.Yellow ||
            f.Severity == Severity.Green);
    }

    [Fact]
    public async Task InvokeDiagnosticsAsync_findings_have_non_empty_Id()
    {
        IDiagnoseService sut = new DiagnoseService(new FakePowerService());
        var findings = await sut.InvokeDiagnosticsAsync();
        findings.Should().OnlyContain(f => !string.IsNullOrEmpty(f.Id));
    }

    [Fact]
    public async Task InvokeDiagnosticsAsync_respects_cancellation()
    {
        IDiagnoseService sut = new DiagnoseService(new FakePowerService());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await sut.InvokeDiagnosticsAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // The destructive fixes (Reset-QuickAccess, Disable-Telemetry, Classic
    // Right-Click toggles, Rebuild-Index) mutate user/system state; they're
    // covered by manual smoke + the live PowerShell suite that already exists
    // on the same logic. We don't run them in unit tests.
    [Fact(Skip = "destructive: mutates HKCU and AppData — run manually")]
    public async Task EnableClassicRightClickAsync_smoke_local_only()
    {
        IDiagnoseService sut = new DiagnoseService(new FakePowerService());
        var r = await sut.EnableClassicRightClickAsync();
        r.Success.Should().BeTrue();
    }
}
