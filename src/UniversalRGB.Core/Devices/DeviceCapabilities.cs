namespace UniversalRGB.Core.Devices;

/// <summary>How the lighting engine may drive a device.</summary>
public enum ControlStrategy
{
    /// <summary>Device has a "Direct" mode: per-LED colors can be streamed every frame.</summary>
    Streaming,

    /// <summary>
    /// Per-LED colors are supported but not via Direct mode. Writes may go to device flash,
    /// so colors are only sent when they change — never streamed.
    /// </summary>
    StaticOnly,

    /// <summary>Only built-in hardware effects are available.</summary>
    HardwareOnly,

    /// <summary>Nothing controllable was exposed.</summary>
    None,
}

public sealed record DeviceCapabilities
{
    public bool SupportsDirectControl { get; init; }
    public bool SupportsPerLedColor { get; init; }
    public bool SupportsBrightness { get; init; }
    public bool SupportsSpeed { get; init; }
    public IReadOnlyList<string> HardwareEffects { get; init; } = [];
    public int ZoneCount { get; init; }
    public int LedCount { get; init; }
    public bool HasResizableZones { get; init; }
    public bool HasUnconfiguredZones { get; init; }
    public ControlStrategy Strategy { get; init; }

    public static DeviceCapabilities From(RgbDevice device)
    {
        var direct = device.Modes.Any(m => m.IsDirect);
        var perLed = device.Modes.Any(m => m.Has(ModeFlags.HasPerLedColor));
        var hardware = device.Modes
            .Where(m => !m.IsDirect)
            .Select(m => m.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var strategy = direct ? ControlStrategy.Streaming
            : perLed ? ControlStrategy.StaticOnly
            : hardware.Length > 0 ? ControlStrategy.HardwareOnly
            : ControlStrategy.None;

        if (device.LedCount == 0 && strategy is ControlStrategy.Streaming or ControlStrategy.StaticOnly)
        {
            strategy = hardware.Length > 0 ? ControlStrategy.HardwareOnly : ControlStrategy.None;
        }

        return new DeviceCapabilities
        {
            SupportsDirectControl = direct,
            SupportsPerLedColor = perLed,
            SupportsBrightness = device.Modes.Any(m => m.Has(ModeFlags.HasBrightness)),
            SupportsSpeed = device.Modes.Any(m => m.Has(ModeFlags.HasSpeed)),
            HardwareEffects = hardware,
            ZoneCount = device.Zones.Count,
            LedCount = device.LedCount,
            HasResizableZones = device.Zones.Any(z => z.IsResizable),
            HasUnconfiguredZones = device.Zones.Any(z => z.IsResizable && z.LedCount == 0),
            Strategy = strategy,
        };
    }
}
