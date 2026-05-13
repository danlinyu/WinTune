using System.Globalization;
using System.Text.RegularExpressions;
using WinTune.Core.Models;

namespace WinTune.Core.Services;

public sealed class PowerService : IPowerService
{
    private static readonly string[] UnhideEppArgs      = { "/attributes", PowerCfgIds.SubProcessor, PowerCfgIds.Epp,           "-ATTRIB_HIDE" };
    private static readonly string[] UnhideCoolingArgs  = { "/attributes", PowerCfgIds.SubProcessor, PowerCfgIds.CoolingPolicy, "-ATTRIB_HIDE" };
    private static readonly string[] GetActiveSchemeArgs = { "/getactivescheme" };

    private readonly IProcessRunner _proc;
    private readonly string         _snapshotPath;
    private bool                    _unhidden;

    public PowerService(IProcessRunner proc, string? snapshotPath = null)
    {
        _proc         = proc;
        _snapshotPath = snapshotPath ?? DefaultSnapshotPath();
    }

    public bool SnapshotExists => File.Exists(_snapshotPath);

    public async Task<PowerState> CaptureCurrentAsync(CancellationToken ct)
    {
        await EnsureUnhiddenAsync(ct);

        var scheme = await GetActiveSchemeAsync(ct);

        var cpuMax  = await QueryDcIntAsync(PowerCfgIds.SubProcessor,   PowerCfgIds.CpuMaxState,        ct);
        var cpuMin  = await QueryDcIntAsync(PowerCfgIds.SubProcessor,   PowerCfgIds.CpuMinState,        ct);
        var epp     = await QueryDcIntAsync(PowerCfgIds.SubProcessor,   PowerCfgIds.Epp,                ct);
        var cooling = await QueryDcIntAsync(PowerCfgIds.SubProcessor,   PowerCfgIds.CoolingPolicy,      ct);
        var saver   = await QueryDcIntAsync(PowerCfgIds.SubEnergySaver, PowerCfgIds.BatterySaverThresh, ct);

        var coolingLabel = cooling switch
        {
            0 => "Passive",
            1 => "Active",
            _ => throw new InvalidOperationException($"unexpected cooling-policy index {cooling}")
        };

        return new PowerState(
            ActiveScheme:              scheme,
            DcCpuMaxPct:               cpuMax,
            DcCpuMinPct:               cpuMin,
            DcEpp:                     epp,
            DcCoolingPolicy:           coolingLabel,
            BatterySaverThresholdPct:  saver);
    }

    public Task<PowerFixResult> ApplyTierBAsync   (CancellationToken ct) => throw new NotImplementedException();
    public Task<PowerFixResult> ApplySingleAsync  (BatteryKnob knob, CancellationToken ct) => throw new NotImplementedException();
    public Task<PowerFixResult> RestorePriorAsync (CancellationToken ct) => throw new NotImplementedException();

    private async Task EnsureUnhiddenAsync(CancellationToken ct)
    {
        if (_unhidden) return;
        // EPP and cooling-policy carry ATTRIB_HIDE by default on stock Windows; unhide
        // before any /query — see spec section 5.1 for the rationale.
        await _proc.RunAsync("powercfg", UnhideEppArgs,     ct);
        await _proc.RunAsync("powercfg", UnhideCoolingArgs, ct);
        _unhidden = true;
    }

    private async Task<Guid> GetActiveSchemeAsync(CancellationToken ct)
    {
        var r = await _proc.RunAsync("powercfg", GetActiveSchemeArgs, ct);
        var m = Regex.Match(r.Stdout, @"Power Scheme GUID:\s*([0-9a-fA-F\-]{36})");
        if (!m.Success) throw new InvalidOperationException("could not parse active power scheme GUID");
        return Guid.Parse(m.Groups[1].Value);
    }

    private async Task<int> QueryDcIntAsync(string subgroup, string setting, CancellationToken ct)
    {
        var r = await _proc.RunAsync("powercfg",
            new[] { "/query", "SCHEME_CURRENT", subgroup, setting }, ct);
        var m = Regex.Match(r.Stdout, @"Current DC Power Setting Index:\s*0x([0-9a-fA-F]+)");
        if (!m.Success) throw new InvalidOperationException(
            $"could not parse DC index for setting {setting}");
        return int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static string DefaultSnapshotPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinTune");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "battery-snapshot.json");
    }
}
