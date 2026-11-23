using System.Runtime.InteropServices;

namespace HardwareMonitor;

// Names and counter identities must come from the same adapter enumeration.
internal static class GpuAdapters
{
    internal sealed record Adapter(string Name, long Luid);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid iid, out nint factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters(nint self, uint index, out nint adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc(nint self, out Description desc);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Description
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
        public uint Vendor, Device, Subsystem, Revision;
        public nuint DedicatedVideo, DedicatedSystem, SharedSystem;
        public long Luid;
        public uint Flags;
    }
    private static T Method<T>(nint obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * nint.Size));

    internal static IReadOnlyList<Adapter> Read()
    {
        var result = new List<Adapter>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        nint factory = 0;
        try
        {
            Marshal.ThrowExceptionForHR(CreateDXGIFactory1(in iid, out factory));
            var enumerate = Method<EnumAdapters>(factory, 12); // EnumAdapters1
            for (uint i = 0; ; i++)
            {
                int hr = enumerate(factory, i, out var adapter);
                if (hr == unchecked((int)0x887A0002)) break; // DXGI_ERROR_NOT_FOUND
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    Marshal.ThrowExceptionForHR(Method<GetDesc>(adapter, 10)(adapter, out var desc));
                    if ((desc.Flags & 2) == 0) result.Add(new(desc.Name.Trim(), desc.Luid));
                }
                finally { Marshal.Release(adapter); }
            }
        }
        catch { /* No guessed index pairing when DXGI is unavailable. */ }
        finally { if (factory != 0) Marshal.Release(factory); }
        return result;
    }
}
