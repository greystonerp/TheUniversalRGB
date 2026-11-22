using System.Text.Json.Serialization;
using UniversalRGB.Core.Devices;

namespace UniversalRGB.Core.Lighting;

[JsonConverter(typeof(JsonStringEnumConverter<EffectKind>))]
public enum EffectKind
{
    Off,
    Static,
    Breathing,
    RainbowWave,
    SpectrumCycle,
    ColorCycle,
    Gradient,
    Comet,
    Temperature,
    MusicReactive,
}

[JsonConverter(typeof(JsonStringEnumConverter<EffectDirection>))]
public enum EffectDirection
{
    Forward,
    Reverse,
}

/// <summary>An effect description. Immutable; use <c>with</c> expressions to change it.</summary>
public sealed record LightingEffect
{
    public static readonly RgbColor DefaultAccent = new(34, 211, 238);

    public EffectKind Kind { get; init; } = EffectKind.Static;

    public IReadOnlyList<RgbColor> Colors { get; init; } = [DefaultAccent];

    /// <summary>1 (slow) .. 100 (fast).</summary>
    public double Speed { get; init; } = 50;

    /// <summary>0 .. 100 percent.</summary>
    public double Brightness { get; init; } = 100;

    public EffectDirection Direction { get; init; } = EffectDirection.Forward;

    /// <summary>Temperature effect: color at or below this temperature is the first gradient stop.</summary>
    public double TemperatureLowC { get; init; } = 35;

    /// <summary>Temperature effect: color at or above this temperature is the last gradient stop.</summary>
    public double TemperatureHighC { get; init; } = 80;

    /// <summary>Music effect: input gain, 1 .. 100.</summary>
    public double AudioSensitivity { get; init; } = 50;

    [JsonIgnore]
    public bool IsAnimated => Kind is not (EffectKind.Static or EffectKind.Off or EffectKind.Gradient) || (Kind == EffectKind.Gradient && Speed > 0);

    [JsonIgnore]
    public RgbColor PrimaryColor => Colors.Count > 0 ? Colors[0] : DefaultAccent;

    public static LightingEffect Off { get; } = new() { Kind = EffectKind.Off, Colors = [RgbColor.Black] };

    public static LightingEffect StaticColor(RgbColor color, double brightness = 100) =>
        new() { Kind = EffectKind.Static, Colors = [color], Brightness = brightness };

    public static IReadOnlyList<RgbColor> DefaultColorsFor(EffectKind kind) => kind switch
    {
        EffectKind.ColorCycle => [new(239, 68, 68), new(59, 130, 246), new(34, 197, 94), new(234, 179, 8)],
        EffectKind.Gradient => [new(34, 211, 238), new(59, 130, 246), new(244, 63, 94)],
        EffectKind.Temperature => [new(59, 130, 246), new(34, 211, 238), new(74, 222, 128), new(250, 204, 21), new(239, 68, 68)],
        EffectKind.MusicReactive => [new(34, 211, 238), new(59, 130, 246), new(244, 63, 94)],
        EffectKind.Off => [RgbColor.Black],
        _ => [DefaultAccent],
    };

    public static string DisplayName(EffectKind kind) => kind switch
    {
        EffectKind.Off => "Off",
        EffectKind.Static => "Static Color",
        EffectKind.Breathing => "Breathing",
        EffectKind.RainbowWave => "Rainbow Wave",
        EffectKind.SpectrumCycle => "Spectrum Cycle",
        EffectKind.ColorCycle => "Color Cycle",
        EffectKind.Gradient => "Gradient",
        EffectKind.Comet => "Comet",
        EffectKind.Temperature => "Temperature",
        EffectKind.MusicReactive => "Music Reactive",
        _ => kind.ToString(),
    };

    /// <summary>Value equality including the color list (records compare lists by reference).</summary>
    public bool ContentEquals(LightingEffect? other) =>
        other is not null
        && Kind == other.Kind
        && Speed.Equals(other.Speed)
        && Brightness.Equals(other.Brightness)
        && Direction == other.Direction
        && TemperatureLowC.Equals(other.TemperatureLowC)
        && TemperatureHighC.Equals(other.TemperatureHighC)
        && AudioSensitivity.Equals(other.AudioSensitivity)
        && Colors.SequenceEqual(other.Colors);
}
