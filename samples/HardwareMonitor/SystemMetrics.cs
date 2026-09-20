using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace HardwareMonitor;

internal static class SystemMetrics
{
    private static readonly string[] VirtualAdapterMarkers =
    {
        "virtual", "remote display", "indirect display", "idd", "basic render", "parsec", "sunshine",
    };

    public static string CpuName { get; } = ReadCpuName();

    public static IReadOnlyList<string> GpuNames { get; } = ReadGpuNames();

    public static int ProcessorGroupCount
    {
        get
        {
            try
            {
                return Math.Max(1, (int)GetActiveProcessorGroupCount());
            }
            catch
            {
                return 1;
            }
        }
    }

    public static (long Idle, long Busy) ReadCpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return (0, 0);
        }

        var idleTicks = ToTicks(idle);
        var kernelTicks = ToTicks(kernel);
        var userTicks = ToTicks(user);
        return (idleTicks, Math.Max(0, kernelTicks - idleTicks) + userTicks);
    }

    public static List<NetworkInterface> ActiveAdapters()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(a => a.OperationalStatus == OperationalStatus.Up
                        && a.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && a.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                        && HasGateway(a))
            .ToList();

    private static bool HasGateway(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().GatewayAddresses.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static long ToTicks(System.Runtime.InteropServices.ComTypes.FILETIME time)
        => ((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

    private static string ReadCpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }
        }
        catch
        {
        }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "CPU";
    }

    private static IReadOnlyList<string> ReadGpuNames()
    {
        var names = new List<string>();
        try
        {
            using var classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");

            if (classKey != null)
            {
                foreach (var subName in classKey.GetSubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                {
                    using var adapterKey = classKey.OpenSubKey(subName);
                    if (adapterKey?.GetValue("DriverDesc") is not string description) continue;
                    var trimmed = description.Trim();
                    if (trimmed.Length == 0 || names.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) continue;
                    if (VirtualAdapterMarkers.Any(m => trimmed.Contains(m, StringComparison.OrdinalIgnoreCase))) continue;
                    names.Add(trimmed);
                }
            }
        }
        catch
        {
        }

        return names;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(
        out System.Runtime.InteropServices.ComTypes.FILETIME idleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();
}
