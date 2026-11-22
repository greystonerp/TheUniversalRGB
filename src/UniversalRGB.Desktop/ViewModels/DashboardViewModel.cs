using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalRGB.Core.Compatibility;
using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Lighting;
using UniversalRGB.Desktop.Services;
using UniversalRGB.Desktop.Views;

namespace UniversalRGB.Desktop.ViewModels;

public sealed record QuickEffect(string Name, LightingEffect Effect, Brush Swatch);

public sealed partial class DashboardViewModel : ObservableObject
{
    private const int StripLeds = 64;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _frameTimer;
    private LightingFrame? _latest;

    [ObservableProperty] private int _attentionCount;

    public DashboardViewModel(ControlCenter center, MainViewModel main)
    {
        Center = center;
        _main = main;
        for (var i = 0; i < StripLeds; i++)
        {
            Strip.Add(new LedSwatch());
        }

        QuickEffects =
        [
            Quick("Cyan", LightingEffect.StaticColor(LightingEffect.DefaultAccent)),
            Quick("White", LightingEffect.StaticColor(new RgbColor(255, 244, 230))),
            Quick("Rainbow", new LightingEffect { Kind = EffectKind.RainbowWave, Speed = 45 }),
            Quick("Spectrum", new LightingEffect { Kind = EffectKind.SpectrumCycle, Speed = 35 }),
            Quick("Breathing", new LightingEffect { Kind = EffectKind.Breathing, Speed = 35, Colors = [LightingEffect.DefaultAccent] }),
            Quick("Off", LightingEffect.Off),
        ];

        center.DevicesChanged += (_, _) => RebuildPreviews();
        center.PropertyChanged += OnCenterChanged;
        center.Frame += (_, frame) => Interlocked.Exchange(ref _latest, frame);

        _frameTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _frameTimer.Tick += (_, _) => PumpFrame();
        _frameTimer.Start();
        RebuildAttention();
    }

    public ControlCenter Center { get; }

    public IReadOnlyList<QuickEffect> QuickEffects { get; }

    public ObservableCollection<DevicePreviewViewModel> Previews { get; } = [];

    public ObservableCollection<DeviceEntry> Attention { get; } = [];

    /// <summary>Effect preview strip rendered from the running profile (works even with no devices).</summary>
    public ObservableCollection<LedSwatch> Strip { get; } = [];

    [RelayCommand]
    private void ApplyQuick(QuickEffect quick) => Center.ApplyQuickEffect(quick.Effect);

    [RelayCommand]
    private void SaveAsProfile()
    {
        if (Center.LiveProfile is not { } live)
        {
            return;
        }

        var name = DialogWindow.Prompt("Save as profile", "Name for the new profile:", LightingEffect.DisplayName(live.GlobalEffect.Kind));
        if (name is not null)
        {
            Center.SaveAsNew(live, name);
        }
    }

    [RelayCommand]
    private void Revert() => Center.RevertToActive();

    [RelayCommand]
    private Task Rescan() => Center.RescanAsync();

    [RelayCommand]
    private void OpenDevices() => _main.NavigateCommand.Execute("devices");

    [RelayCommand]
    private void OpenDrivers() => _main.NavigateCommand.Execute("drivers");

    [RelayCommand]
    private void OpenStudio() => _main.NavigateCommand.Execute("lighting");

    private static QuickEffect Quick(string name, LightingEffect effect) => new(name, effect, Shell.SwatchFor(effect));

    private void OnCenterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ControlCenter.Entries))
        {
            RebuildAttention();
        }
    }

    private void RebuildPreviews()
    {
        Previews.Clear();
        foreach (var device in Center.Devices)
        {
            Previews.Add(new DevicePreviewViewModel(device));
        }
    }

    private void RebuildAttention()
    {
        Attention.Clear();
        var items = Center.Entries
            .Where(e => e.Status is DeviceStatus.DriverRequired or DeviceStatus.SetupRequired or DeviceStatus.ConflictDetected
                || (e.Controller is not null && e.NextStep is not null))
            .OrderBy(e => e.Status == DeviceStatus.ConflictDetected ? 0 : e.Status == DeviceStatus.DriverRequired ? 1 : 2)
            .ToList();
        AttentionCount = items.Count;
        foreach (var entry in items.Take(5))
        {
            Attention.Add(entry);
        }
    }

    private void PumpFrame()
    {
        var engine = Center.Services.Engine;
        var t = engine.CurrentTimeSeconds;
        if (Center.LiveProfile is { } live && !Center.LightingPaused)
        {
            var signals = new RenderSignals(Center.Services.Temperature.CurrentTemperatureC, Center.Services.Audio.Level);
            var colors = EffectRenderer.Render(live.GlobalEffect, t, StripLeds, signals);
            EffectRenderer.ApplyBrightness(colors, live.MasterBrightness / 100.0);
            for (var i = 0; i < StripLeds; i++)
            {
                Strip[i].Set(colors[i]);
            }
        }

        var frame = Interlocked.Exchange(ref _latest, null);
        if (frame is null)
        {
            return;
        }

        foreach (var preview in Previews)
        {
            if (frame.Colors.TryGetValue(preview.Id, out var colors))
            {
                preview.Update(colors);
            }
        }
    }
}
