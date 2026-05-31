using System;
using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services;

public class BatteryServiceImpl : IBatteryService
{
    // ─── GetSystemPowerStatus ─────────────────────────────────────────────
    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint CallNtPowerInformation(
        int InformationLevel,
        IntPtr InputBuffer,
        uint InputBufferLength,
        out SYSTEM_BATTERY_STATE OutputBuffer,
        uint OutputBufferLength);

    private const int SystemBatteryState = 5; // POWER_INFORMATION_LEVEL.SystemBatteryState
    private const uint STATUS_SUCCESS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_BATTERY_STATE
    {
        [MarshalAs(UnmanagedType.U1)] public bool AcOnLine;
        [MarshalAs(UnmanagedType.U1)] public bool BatteryPresent;
        [MarshalAs(UnmanagedType.U1)] public bool Charging;
        [MarshalAs(UnmanagedType.U1)] public bool Discharging;
        public byte Spare1_0;
        public byte Spare1_1;
        public byte Spare1_2;
        public byte Spare1_3;
        public uint MaxCapacity;
        public uint RemainingCapacity;
        public int Rate;          // mW; positive when charging, negative when discharging
        public uint EstimatedTime;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
    }

    public BatteryInfo GetBatteryInfo()
    {
        var info = new BatteryInfo();

        try
        {
            if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
            {
                info.Percentage = status.BatteryLifePercent == 255 ? 100 : status.BatteryLifePercent;
                info.IsCharging = status.ACLineStatus == 1;
                info.IsPluggedIn = status.ACLineStatus == 1;
                info.HasBattery = status.BatteryFlag != 128;

                if (status.BatteryFlag == 8)
                {
                    info.IsCharging = true;
                }

                if (status.BatteryLifeTime != -1)
                {
                    info.RemainingMinutes = status.BatteryLifeTime / 60;
                }
            }
        }
        catch
        {
            info.Percentage = 100;
            info.HasBattery = false;
        }

        // ─── Live charge/discharge rate via CallNtPowerInformation ───────
        try
        {
            uint size = (uint)Marshal.SizeOf<SYSTEM_BATTERY_STATE>();
            uint result = CallNtPowerInformation(SystemBatteryState, IntPtr.Zero, 0, out SYSTEM_BATTERY_STATE bs, size);

            if (result == STATUS_SUCCESS && bs.BatteryPresent)
            {
                int rateMilliwatts = bs.Rate;

                bool plausible = Math.Abs(rateMilliwatts) < 200_000; // < 200 W upper bound

                if (plausible)
                {
                    info.PowerWatts = rateMilliwatts / 1000.0;
                    info.HasPowerRate = true;
                }

                // Refine charging flag from the more authoritative source.
                if (bs.Charging) info.IsCharging = true;
                if (bs.AcOnLine) info.IsPluggedIn = true;
            }
        }
        catch
        {
            // Power rate is best-effort — fall back to whatever GetSystemPowerStatus gave us.
        }

        return info;
    }
}
