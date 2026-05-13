using System.Globalization;
using System.Text.RegularExpressions;
using WinTune.Core.Models;

namespace WinTune.Core.Services;

public sealed class PowerService : IPowerService
{
    private static readonly string[] UnhideEppArgs       = { "/attributes", PowerCfgIds.SubProcessor, PowerCfgIds.Epp,           "-ATTRIB_HIDE" };
    private static readonly string[] UnhideCoolingArgs   = { "/attributes", PowerCfgIds.SubProcessor, PowerCfgIds.CoolingPolicy, "-ATTRIB_HIDE" };
    private static readonly string[] GetActiveSchemeArgs = { "/getactivescheme" };
    private static readonly string[] SetActiveArgs       = { "/setactive", "SCHEME_CURRENT" };

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

    public async Task<PowerFixResult> ApplyTierBAsync(CancellationToken ct)
    {
        var before = await CaptureCurrentAsync(ct);

        if (!File.Exists(_snapshotPath))
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(before);
                await File.WriteAllTextAsync(_snapshotPath, json, ct);
            }
            catch (Exception ex)
            {
                return new PowerFixResult(false, null,
                    "could not write snapshot — aborting fix to keep state reversible",
                    new[] { ex.Message });
            }
        }

        var errors = new List<string>();
        await SetDcAsync(PowerCfgIds.CpuMaxState,   100, errors, ct);
        await SetDcAsync(PowerCfgIds.Epp,             0, errors, ct);
        await SetDcAsync(PowerCfgIds.CoolingPolicy,   1, errors, ct);
        await CommitActiveSchemeAsync(errors, ct);

        if (errors.Count > 0)
            return new PowerFixResult(false, null, "one or more powercfg writes failed", errors);

        var after = await CaptureCurrentAsync(ct);
        return new PowerFixResult(true, after, "Tier B applied", null);
    }

    public async Task<PowerFixResult> ApplySingleAsync(BatteryKnob knob, CancellationToken ct)
    {
        var before = await CaptureCurrentAsync(ct);

        if (!File.Exists(_snapshotPath))
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(before);
                await File.WriteAllTextAsync(_snapshotPath, json, ct);
            }
            catch (Exception ex)
            {
                return new PowerFixResult(false, null,
                    "could not write snapshot — aborting fix to keep state reversible",
                    new[] { ex.Message });
            }
        }

        var errors = new List<string>();
        switch (knob)
        {
            case BatteryKnob.CpuMax:
                await SetDcAsync(PowerCfgIds.CpuMaxState,  100, errors, ct);
                break;
            case BatteryKnob.Epp:
                await SetDcAsync(PowerCfgIds.Epp,            0, errors, ct);
                break;
            case BatteryKnob.Cooling:
                await SetDcAsync(PowerCfgIds.CoolingPolicy,  1, errors, ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(knob));
        }
        await CommitActiveSchemeAsync(errors, ct);

        if (errors.Count > 0)
            return new PowerFixResult(false, null, $"powercfg write failed for {knob}", errors);

        var after = await CaptureCurrentAsync(ct);
        return new PowerFixResult(true, after, $"{knob} set", null);
    }

    public async Task<PowerFixResult> RestorePriorAsync(CancellationToken ct)
    {
        if (!File.Exists(_snapshotPath))
            return new PowerFixResult(false, null, "no snapshot — nothing to restore", null);

        PowerState prior;
        try
        {
            var json = await File.ReadAllTextAsync(_snapshotPath, ct);
            prior = System.Text.Json.JsonSerializer.Deserialize<PowerState>(json)
                ?? throw new InvalidOperationException("snapshot deserialized to null");
        }
        catch (Exception ex)
        {
            return new PowerFixResult(false, null, "snapshot file unreadable", new[] { ex.Message });
        }

        var errors  = new List<string>();
        var cooling = prior.DcCoolingPolicy switch
        {
            "Passive" => 0,
            "Active"  => 1,
            _         => throw new InvalidOperationException(
                $"snapshot cooling-policy '{prior.DcCoolingPolicy}' is not Passive/Active")
        };

        await SetDcAsync(PowerCfgIds.CpuMaxState,   prior.DcCpuMaxPct, errors, ct);
        await SetDcAsync(PowerCfgIds.CpuMinState,   prior.DcCpuMinPct, errors, ct);
        await SetDcAsync(PowerCfgIds.Epp,           prior.DcEpp,       errors, ct);
        await SetDcAsync(PowerCfgIds.CoolingPolicy, cooling,           errors, ct);
        await CommitActiveSchemeAsync(errors, ct);

        if (errors.Count > 0)
            return new PowerFixResult(false, null, "one or more powercfg writes failed", errors);

        File.Delete(_snapshotPath);
        var after = await CaptureCurrentAsync(ct);
        return new PowerFixResult(true, after, "restored to prior DC settings", null);
    }

    private async Task SetDcAsync(string settingGuid, int value, List<string> errors, CancellationToken ct)
    {
        var r = await _proc.RunAsync("powercfg",
            new[] { "/setdcvalueindex", "SCHEME_CURRENT", PowerCfgIds.SubProcessor, settingGuid,
                    value.ToString(CultureInfo.InvariantCulture) }, ct);
        if (r.ExitCode != 0)
            errors.Add($"setdcvalueindex {settingGuid} -> {value}: exit {r.ExitCode}; {r.Stderr.Trim()}");
    }

    private async Task CommitActiveSchemeAsync(List<string> errors, CancellationToken ct)
    {
        var r = await _proc.RunAsync("powercfg", SetActiveArgs, ct);
        if (r.ExitCode != 0)
            errors.Add($"setactive: exit {r.ExitCode}; {r.Stderr.Trim()}");
    }

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
