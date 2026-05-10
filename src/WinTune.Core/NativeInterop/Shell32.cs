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

    // SHFileOperationW for silent batch send-to-recycle-bin. The pFrom field is
    // a double-null-terminated wide string of source paths.
    [Flags]
    internal enum FOF : ushort
    {
        Silent           = 0x0004,  // no progress dialog
        NoConfirmation   = 0x0010,  // no "are you sure?" dialog
        AllowUndo        = 0x0040,  // send to Recycle Bin
        FilesOnly        = 0x0080,
        NoErrorUI        = 0x0400,  // suppress error dialogs (the key flag)
        NoConfirmMkDir   = 0x0200,
        WantNukeWarning  = 0x4000
    }

    internal enum FO : uint
    {
        Move   = 0x0001,
        Copy   = 0x0002,
        Delete = 0x0003,
        Rename = 0x0004
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public FO wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public FOF fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    // SHFileOperationW takes a struct pointer, so DllImport (not LibraryImport)
    // is the simplest path here — the source generator doesn't yet handle the
    // string-marshalled struct cleanly across all .NET 10 SDK builds.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    internal static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);
}
