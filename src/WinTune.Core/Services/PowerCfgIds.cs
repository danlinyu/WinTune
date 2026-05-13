namespace WinTune.Core.Services;

/// <summary>
/// Well-known powercfg subgroup and setting GUIDs. Values are documented in
/// Microsoft's "Power Settings" reference and are stable across Windows 10/11 SKUs.
/// </summary>
public static class PowerCfgIds
{
    // Subgroup: Processor power management
    public const string SubProcessor       = "54533251-82be-4824-96c1-47b60b740d00";

    // Settings under Processor subgroup
    public const string CpuMaxState        = "bc5038f7-23e0-4960-96da-33abaf5935ec";
    public const string CpuMinState        = "893dee8e-2bef-41e0-89c6-b55d0929964c";
    public const string Epp                = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";
    public const string CoolingPolicy      = "94d3a615-a899-4ac5-ae2b-e4d8f634367f";

    // Subgroup: Energy saver settings (Win10+)
    public const string SubEnergySaver     = "de830923-a562-41af-a086-e3a2c6bad2da";
    public const string BatterySaverThresh = "e69653ca-cf7f-4f05-aa73-cb833fa90ad4";
}
