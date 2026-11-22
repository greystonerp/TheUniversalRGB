using UniversalRGB.Core.Devices;

namespace UniversalRGB.Adapters.OpenRGB;

public sealed record OpenRgbLed(string Name, uint Value);

/// <summary>Controller description as sent by the OpenRGB SDK server.</summary>
public sealed record OpenRgbControllerData
{
    public RgbDeviceType Type { get; init; } = RgbDeviceType.Unknown;
    public string Name { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string Description { get; init; } = "";
    public string Version { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Location { get; init; } = "";
    public int ActiveMode { get; init; }
    public IReadOnlyList<RgbMode> Modes { get; init; } = [];
    public IReadOnlyList<RgbZone> Zones { get; init; } = [];
    public IReadOnlyList<OpenRgbLed> Leds { get; init; } = [];
    public IReadOnlyList<RgbColor> Colors { get; init; } = [];
}

/// <summary>
/// Encodes/decodes OpenRGB SDK payloads for protocol versions 0..5 (layout from RGBController.cpp).
/// </summary>
public static class OpenRgbSerializer
{
    // ------------------------------------------------------------------ controller data

    public static OpenRgbControllerData ParseController(ReadOnlySpan<byte> payload, uint protocol)
    {
        var r = new PacketReader(payload);
        var dataSize = r.ReadUInt32();
        if (dataSize > payload.Length)
        {
            throw new InvalidDataException($"Controller data size {dataSize} exceeds packet size {payload.Length}.");
        }

        var rawType = r.ReadInt32();
        var type = Enum.IsDefined(typeof(RgbDeviceType), rawType) ? (RgbDeviceType)rawType : RgbDeviceType.Unknown;
        var name = r.ReadString();
        var vendor = protocol >= 1 ? r.ReadString() : "";
        var description = r.ReadString();
        var version = r.ReadString();
        var serial = r.ReadString();
        var location = r.ReadString();

        var modeCount = r.ReadUInt16();
        var activeMode = r.ReadInt32();
        var modes = new List<RgbMode>(modeCount);
        for (var i = 0; i < modeCount; i++)
        {
            modes.Add(ReadMode(ref r, i, protocol));
        }

        var zoneCount = r.ReadUInt16();
        var zones = new List<RgbZone>(zoneCount);
        var start = 0;
        for (var i = 0; i < zoneCount; i++)
        {
            var zone = ReadZone(ref r, i, start, protocol);
            zones.Add(zone);
            start += zone.LedCount;
        }

        var ledCount = r.ReadUInt16();
        var leds = new List<OpenRgbLed>(ledCount);
        for (var i = 0; i < ledCount; i++)
        {
            var ledName = r.ReadString();
            var value = r.ReadUInt32();
            leds.Add(new OpenRgbLed(ledName, value));
        }

        var colorCount = r.ReadUInt16();
        var colors = new RgbColor[colorCount];
        for (var i = 0; i < colorCount; i++)
        {
            colors[i] = RgbColor.FromOpenRgb(r.ReadUInt32());
        }

        // Protocol 5+ appends LED display names and controller flags; not needed here.
        return new OpenRgbControllerData
        {
            Type = type,
            Name = name,
            Vendor = vendor,
            Description = description,
            Version = version,
            Serial = serial,
            Location = location,
            ActiveMode = activeMode,
            Modes = modes,
            Zones = zones,
            Leds = leds,
            Colors = colors,
        };
    }

    /// <summary>Encodes a controller exactly like the server does (used by tests and the protocol simulator).</summary>
    public static byte[] SerializeController(OpenRgbControllerData c, uint protocol)
    {
        var w = new PacketWriter();
        w.Int32((int)c.Type).String(c.Name);
        if (protocol >= 1)
        {
            w.String(c.Vendor);
        }

        w.String(c.Description).String(c.Version).String(c.Serial).String(c.Location);
        w.UInt16((ushort)c.Modes.Count).Int32(c.ActiveMode);
        foreach (var mode in c.Modes)
        {
            WriteMode(w, mode, protocol);
        }

        w.UInt16((ushort)c.Zones.Count);
        foreach (var zone in c.Zones)
        {
            WriteZone(w, zone, protocol);
        }

        w.UInt16((ushort)c.Leds.Count);
        foreach (var led in c.Leds)
        {
            w.String(led.Name).UInt32(led.Value);
        }

        w.UInt16((ushort)c.Colors.Count);
        foreach (var color in c.Colors)
        {
            w.UInt32(color.ToOpenRgb());
        }

        var body = w.ToArray();
        var result = new PacketWriter().UInt32((uint)(body.Length + 4)).Bytes(body);
        return result.ToArray();
    }

    // ------------------------------------------------------------------ modes

    public static RgbMode ReadMode(ref PacketReader r, int index, uint protocol)
    {
        var name = r.ReadString();
        var value = r.ReadInt32();
        var flags = (ModeFlags)r.ReadUInt32();
        var speedMin = r.ReadUInt32();
        var speedMax = r.ReadUInt32();
        uint brightnessMin = 0, brightnessMax = 0;
        if (protocol >= 3)
        {
            brightnessMin = r.ReadUInt32();
            brightnessMax = r.ReadUInt32();
        }

        var colorsMin = r.ReadUInt32();
        var colorsMax = r.ReadUInt32();
        var speed = r.ReadUInt32();
        uint brightness = 0;
        if (protocol >= 3)
        {
            brightness = r.ReadUInt32();
        }

        var direction = r.ReadUInt32();
        var colorMode = r.ReadUInt32();
        var colorCount = r.ReadUInt16();
        var colors = new RgbColor[colorCount];
        for (var i = 0; i < colorCount; i++)
        {
            colors[i] = RgbColor.FromOpenRgb(r.ReadUInt32());
        }

        return new RgbMode
        {
            Index = index,
            Name = name,
            Value = value,
            Flags = flags,
            SpeedMin = speedMin,
            SpeedMax = speedMax,
            BrightnessMin = brightnessMin,
            BrightnessMax = brightnessMax,
            ColorsMin = colorsMin,
            ColorsMax = colorsMax,
            Speed = speed,
            Brightness = brightness,
            Direction = direction <= (uint)ModeDirection.Vertical ? (ModeDirection)direction : ModeDirection.Left,
            ColorMode = colorMode <= (uint)ModeColorMode.Random ? (ModeColorMode)colorMode : ModeColorMode.None,
            Colors = colors,
        };
    }

    public static void WriteMode(PacketWriter w, RgbMode mode, uint protocol)
    {
        w.String(mode.Name).Int32(mode.Value).UInt32((uint)mode.Flags).UInt32(mode.SpeedMin).UInt32(mode.SpeedMax);
        if (protocol >= 3)
        {
            w.UInt32(mode.BrightnessMin).UInt32(mode.BrightnessMax);
        }

        w.UInt32(mode.ColorsMin).UInt32(mode.ColorsMax).UInt32(mode.Speed);
        if (protocol >= 3)
        {
            w.UInt32(mode.Brightness);
        }

        w.UInt32((uint)mode.Direction).UInt32((uint)mode.ColorMode).UInt16((ushort)mode.Colors.Count);
        foreach (var color in mode.Colors)
        {
            w.UInt32(color.ToOpenRgb());
        }
    }

    // ------------------------------------------------------------------ zones

    private static RgbZone ReadZone(ref PacketReader r, int index, int startIndex, uint protocol)
    {
        var name = r.ReadString();
        var rawType = r.ReadInt32();
        var ledsMin = r.ReadUInt32();
        var ledsMax = r.ReadUInt32();
        var ledsCount = r.ReadUInt32();

        RgbMatrix? matrix = null;
        var matrixSize = r.ReadUInt16();
        if (matrixSize > 0)
        {
            if (matrixSize < 8)
            {
                throw new InvalidDataException("Invalid zone matrix size.");
            }

            var height = (int)r.ReadUInt32();
            var width = (int)r.ReadUInt32();
            var cells = (matrixSize - 8) / 4;
            var map = new uint[cells];
            for (var i = 0; i < cells; i++)
            {
                map[i] = r.ReadUInt32();
            }

            r.Skip((matrixSize - 8) % 4);
            matrix = new RgbMatrix(width, height, map);
        }

        if (protocol >= 4)
        {
            var segmentCount = r.ReadUInt16();
            for (var i = 0; i < segmentCount; i++)
            {
                r.ReadString(); // name
                r.ReadInt32();  // type
                r.ReadUInt32(); // start index
                r.ReadUInt32(); // led count
            }
        }

        if (protocol >= 5)
        {
            r.ReadUInt32(); // zone flags
        }

        return new RgbZone
        {
            Id = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Index = index,
            Name = name,
            Type = rawType is >= 0 and <= 2 ? (RgbZoneType)rawType : RgbZoneType.Linear,
            LedsMin = (int)Math.Min(ledsMin, int.MaxValue),
            LedsMax = (int)Math.Min(ledsMax, int.MaxValue),
            LedCount = (int)Math.Min(ledsCount, ushort.MaxValue),
            StartIndex = startIndex,
            Matrix = matrix,
        };
    }

    private static void WriteZone(PacketWriter w, RgbZone zone, uint protocol)
    {
        w.String(zone.Name).Int32((int)zone.Type).UInt32((uint)zone.LedsMin).UInt32((uint)zone.LedsMax).UInt32((uint)zone.LedCount);
        if (zone.Matrix is { } m)
        {
            w.UInt16((ushort)(8 + (m.Map.Count * 4))).UInt32((uint)m.Height).UInt32((uint)m.Width);
            foreach (var cell in m.Map)
            {
                w.UInt32(cell);
            }
        }
        else
        {
            w.UInt16(0);
        }

        if (protocol >= 4)
        {
            w.UInt16(0); // no segments
        }

        if (protocol >= 5)
        {
            w.UInt32(0);
        }
    }

    // ------------------------------------------------------------------ requests

    public static byte[] UpdateLeds(IReadOnlyList<RgbColor> colors)
    {
        var size = 4 + 2 + (colors.Count * 4);
        var w = new PacketWriter().UInt32((uint)size).UInt16(checked((ushort)colors.Count));
        foreach (var color in colors)
        {
            w.UInt32(color.ToOpenRgb());
        }

        return w.ToArray();
    }

    public static byte[] UpdateZoneLeds(int zoneIndex, IReadOnlyList<RgbColor> colors)
    {
        var size = 4 + 4 + 2 + (colors.Count * 4);
        var w = new PacketWriter().UInt32((uint)size).UInt32((uint)zoneIndex).UInt16(checked((ushort)colors.Count));
        foreach (var color in colors)
        {
            w.UInt32(color.ToOpenRgb());
        }

        return w.ToArray();
    }

    public static byte[] UpdateMode(int modeIndex, RgbMode mode, uint protocol)
    {
        var body = new PacketWriter();
        body.Int32(modeIndex);
        WriteMode(body, mode, protocol);
        var bytes = body.ToArray();
        return new PacketWriter().UInt32((uint)(bytes.Length + 4)).Bytes(bytes).ToArray();
    }

    public static byte[] ResizeZone(int zoneIndex, int newSize) =>
        new PacketWriter().Int32(zoneIndex).Int32(newSize).ToArray();
}
