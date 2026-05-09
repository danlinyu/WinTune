using System.Runtime.InteropServices;

namespace WinTune.Core.NativeInterop;

internal static partial class Shell32
{
    [Flags]
    internal enum SHERB : uint
    {
        NoConfirmation = 0x00000001,
        NoProgressUI   = 0x00000002,
        NoSound        = 0x00000004
    }

    [LibraryImport("shell32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHEmptyRecycleBinW(
        IntPtr hwnd,
        string? pszRootPath,
        SHERB dwFlags);
}
