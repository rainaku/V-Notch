using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VNotch.Services;

public static class BatteryService
{
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

    public static BatteryInfo GetBatteryInfo()
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

        return info;
    }
}

public class BatteryInfo
{
    public int Percentage { get; set; } = 100;
    public bool IsCharging { get; set; } = false;
    public bool IsPluggedIn { get; set; } = false;
    public bool HasBattery { get; set; } = true;
    public int RemainingMinutes { get; set; } = -1;

    public string GetBatteryIcon()
    {
        if (IsCharging) return "⚡";
        if (Percentage >= 80) return "🔋";
        if (Percentage >= 50) return "🔋";
        if (Percentage >= 20) return "🪫";
        return "🪫";
    }

    public string GetPercentageText()
    {
        return $"{Percentage}%";
    }
}