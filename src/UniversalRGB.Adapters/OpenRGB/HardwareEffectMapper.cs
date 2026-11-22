using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Lighting;

namespace UniversalRGB.Adapters.OpenRGB;

public sealed record HardwareModeChoice(int ModeIndex, RgbMode Mode, bool NeedsPerLedColors, RgbColor PerLedColor);

/// <summary>
/// Picks the built-in controller mode closest to a requested effect and fills in speed, brightness,
/// direction and colors within the limits the device reports. Only capabilities the mode declares are touched.
/// </summary>
public static class HardwareEffectMapper
{
    private static readonly Dictionary<EffectKind, string[]> Candidates = new()
    {
        [EffectKind.Off] = ["Off"],
        [EffectKind.Static] = ["Static", "Fixed", "Solid", "Custom"],
        [EffectKind.Breathing] = ["Breathing", "Breathe", "Pulse", "Pulsing"],
        [EffectKind.RainbowWave] = ["Rainbow Wave", "Rainbow", "Spectrum Wave", "Rainbow Flow", "Wave", "Flow"],
        [EffectKind.SpectrumCycle] = ["Spectrum Cycle", "Color Cycle", "Spectrum", "Color Shift", "Cycle"],
        [EffectKind.ColorCycle] = ["Color Cycle", "Color Shift", "Spectrum Cycle", "Cycle"],
        [EffectKind.Gradient] = ["Gradient", "Color Wave", "Rainbow Wave", "Wave"],
        [EffectKind.Comet] = ["Comet", "Chase", "Marquee", "Running", "Meteor"],
    };

    public static HardwareModeChoice? Choose(IReadOnlyList<RgbMode> modes, LightingEffect effect)
    {
        if (!Candidates.TryGetValue(effect.Kind, out var names))
        {
            return null;
        }

        var mode = FindMode(modes, names);
        if (mode is null && effect.Kind == EffectKind.Off)
        {
            // No "Off" mode: a black static color is the safe equivalent.
            mode = FindMode(modes, Candidates[EffectKind.Static]);
            effect = effect with { Colors = [RgbColor.Black] };
        }

        if (mode is null || mode.IsDirect)
        {
            return null;
        }

        var configured = mode;

        if (mode.Has(ModeFlags.HasSpeed))
        {
            configured = configured with { Speed = Interpolate(mode.SpeedMin, mode.SpeedMax, effect.Speed / 100.0) };
        }

        if (mode.Has(ModeFlags.HasBrightness))
        {
            configured = configured with { Brightness = Interpolate(mode.BrightnessMin, mode.BrightnessMax, effect.Brightness / 100.0) };
        }

        if (mode.Has(ModeFlags.HasDirectionLR))
        {
            configured = configured with
            {
                Direction = effect.Direction == EffectDirection.Reverse ? ModeDirection.Left : ModeDirection.Right,
            };
        }

        var needsPerLed = false;
        var brightnessFactor = mode.Has(ModeFlags.HasBrightness) ? 1.0 : effect.Brightness / 100.0;
        var colors = effect.Colors.Count > 0 ? effect.Colors : LightingEffect.DefaultColorsFor(effect.Kind);
        var wantsRainbow = effect.Kind is EffectKind.RainbowWave or EffectKind.SpectrumCycle;

        if (wantsRainbow && mode.Has(ModeFlags.HasRandomColor))
        {
            configured = configured with { ColorMode = ModeColorMode.Random, Colors = mode.Colors };
        }
        else if (mode.Has(ModeFlags.HasModeSpecificColor) && mode.ColorsMax > 0 && !wantsRainbow)
        {
            var minColors = Math.Max(1u, mode.ColorsMin);
            var count = (int)Math.Clamp((uint)colors.Count, minColors, Math.Max(minColors, mode.ColorsMax));
            var chosen = Enumerable.Range(0, count).Select(i => colors[i % colors.Count].Scale(brightnessFactor)).ToArray();
            configured = configured with { ColorMode = ModeColorMode.ModeSpecific, Colors = chosen };
        }
        else if (mode.Has(ModeFlags.HasPerLedColor) && !wantsRainbow)
        {
            configured = configured with { ColorMode = ModeColorMode.PerLed };
            needsPerLed = true;
        }

        return new HardwareModeChoice(mode.Index, configured, needsPerLed, colors[0].Scale(brightnessFactor));
    }

    private static RgbMode? FindMode(IReadOnlyList<RgbMode> modes, string[] names)
    {
        foreach (var name in names)
        {
            var exact = modes.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        foreach (var name in names)
        {
            var partial = modes.FirstOrDefault(m => !m.IsDirect && m.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (partial is not null)
            {
                return partial;
            }
        }

        return null;
    }

    /// <summary>Maps 0..1 onto a device range; works when the device reports min &gt; max (inverted scales).</summary>
    internal static uint Interpolate(uint min, uint max, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return (uint)Math.Round(min + ((double)max - min) * t);
    }
}
