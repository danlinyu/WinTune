using System.Runtime.InteropServices;

namespace WinTune.Core.NativeInterop;

internal static partial class PsApi
{
    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyWorkingSet(IntPtr hProcess);
}
