namespace UniversalRGB.Core.Devices;

/// <summary>Device categories. Numeric values match the OpenRGB SDK device_type enum.</summary>
public enum RgbDeviceType
{
    Motherboard = 0,
    Dram = 1,
    Gpu = 2,
    Cooler = 3,
    LedStrip = 4,
    Keyboard = 5,
    Mouse = 6,
    Mousemat = 7,
    Headset = 8,
    HeadsetStand = 9,
    Gamepad = 10,
    Light = 11,
    Speaker = 12,
    Virtual = 13,
    Storage = 14,
    Case = 15,
    Microphone = 16,
    Accessory = 17,
    Keypad = 18,
    Laptop = 19,
    Monitor = 20,
    Unknown = 21,
}

/// <summary>Zone layout. Values match OpenRGB zone_type (protocol &lt; 6).</summary>
public enum RgbZoneType
{
    Single = 0,
    Linear = 1,
    Matrix = 2,
}

[Flags]
public enum ModeFlags : uint
{
    None = 0,
    HasSpeed = 1 << 0,
    HasDirectionLR = 1 << 1,
    HasDirectionUD = 1 << 2,
    HasDirectionHV = 1 << 3,
    HasBrightness = 1 << 4,
    HasPerLedColor = 1 << 5,
    HasModeSpecificColor = 1 << 6,
    HasRandomColor = 1 << 7,
    ManualSave = 1 << 8,
    AutomaticSave = 1 << 9,
    RequiresEntireDevice = 1 << 10,
}

public enum ModeColorMode : uint
{
    None = 0,
    PerLed = 1,
    ModeSpecific = 2,
    Random = 3,
}

public enum ModeDirection : uint
{
    Left = 0,
    Right = 1,
    Up = 2,
    Down = 3,
    Horizontal = 4,
    Vertical = 5,
}

public sealed record RgbMatrix(int Width, int Height, IReadOnlyList<uint> Map)
{
    public const uint NoLed = 0xFFFFFFFF;
}

public sealed record RgbZone
{
    /// <summary>Zone identifier used by adapters (zone index as text for OpenRGB).</summary>
    public required string Id { get; init; }
    public required int Index { get; init; }
    public required string Name { get; init; }
    public RgbZoneType Type { get; init; }
    public int LedCount { get; init; }
    public int LedsMin { get; init; }
    public int LedsMax { get; init; }

    /// <summary>Index of the zone's first LED in the device-wide LED array.</summary>
    public int StartIndex { get; init; }
    public RgbMatrix? Matrix { get; init; }

    /// <summary>Addressable headers/channels whose LED count the user must configure.</summary>
    public bool IsResizable => LedsMax > LedsMin;

    public const string AllZones = "*";
}

public sealed record RgbMode
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public int Value { get; init; }
    public ModeFlags Flags { get; init; }
    public uint SpeedMin { get; init; }
    public uint SpeedMax { get; init; }
    public uint BrightnessMin { get; init; }
    public uint BrightnessMax { get; init; }
    public uint ColorsMin { get; init; }
    public uint ColorsMax { get; init; }
    public uint Speed { get; init; }
    public uint Brightness { get; init; }
    public ModeDirection Direction { get; init; }
    public ModeColorMode ColorMode { get; init; }
    public IReadOnlyList<RgbColor> Colors { get; init; } = [];

    public bool Has(ModeFlags flag) => (Flags & flag) == flag;

    public bool IsDirect => Name.Equals("Direct", StringComparison.OrdinalIgnoreCase) && Has(ModeFlags.HasPerLedColor);
}

/// <summary>A lighting device exposed by an RGB backend (a real, controllable controller).</summary>
public sealed record RgbDevice
{
    /// <summary>Stable identifier, unique across adapters and sessions (e.g. "openrgb:...").</summary>
    public required string Id { get; init; }
    public required string AdapterName { get; init; }
    public required string Name { get; init; }
    public string Vendor { get; init; } = "";
    public string Description { get; init; } = "";
    public string Version { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Location { get; init; } = "";
    public RgbDeviceType Type { get; init; } = RgbDeviceType.Unknown;
    public IReadOnlyList<RgbZone> Zones { get; init; } = [];
    public IReadOnlyList<RgbMode> Modes { get; init; } = [];
    public int ActiveModeIndex { get; init; }
    public int LedCount { get; init; }
    public IReadOnlyList<RgbColor> Colors { get; init; } = [];

    /// <summary>USB/PCI identity parsed from the backend location string, when available.</summary>
    public HardwareIdentity? Identity { get; init; }

    public RgbMode? ActiveMode => ActiveModeIndex >= 0 && ActiveModeIndex < Modes.Count ? Modes[ActiveModeIndex] : null;

    public RgbZone? FindZone(string zoneId) => Zones.FirstOrDefault(z => z.Id == zoneId);
}
