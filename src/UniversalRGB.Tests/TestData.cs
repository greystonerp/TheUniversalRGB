using UniversalRGB.Adapters.OpenRGB;
using UniversalRGB.Core.Devices;

namespace UniversalRGB.Tests;

internal static class TestData
{
    public static RgbMode Mode(int index, string name, ModeFlags flags, ModeColorMode colorMode = ModeColorMode.None,
        uint speedMin = 0, uint speedMax = 0, uint colorsMin = 0, uint colorsMax = 0, IReadOnlyList<RgbColor>? colors = null,
        uint brightnessMin = 0, uint brightnessMax = 0) => new()
    {
        Index = index,
        Name = name,
        Value = index + 10,
        Flags = flags,
        SpeedMin = speedMin,
        SpeedMax = speedMax,
        BrightnessMin = brightnessMin,
        BrightnessMax = brightnessMax,
        ColorsMin = colorsMin,
        ColorsMax = colorsMax,
        Speed = speedMin,
        Brightness = brightnessMax,
        ColorMode = colorMode,
        Colors = colors ?? [],
    };

    /// <summary>A motherboard-like controller: Direct + hardware modes, a fixed zone and a resizable ARGB header.</summary>
    public static OpenRgbControllerData Motherboard(int argbLeds = 0, bool withDirect = true)
    {
        var modes = new List<RgbMode>();
        if (withDirect)
        {
            modes.Add(Mode(modes.Count, "Direct", ModeFlags.HasPerLedColor, ModeColorMode.PerLed));
        }

        modes.Add(Mode(modes.Count, "Static", ModeFlags.HasModeSpecificColor | ModeFlags.HasPerLedColor, ModeColorMode.ModeSpecific,
            colorsMin: 1, colorsMax: 1, colors: [RgbColor.White]));
        modes.Add(Mode(modes.Count, "Breathing", ModeFlags.HasSpeed | ModeFlags.HasModeSpecificColor | ModeFlags.HasBrightness,
            ModeColorMode.ModeSpecific, speedMin: 4, speedMax: 0, colorsMin: 1, colorsMax: 2, colors: [RgbColor.White], brightnessMin: 0, brightnessMax: 255));
        modes.Add(Mode(modes.Count, "Rainbow", ModeFlags.HasSpeed | ModeFlags.HasDirectionLR | ModeFlags.HasRandomColor, ModeColorMode.None,
            speedMin: 1, speedMax: 10));
        modes.Add(Mode(modes.Count, "Off", ModeFlags.None));

        var zones = new List<RgbZone>
        {
            new() { Id = "0", Index = 0, Name = "Aura Mainboard", Type = RgbZoneType.Linear, LedsMin = 3, LedsMax = 3, LedCount = 3 },
            new() { Id = "1", Index = 1, Name = "Aura Addressable 1", Type = RgbZoneType.Linear, LedsMin = 0, LedsMax = 120, LedCount = argbLeds, StartIndex = 3 },
        };

        var ledCount = 3 + argbLeds;
        return new OpenRgbControllerData
        {
            Type = RgbDeviceType.Motherboard,
            Name = "ASUS PRIME B550M-A WIFI II",
            Vendor = "ASUS",
            Description = "ASUS Aura USB Device",
            Version = "AULA3-AR32-0207",
            Serial = "9876543210",
            Location = @"HID: \\?\hid#vid_0b05&pid_19af&col01#7&1234&0&0000",
            ActiveMode = 0,
            Modes = modes,
            Zones = zones,
            Leds = Enumerable.Range(0, ledCount).Select(i => new OpenRgbLed($"LED {i + 1}", (uint)i)).ToList(),
            Colors = Enumerable.Repeat(RgbColor.Black, ledCount).ToList(),
        };
    }

    /// <summary>A keyboard-like controller with a 2x3 matrix zone.</summary>
    public static OpenRgbControllerData Keyboard() => new()
    {
        Type = RgbDeviceType.Keyboard,
        Name = "Razer Ornata V3 X",
        Vendor = "Razer",
        Description = "Razer Keyboard",
        Version = "",
        Serial = "",
        Location = @"HID: \\?\hid#vid_1532&pid_0294&mi_00#8&abc&0&0000",
        ActiveMode = 1,
        Modes =
        [
            Mode(0, "Direct", ModeFlags.HasPerLedColor, ModeColorMode.PerLed),
            Mode(1, "Spectrum Cycle", ModeFlags.HasSpeed, speedMin: 1, speedMax: 3),
        ],
        Zones =
        [
            new() { Id = "0", Index = 0, Name = "Keyboard", Type = RgbZoneType.Matrix, LedsMin = 6, LedsMax = 6, LedCount = 6,
                Matrix = new RgbMatrix(3, 2, [0, 1, 2, 3, 4, RgbMatrix.NoLed]) },
        ],
        Leds = Enumerable.Range(0, 6).Select(i => new OpenRgbLed($"Key {i}", (uint)i)).ToList(),
        Colors = Enumerable.Repeat(RgbColor.Black, 6).ToList(),
    };

    public static RgbDevice Device(OpenRgbControllerData data, string? id = null) =>
        OpenRgbAdapter.ToDevice(id ?? OpenRgbAdapter.MakeId(data), data);
}
