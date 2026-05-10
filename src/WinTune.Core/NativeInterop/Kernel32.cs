using System.Runtime.InteropServices;

namespace WinTune.Core.NativeInterop;

internal static partial class Kernel32
{
    // Thread-mode background hints. Unlike thread CPU priority, these also lower
    // the thread's *I/O* and *paging* priority — which is what makes a long-running
    // disk-bound scan stop monopolising the user's machine. Calling BEGIN raises
    // the kernel's IRP-priority cap to "very low" for IRPs issued on this thread,
    // and END restores normal. Must be called from inside the thread that will do
    // the I/O. A no-op on non-Windows OS, but the rest of the app is win-only too.
    //
    // Docs: https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadpriority
    internal const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
    internal const int THREAD_MODE_BACKGROUND_END = 0x00020000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetThreadPriority(IntPtr hThread, int nPriority);

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetCurrentThread();

    /// <summary>
    /// Run <paramref name="action"/> on the calling thread with very-low I/O and
    /// paging priority. Always restores normal priority on exit, even on exception.
    /// </summary>
    internal static void RunWithBackgroundIoPriority(Action action)
    {
        var handle = GetCurrentThread();
        bool entered = SetThreadPriority(handle, THREAD_MODE_BACKGROUND_BEGIN);
        try
        {
            action();
        }
        finally
        {
            if (entered)
            {
                _ = SetThreadPriority(handle, THREAD_MODE_BACKGROUND_END);
            }
        }
    }

    internal static T RunWithBackgroundIoPriority<T>(Func<T> func)
    {
        var handle = GetCurrentThread();
        bool entered = SetThreadPriority(handle, THREAD_MODE_BACKGROUND_BEGIN);
        try
        {
            return func();
        }
        finally
        {
            if (entered)
            {
                _ = SetThreadPriority(handle, THREAD_MODE_BACKGROUND_END);
            }
        }
    }
}
