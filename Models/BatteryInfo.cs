namespace VNotch.Models;

public class BatteryInfo
{
    public int Percentage { get; set; } = 100;
    public bool IsCharging { get; set; } = false;
    public bool IsPluggedIn { get; set; } = false;
    public bool HasBattery { get; set; } = true;
    public int RemainingMinutes { get; set; } = -1;

    /// <summary>
    /// Instantaneous power flow in watts.
    /// Positive while charging (energy going IN), negative while discharging (going OUT).
    /// 0 when unknown or idle. See <see cref="HasPowerRate"/> to distinguish unknown vs. truly idle.
    /// </summary>
    public double PowerWatts { get; set; } = 0.0;

    /// <summary>True when the OS reports a usable charge/discharge rate.</summary>
    public bool HasPowerRate { get; set; } = false;

    /// <summary>True when plugged in and at (or essentially at) 100%.</summary>
    public bool IsFullyCharged => IsPluggedIn && Percentage >= 99;

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
