namespace VNotch.Models;

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
