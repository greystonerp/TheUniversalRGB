using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniversalRGB.Core.Devices;

/// <summary>A 24-bit RGB color. Serialized to JSON as a "#RRGGBB" string.</summary>
[JsonConverter(typeof(RgbColorJsonConverter))]
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static readonly RgbColor Black = new(0, 0, 0);
    public static readonly RgbColor White = new(255, 255, 255);

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public override string ToString() => ToHex();

    public static bool TryParseHex(string? text, out RgbColor color)
    {
        color = Black;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim().TrimStart('#');
        if (s.Length == 3)
        {
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        }

        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
        {
            return false;
        }

        color = new RgbColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static RgbColor FromHex(string text) =>
        TryParseHex(text, out var c) ? c : throw new FormatException($"'{text}' is not a valid #RRGGBB color.");

    /// <summary>OpenRGB packs colors as 0x00BBGGRR (bytes R, G, B, 0 on the wire).</summary>
    public uint ToOpenRgb() => (uint)(R | (G << 8) | (B << 16));

    public static RgbColor FromOpenRgb(uint value) => new((byte)value, (byte)(value >> 8), (byte)(value >> 16));

    public RgbColor Scale(double factor)
    {
        factor = Math.Clamp(factor, 0, 1);
        return new RgbColor(
            (byte)Math.Round(R * factor),
            (byte)Math.Round(G * factor),
            (byte)Math.Round(B * factor));
    }

    public static RgbColor Lerp(RgbColor a, RgbColor b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new RgbColor(
            (byte)Math.Round(a.R + ((b.R - a.R) * t)),
            (byte)Math.Round(a.G + ((b.G - a.G) * t)),
            (byte)Math.Round(a.B + ((b.B - a.B) * t)));
    }

    /// <param name="hue">0..1 (wraps)</param>
    /// <param name="saturation">0..1</param>
    /// <param name="value">0..1</param>
    public static RgbColor FromHsv(double hue, double saturation, double value)
    {
        hue -= Math.Floor(hue);
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        var h6 = hue * 6.0;
        var sector = (int)Math.Floor(h6) % 6;
        var f = h6 - Math.Floor(h6);
        var p = value * (1 - saturation);
        var q = value * (1 - (f * saturation));
        var t = value * (1 - ((1 - f) * saturation));

        var (r, g, b) = sector switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q),
        };

        return new RgbColor((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    /// <returns>Hue 0..1, saturation 0..1, value 0..1.</returns>
    public (double Hue, double Saturation, double Value) ToHsv()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue = 0;
        if (delta > 0)
        {
            if (max == r)
            {
                hue = ((g - b) / delta) % 6;
            }
            else if (max == g)
            {
                hue = ((b - r) / delta) + 2;
            }
            else
            {
                hue = ((r - g) / delta) + 4;
            }

            hue /= 6;
            if (hue < 0)
            {
                hue += 1;
            }
        }

        var saturation = max == 0 ? 0 : delta / max;
        return (hue, saturation, max);
    }
}

public sealed class RgbColorJsonConverter : JsonConverter<RgbColor>
{
    public override RgbColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return RgbColor.TryParseHex(text, out var color)
            ? color
            : throw new JsonException($"Invalid color '{text}'. Expected #RRGGBB.");
    }

    public override void Write(Utf8JsonWriter writer, RgbColor value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToHex());
}
