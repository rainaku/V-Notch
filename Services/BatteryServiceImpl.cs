using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services;

public class BatteryServiceImpl : IBatteryService
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

        return info;
    }
}
