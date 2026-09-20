using System.Runtime.InteropServices;

namespace HardwareMonitor;

internal sealed class PdhQuery : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhCstatusValidData = 0x00000000;
    private const uint PdhCstatusNewData = 0x00000001;

    private readonly IntPtr _query;
    private readonly List<IntPtr> _counters = new();

    public PdhQuery(params string[] counterPaths)
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            return;
        }

        foreach (var path in counterPaths)
        {
            if (PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter) == 0)
            {
                _counters.Add(counter);
            }
        }
    }

    public bool IsValid => _query != IntPtr.Zero && _counters.Count > 0;

    public void Collect()
    {
        if (_query != IntPtr.Zero) PdhCollectQueryData(_query);
    }

    public IReadOnlyList<(string Instance, double Value)> Read(int counterIndex)
    {
        var results = new List<(string, double)>();
        if (counterIndex < 0 || counterIndex >= _counters.Count) return results;

        var counter = _counters[counterIndex];
        uint size = 0;
        uint count = 0;
        var status = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, out count, IntPtr.Zero);
        if (status != PdhMoreData || size == 0) return results;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, out count, buffer);
            if (status != 0) return results;

            var itemSize = Marshal.SizeOf<PdhFmtCounterValueItemW>();
            for (var i = 0; i < count; i++)
            {
                var itemPtr = IntPtr.Add(buffer, i * itemSize);
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItemW>(itemPtr);
                var valueStatus = item.Value.Status;
                if (valueStatus is not (PdhCstatusValidData or PdhCstatusNewData)) continue;
                if (double.IsNaN(item.Value.Value)) continue;
                results.Add((item.Name ?? string.Empty, item.Value.Value));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return results;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint Status;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItemW
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Name;

        public PdhFmtCounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
}
