using System.Runtime.InteropServices;

namespace Easy4K.Services;

/// <summary>用 DXGI 精确读取显卡专用显存（字节）。WMI AdapterRAM 是 uint32，
/// 对 &gt;4GB 显存会溢出（如 RX 580 8GB 被读成约 4GB），DXGI 用 64 位无此问题。</summary>
internal static class DxgiMemoryQuery
{
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public Luid AdapterLuid;
        public uint Flags;
    }

    // IDXGIAdapter：仅供 EnumAdapters 参数类型使用（实际走 EnumAdapters1）
    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter
    {
        void QueryInterface(ref Guid riid, out IntPtr ppv);
        uint AddRef();
        uint Release();
    }

    // IDXGIAdapter1
    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void QueryInterface(ref Guid riid, out IntPtr ppv);
        uint AddRef();
        uint Release();
        void SetPrivateData(ref Guid guid, uint dataSize, IntPtr data);
        void SetPrivateDataInterface(ref Guid guid, IntPtr data);
        void GetPrivateData(ref Guid guid, ref uint dataSize, IntPtr data);
        void GetParent(ref Guid riid, out IntPtr parent);
        void EnumOutputs(uint index, out IntPtr output);
        void GetDesc(out DxgiAdapterDesc desc);
        void CheckInterfaceSupport(ref Guid guid, out long umdVersion);
        void GetDesc1(out DxgiAdapterDesc1 desc);
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
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public Luid AdapterLuid;
    }

    // IDXGIFactory1
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void QueryInterface(ref Guid riid, out IntPtr ppv);
        uint AddRef();
        uint Release();
        void SetPrivateData(ref Guid guid, uint dataSize, IntPtr data);
        void SetPrivateDataInterface(ref Guid guid, IntPtr data);
        void GetPrivateData(ref Guid guid, ref uint dataSize, IntPtr data);
        void GetParent(ref Guid riid, out IntPtr parent);
        void EnumAdapters(uint index, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter adapter);
        void MakeWindowAssociation(IntPtr hwnd, uint flags);
        void GetWindowAssociation(out IntPtr hwnd);
        void CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
        void CreateSoftwareAdapter(IntPtr module, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter adapter);
        void EnumAdapters1(uint index, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1 adapter);
        [return: MarshalAs(UnmanagedType.Bool)]
        bool IsCurrent();
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetDesc1Delegate(IntPtr self, out DxgiAdapterDesc1 desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr self);

    /// <summary>遍历所有适配器，返回专用显存最大的那个的字节数。用手动 vtable 调用（ComImport 自动 marshaling 在该接口上不稳定）。</summary>
    public static long ReadDedicatedVideoMemory()
    {
        try
        {
            var guid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            int hr = CreateDXGIFactory1(ref guid, out IntPtr factoryPtr);
            if (hr != 0 || factoryPtr == IntPtr.Zero) return 0;

            IntPtr vtbl = Marshal.ReadIntPtr(factoryPtr);
            // IDXGIFactory1.EnumAdapters1 是 vtable 索引 12
            var enumAdapters1 = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(
                Marshal.ReadIntPtr(vtbl, 12 * IntPtr.Size));
            // IUnknown.Release 是 vtable 索引 2
            var releaseFactory = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(
                Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));

            long max = 0;
            for (uint i = 0; i < 8; i++)
            {
                int hr2 = enumAdapters1(factoryPtr, i, out IntPtr adapterPtr);
                if (hr2 != 0 || adapterPtr == IntPtr.Zero) break;

                IntPtr avtbl = Marshal.ReadIntPtr(adapterPtr);
                // IDXGIAdapter1.GetDesc1 是 vtable 索引 10
                var getDesc1 = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(
                    Marshal.ReadIntPtr(avtbl, 10 * IntPtr.Size));
                var releaseAdapter = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(
                    Marshal.ReadIntPtr(avtbl, 2 * IntPtr.Size));

                getDesc1(adapterPtr, out var desc);
                var mem = (long)desc.DedicatedVideoMemory;
                if (mem > max) max = mem;

                releaseAdapter(adapterPtr);
            }

            releaseFactory(factoryPtr);
            return max;
        }
        catch
        {
            return 0;
        }
    }
}
