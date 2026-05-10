using System.Runtime.InteropServices;

namespace WinTune.Core.NativeInterop;

internal static partial class DnsApi
{
    [LibraryImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DnsFlushResolverCache();
}
