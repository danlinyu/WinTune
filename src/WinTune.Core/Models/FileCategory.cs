namespace WinTune.Core.Models;

/// <summary>
/// Where a duplicate file sits in the user's home, in terms of "is the user the
/// person responsible for this file or is some tool managing it on the user's behalf".
/// Drives the default UI filter and the smart-keeper auto-selection.
/// </summary>
public enum FileCategory
{
    /// <summary>
    /// Personal content the user owns and arranges manually: Desktop, Documents,
    /// Pictures, Videos, Music, Downloads, OneDrive, Dropbox, Box, iCloudDrive.
    /// Real recoverable space if duplicated.
    /// </summary>
    UserContent,

    /// <summary>
    /// User-content backups (e.g. ".bak", ".old", "Copy of …"). Slight nuance vs
    /// UserContent — usually safe to consolidate, surface separately so the UI can
    /// suggest keeping the newest non-backup copy.
    /// </summary>
    Backup,

    /// <summary>
    /// Tooling content stores managed by package managers, IDEs, runtimes, build
    /// systems. Looks like duplicates because each version of the tool re-derives
    /// or re-downloads the same artefact. NEVER user-deletable safely.
    /// </summary>
    BuildOutput,

    /// <summary>
    /// Application data (AppData/*, browser User Data trees, app-specific caches
    /// not caught by BuildOutput). User can theoretically clean it, but consequences
    /// are app-specific and the wins are usually marginal. Hidden by default.
    /// </summary>
    AppManaged,

    /// <summary>
    /// Anything that didn't match a more specific bucket. Worth showing.
    /// </summary>
    Other
}
