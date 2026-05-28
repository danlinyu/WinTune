using System.Runtime.InteropServices;

namespace WinTune.Core.NativeInterop;

internal static class Dxgi
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint DxgiAdapterFlagSoftware = 2;

    private static readonly Guid IdxgiFactory1Guid =
        new("770aae78-f26f-4dba-a829-253c83d1b387");

    public static IReadOnlyList<DxgiAdapterMemory> EnumerateAdapters()
    {
        var adapters = new List<DxgiAdapterMemory>();
        var factoryGuid = IdxgiFactory1Guid;
        int hr = CreateDXGIFactory1(ref factoryGuid, out var factory);
        if (hr < 0 || factory is null)
        {
            return adapters;
        }

        try
        {
            for (uint i = 0; ; i++)
            {
                hr = factory.EnumAdapters1(i, out var adapter);
                if (hr == DxgiErrorNotFound) break;
                if (hr < 0 || adapter is null) continue;

                try
                {
                    hr = adapter.GetDesc1(out var desc);
                    if (hr < 0) continue;
                    if ((desc.Flags & DxgiAdapterFlagSoftware) != 0) continue;

                    ulong dedicatedBytes = desc.DedicatedVideoMemory.ToUInt64();
                    adapters.Add(new DxgiAdapterMemory(
                        desc.Description.TrimEnd('\0'),
                        dedicatedBytes));
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }

        return adapters;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1? ppFactory);

    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig]
        int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);

        [PreserveSig]
        int SetPrivateDataInterface(ref Guid name, IntPtr unknown);

        [PreserveSig]
        int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);

        [PreserveSig]
        int GetParent(ref Guid riid, out IntPtr parent);

        [PreserveSig]
        int EnumAdapters(uint adapter, out IntPtr dxgiAdapter);

        [PreserveSig]
        int MakeWindowAssociation(IntPtr windowHandle, uint flags);

        [PreserveSig]
        int GetWindowAssociation(out IntPtr windowHandle);

        [PreserveSig]
        int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);

        [PreserveSig]
        int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);

        [PreserveSig]
        int EnumAdapters1(uint adapter, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1? dxgiAdapter);

        [PreserveSig]
        int IsCurrent();
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig]
        int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);

        [PreserveSig]
        int SetPrivateDataInterface(ref Guid name, IntPtr unknown);

        [PreserveSig]
        int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);

        [PreserveSig]
        int GetParent(ref Guid riid, out IntPtr parent);

        [PreserveSig]
        int EnumOutputs(uint output, out IntPtr dxgiOutput);

        [PreserveSig]
        int GetDesc(out DxgiAdapterDesc desc);

        [PreserveSig]
        int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);

        [PreserveSig]
        int GetDesc1(out DxgiAdapterDesc1 desc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public Luid AdapterLuid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public Luid AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }
}

internal sealed record DxgiAdapterMemory(
    string Description,
    ulong DedicatedVideoMemoryBytes);
