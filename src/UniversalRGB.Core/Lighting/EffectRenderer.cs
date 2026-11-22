using UniversalRGB.Core.Devices;

namespace UniversalRGB.Core.Lighting;

/// <summary>
/// Pure, allocation-free effect math. Renders one zone (a run of LEDs) for a point in time.
/// </summary>
public static class EffectRenderer
{
    public static void Render(LightingEffect effect, double timeSeconds, Span<RgbColor> output, in RenderSignals signals)
    {
        var count = output.Length;
        if (count == 0)
        {
            return;
        }

        var colors = effect.Colors.Count > 0 ? effect.Colors : LightingEffect.DefaultColorsFor(effect.Kind);
        var dir = effect.Direction == EffectDirection.Reverse ? -1.0 : 1.0;

        switch (effect.Kind)
        {
            case EffectKind.Off:
                output.Fill(RgbColor.Black);
                return;

            case EffectKind.Static:
                output.Fill(colors[0]);
                break;

            case EffectKind.Breathing:
            {
                var hz = Rate(effect.Speed, 0.08, 1.0);
                var phase = timeSeconds * hz;
                var index = (int)(Math.Floor(phase) % colors.Count);
                var level = (1 - Math.Cos(2 * Math.PI * Frac(phase))) / 2;
                output.Fill(colors[Math.Abs(index)].Scale(0.04 + (0.96 * level * level)));
                break;
            }

            case EffectKind.RainbowWave:
            {
                var rate = Rate(effect.Speed, 0.03, 1.2);
                for (var i = 0; i < count; i++)
                {
                    var p = (double)i / count;
                    output[i] = RgbColor.FromHsv(p - (dir * timeSeconds * rate), 1, 1);
                }

                break;
            }

            case EffectKind.SpectrumCycle:
            {
                var rate = Rate(effect.Speed, 0.01, 0.5);
                output.Fill(RgbColor.FromHsv(dir * timeSeconds * rate, 1, 1));
                break;
            }

            case EffectKind.ColorCycle:
            {
                var rate = Rate(effect.Speed, 0.05, 1.5);
                var phase = timeSeconds * rate;
                var index = (int)(Math.Floor(phase) % colors.Count);
                var next = (index + 1) % colors.Count;
                var t = SmoothStep(Frac(phase));
                output.Fill(RgbColor.Lerp(colors[index], colors[next], t));
                break;
            }

            case EffectKind.Gradient:
            {
                var shift = effect.Speed <= 0 ? 0 : dir * timeSeconds * Rate(effect.Speed, 0.01, 0.4);
                for (var i = 0; i < count; i++)
                {
                    var p = count == 1 ? 0 : (double)i / count;
                    output[i] = SampleLoop(colors, p - shift);
                }

                break;
            }

            case EffectKind.Comet:
            {
                var rate = Rate(effect.Speed, 0.15, 2.0);
                var head = Frac(dir * timeSeconds * rate);
                var tail = Math.Max(0.12, 3.0 / count);
                var background = colors.Count > 1 ? colors[1] : RgbColor.Black;
                for (var i = 0; i < count; i++)
                {
                    var p = (double)i / count;
                    var behind = Frac(dir > 0 ? head - p : p - head);
                    var intensity = Math.Exp(-behind / tail * 3);
                    output[i] = RgbColor.Lerp(background, colors[0], intensity);
                }

                break;
            }

            case EffectKind.Temperature:
            {
                var low = Math.Min(effect.TemperatureLowC, effect.TemperatureHighC - 1);
                var high = Math.Max(effect.TemperatureHighC, low + 1);
                if (signals.TemperatureC is { } temp)
                {
                    var t = Math.Clamp((temp - low) / (high - low), 0, 1);
                    output.Fill(Sample(colors, t));
                }
                else
                {
                    // No trusted reading: show the "cool" color dimly so the device is not left dark.
                    output.Fill(colors[0].Scale(0.35));
                }

                break;
            }

            case EffectKind.MusicReactive:
            {
                var gain = Rate(effect.AudioSensitivity, 0.5, 6.0);
                var level = Math.Clamp(signals.AudioLevel * gain, 0, 1);
                if (count == 1)
                {
                    output[0] = Sample(colors, level).Scale(0.08 + (0.92 * level));
                    break;
                }

                for (var i = 0; i < count; i++)
                {
                    var p = (i + 0.5) / count;
                    var lit = dir > 0 ? p <= level : (1 - p) <= level;
                    output[i] = lit ? Sample(colors, p) : Sample(colors, p).Scale(0.06);
                }

                break;
            }

            default:
                output.Fill(colors[0]);
                break;
        }

        ApplyBrightness(output, effect.Brightness / 100.0);
    }

    public static RgbColor[] Render(LightingEffect effect, double timeSeconds, int ledCount, in RenderSignals signals)
    {
        var result = new RgbColor[Math.Max(0, ledCount)];
        Render(effect, timeSeconds, result, signals);
        return result;
    }

    public static void ApplyBrightness(Span<RgbColor> colors, double factor)
    {
        if (factor >= 0.999)
        {
            return;
        }

        for (var i = 0; i < colors.Length; i++)
        {
            colors[i] = colors[i].Scale(factor);
        }
    }

    /// <summary>Maps the 1..100 speed slider onto a rate range.</summary>
    internal static double Rate(double speed, double min, double max) =>
        min + ((max - min) * Math.Clamp(speed, 0, 100) / 100.0);

    /// <summary>Samples a multi-stop gradient at t in [0, 1].</summary>
    public static RgbColor Sample(IReadOnlyList<RgbColor> stops, double t)
    {
        if (stops.Count == 1)
        {
            return stops[0];
        }

        t = Math.Clamp(t, 0, 1) * (stops.Count - 1);
        var index = Math.Min((int)Math.Floor(t), stops.Count - 2);
        return RgbColor.Lerp(stops[index], stops[index + 1], t - index);
    }

    /// <summary>Samples a looping gradient (last stop blends back into the first).</summary>
    private static RgbColor SampleLoop(IReadOnlyList<RgbColor> stops, double t)
    {
        if (stops.Count == 1)
        {
            return stops[0];
        }

        t = Frac(t) * stops.Count;
        var index = (int)Math.Floor(t) % stops.Count;
        return RgbColor.Lerp(stops[index], stops[(index + 1) % stops.Count], t - Math.Floor(t));
    }

    private static double Frac(double v) => v - Math.Floor(v);

    private static double SmoothStep(double t) => t * t * (3 - (2 * t));
}
