using System.Management;
using Windows.System.Power;
using WinBattery = Windows.Devices.Power.Battery;

namespace WinIsland.Modules.Battery;

/// <summary>
/// 电池高级信息快照：WinRT 报告（剩余/满充/设计容量、充电速率、状态）＋ root\WMI 的
/// 电池类（循环次数、电压、放电速率）。任何一项拿不到就是 null，界面显示 "—"。
/// </summary>
internal readonly record struct BatteryPowerInfo(
    int? DesignedCapacityMwh,
    int? FullChargeCapacityMwh,
    int? RemainingCapacityMwh,
    int? CycleCount,
    int? VoltageMv,
    int? ChargeRateMw,
    int? DischargeRateMw,
    bool Charging,
    bool Discharging)
{
    /// <summary>电池健康度 = 满充容量 ÷ 设计容量。</summary>
    public double? HealthPercent => DesignedCapacityMwh is > 0 && FullChargeCapacityMwh is > 0
        ? FullChargeCapacityMwh.Value * 100.0 / DesignedCapacityMwh.Value
        : null;

    /// <summary>放电时按当前功率估算的剩余可用时间。</summary>
    public TimeSpan? RemainingTime => Discharging && DischargeRateMw is > 0 && RemainingCapacityMwh is > 0
        ? TimeSpan.FromHours(RemainingCapacityMwh.Value / (double)DischargeRateMw.Value)
        : null;

    /// <summary>充电时按当前功率估算的充满时间。</summary>
    public TimeSpan? UntilFullTime => !Discharging
                                      && ChargeRateMw is > 0
                                      && FullChargeCapacityMwh is > 0
                                      && RemainingCapacityMwh is > 0
                                      && FullChargeCapacityMwh > RemainingCapacityMwh
        ? TimeSpan.FromHours((FullChargeCapacityMwh.Value - RemainingCapacityMwh.Value) / (double)ChargeRateMw.Value)
        : null;
}

/// <summary>电池数值的统一格式化（岛体展开面板与聚光卡共用）。</summary>
internal static class BatteryFormat
{
    public static string Power(int milliwatts) => $"{milliwatts / 1000.0:F1} W";

    public static string Energy(int milliwattHours) => $"{milliwattHours / 1000.0:F1} Wh";

    public static string Duration(TimeSpan time)
    {
        if (time.TotalMinutes < 1) return "不到 1 分钟";
        if (time.TotalHours >= 1) return $"{(int)time.TotalHours} 小时 {time.Minutes} 分";
        return $"{time.Minutes} 分钟";
    }
}

/// <summary>读数器：WinRT 每次都读；WMI 很慢（上百毫秒）所以后台刷新 + 缓存。</summary>
internal static class BatteryProbe
{
    private const int WmiCacheMs = 8000;

    private static readonly Lock Gate = new();
    private static int? _designedCapacityMwh;
    private static int? _cycleCount;
    private static int? _voltageMv;
    private static int? _wmiChargeRateMw;
    private static int? _wmiDischargeRateMw;
    private static long _wmiAt;
    private static bool _wmiInFlight;

    public static BatteryPowerInfo Read()
    {
        int? remaining = null;
        int? fullCharge = null;
        int? designed = null;
        int? chargeRate = null;
        bool charging = false;
        bool discharging = false;

        try
        {
            var report = WinBattery.AggregateBattery?.GetReport();
            remaining = report?.RemainingCapacityInMilliwattHours;
            fullCharge = report?.FullChargeCapacityInMilliwattHours;
            chargeRate = report?.ChargeRateInMilliwatts;
            designed = report?.DesignCapacityInMilliwattHours;
            charging = report?.Status == BatteryStatus.Charging;
            discharging = report?.Status == BatteryStatus.Discharging;
        }
        catch
        {
        }

        RefreshWmiIfStale();

        lock (Gate)
        {
            return new BatteryPowerInfo(
                _designedCapacityMwh ?? designed,
                fullCharge,
                remaining,
                _cycleCount,
                _voltageMv,
                chargeRate ?? _wmiChargeRateMw,
                _wmiDischargeRateMw,
                charging,
                discharging);
        }
    }

    private static void RefreshWmiIfStale()
    {
        lock (Gate)
        {
            if (_wmiInFlight || Environment.TickCount64 - _wmiAt < WmiCacheMs) return;
            _wmiInFlight = true;
        }

        Task.Run(() =>
        {
            int? designed = null;
            int? cycles = null;
            int? voltage = null;
            int? chargeRate = null;
            int? dischargeRate = null;
            try
            {
                // 设计容量 / 循环次数 / 电压：只有经典 WMI 拿得到（WinRT 没有这三个）
                designed = QueryNumber("BatteryStaticData", "DesignedCapacity");
                cycles = QueryNumber("BatteryCycleCount", "CycleCount");

                using var searcher = new ManagementObjectSearcher(
                    @"root\WMI", "SELECT Voltage, ChargeRate, DischargeRate FROM BatteryStatus");
                foreach (ManagementBaseObject item in searcher.Get())
                {
                    using (item)
                    {
                        voltage = ToNumber(item["Voltage"]);
                        chargeRate = ToNumber(item["ChargeRate"]);
                        dischargeRate = ToNumber(item["DischargeRate"]);
                    }
                }
            }
            catch
            {
                // 台式机 / 精简系统上没有这些类：保持 null，界面显示 "—"
            }

            lock (Gate)
            {
                if (designed != null) _designedCapacityMwh = designed;
                if (cycles != null) _cycleCount = cycles;
                if (voltage != null) _voltageMv = voltage;
                if (chargeRate != null) _wmiChargeRateMw = chargeRate;
                if (dischargeRate != null) _wmiDischargeRateMw = dischargeRate;
                _wmiAt = Environment.TickCount64;
                _wmiInFlight = false;
            }
        });
    }

    private static int? QueryNumber(string className, string property)
    {
        using var searcher = new ManagementObjectSearcher(@"root\WMI", $"SELECT {property} FROM {className}");
        foreach (ManagementBaseObject item in searcher.Get())
        {
            using (item)
            {
                if (ToNumber(item[property]) is { } value) return value;
            }
        }

        return null;
    }

    private static int? ToNumber(object? raw) => raw switch
    {
        uint value when value is > 0 and not uint.MaxValue => (int)Math.Min(value, int.MaxValue),
        int value when value > 0 => value,
        ushort value => value,
        _ => null,
    };
}
