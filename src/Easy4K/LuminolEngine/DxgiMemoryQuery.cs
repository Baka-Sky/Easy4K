using System.Runtime.InteropServices;

namespace Easy4K.LuminolEngine;

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

    /// <summary>按 DXGI 枚举顺序返回所有适配器名称（索引 i 即 ncnn -g 的候选序号）。
    /// 顺序与 Vulkan 物理设备枚举基本一致；软件适配器（Microsoft Basic Render Driver）跳过。</summary>
    public static List<string> ReadAdapterNames()
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IntPtr factoryPtr = IntPtr.Zero;
        try
        {
            var guid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            int hr = CreateDXGIFactory1(ref guid, out factoryPtr);
            if (hr != 0 || factoryPtr == IntPtr.Zero) return names;

            IntPtr vtbl = Marshal.ReadIntPtr(factoryPtr);
            var enumAdapters1 = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(
                Marshal.ReadIntPtr(vtbl, 12 * IntPtr.Size));
            var releaseFactory = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(
                Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));

            for (uint i = 0; i < 8; i++)
            {
                int hr2 = enumAdapters1(factoryPtr, i, out IntPtr adapterPtr);
                if (hr2 != 0 || adapterPtr == IntPtr.Zero) break;

                IntPtr avtbl = Marshal.ReadIntPtr(adapterPtr);
                var getDesc1 = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(
                    Marshal.ReadIntPtr(avtbl, 10 * IntPtr.Size));
                var releaseAdapter = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(
                    Marshal.ReadIntPtr(avtbl, 2 * IntPtr.Size));

                getDesc1(adapterPtr, out var desc);
                // DXGI_ADAPTER_FLAG_SOFTWARE = 2：软件适配器（Microsoft Basic Render Driver）跳过；
                // 另外跳过虚拟显示器驱动（Oray/ToDesk/IDD 之类），它们不会出现在 Vulkan 设备列表里
                var isSoftware = (desc.Flags & 2u) != 0;
                if (!isSoftware && !GpuDetector.IsVirtualGpu(desc.Description))
                {
                    // 虚拟显示器驱动（如 GameViewer/ToDesk）会把自己的适配器报成主显卡同名，按名字去重
                    var name = desc.Description.Trim();
                    if (seen.Add(name)) names.Add(name);
                }

                releaseAdapter(adapterPtr);
            }

            releaseFactory(factoryPtr);
        }
        catch { }
        return names;
    }

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
