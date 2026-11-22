using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using UniversalRGB.Core.Compatibility;
using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Lighting;
using UniversalRGB.Desktop.Converters;

namespace UniversalRGB.Desktop.ViewModels;

/// <summary>One LED in a preview strip. The brush is mutated in place so no binding refresh is needed.</summary>
public sealed class LedSwatch
{
    public SolidColorBrush Brush { get; } = new(Colors.Black);

    public void Set(RgbColor color)
    {
        var wpf = color.ToWpf();
        if (Brush.Color != wpf)
        {
            Brush.Color = wpf;
        }
    }
}

/// <summary>Live preview of one controller, fed by engine frames.</summary>
public sealed class DevicePreviewViewModel
{
    public const int MaxLeds = 96;

    public DevicePreviewViewModel(RgbDevice device)
    {
        Id = device.Id;
        Name = device.Name;
        var caps = DeviceCapabilities.From(device);
        Subtitle = $"{DeviceClassifier.TypeName(device.Type)} · {device.Zones.Count} zone(s) · {device.LedCount} LEDs";
        Mode = caps.Strategy switch
        {
            ControlStrategy.Streaming => "Direct",
            ControlStrategy.StaticOnly => "Static + built-in effects",
            ControlStrategy.HardwareOnly => "Built-in effects",
            _ => "Not controllable",
        };
        var count = Math.Min(device.LedCount, MaxLeds);
        for (var i = 0; i < count; i++)
        {
            Leds.Add(new LedSwatch());
        }

        Truncated = device.LedCount > MaxLeds ? $"+{device.LedCount - MaxLeds} more" : null;
    }

    public string Id { get; }
    public string Name { get; }
    public string Subtitle { get; }
    public string Mode { get; }
    public string? Truncated { get; }
    public ObservableCollection<LedSwatch> Leds { get; } = [];

    public void Update(RgbColor[] colors)
    {
        var n = Math.Min(colors.Length, Leds.Count);
        for (var i = 0; i < n; i++)
        {
            Leds[i].Set(colors[i]);
        }
    }
}

public sealed record EffectOption(
    EffectKind Kind,
    string Name,
    string Description,
    string Glyph,
    int MinColors,
    int MaxColors,
    bool UsesSpeed,
    bool UsesDirection)
{
    public Brush Swatch => Shell.SwatchFor(new LightingEffect { Kind = Kind, Colors = LightingEffect.DefaultColorsFor(Kind) });

    public static IReadOnlyList<EffectOption> All { get; } =
    [
        new(EffectKind.Static, "Static", "One solid color", "", 1, 1, false, false),
        new(EffectKind.Breathing, "Breathing", "Smooth brightness pulse", "", 1, 4, true, false),
        new(EffectKind.RainbowWave, "Rainbow Wave", "Spectrum flowing across LEDs", "", 0, 0, true, true),
        new(EffectKind.SpectrumCycle, "Spectrum Cycle", "Whole device fades through hues", "", 0, 0, true, true),
        new(EffectKind.ColorCycle, "Color Cycle", "Crossfade through chosen colors", "", 2, 6, true, false),
        new(EffectKind.Gradient, "Gradient", "Blend across LEDs (speed 0 = still)", "", 2, 6, true, true),
        new(EffectKind.Comet, "Comet", "A running light with a tail", "", 1, 2, true, true),
        new(EffectKind.Temperature, "Temperature", "Color follows GPU temperature", "", 2, 5, false, false),
        new(EffectKind.MusicReactive, "Music Reactive", "Responds to what the PC is playing", "", 1, 4, false, true),
        new(EffectKind.Off, "Off", "Turn lighting off", "", 0, 0, false, false),
    ];

    public static EffectOption For(EffectKind kind) => All.First(o => o.Kind == kind);
}

internal static class Shell
{
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Views.DialogWindow.Alert("Could not open", ex.Message, error: true);
        }
    }

    public static void OpenDeviceManager() => Open("devmgmt.msc");

    public static Brush SwatchFor(LightingEffect effect)
    {
        if (effect.Kind is EffectKind.RainbowWave or EffectKind.SpectrumCycle)
        {
            return (Brush)System.Windows.Application.Current.Resources["RgbGradient"];
        }

        if (effect.Kind == EffectKind.Off)
        {
            return new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x29));
        }

        var colors = effect.Colors.Count > 0 ? effect.Colors : LightingEffect.DefaultColorsFor(effect.Kind);
        if (colors.Count == 1)
        {
            return new SolidColorBrush(colors[0].ToWpf());
        }

        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        for (var i = 0; i < colors.Count; i++)
        {
            brush.GradientStops.Add(new GradientStop(colors[i].ToWpf(), (double)i / (colors.Count - 1)));
        }

        brush.Freeze();
        return brush;
    }
}
