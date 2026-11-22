using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalRGB.Core.Compatibility;
using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Lighting;
using UniversalRGB.Core.Profiles;
using UniversalRGB.Desktop.Converters;
using UniversalRGB.Desktop.Services;
using UniversalRGB.Desktop.Views;

namespace UniversalRGB.Desktop.ViewModels;

public sealed partial class ColorSlot : ObservableObject
{
    [ObservableProperty] private Color _color;
    [ObservableProperty] private bool _isSelected;

    public ColorSlot(Color color)
    {
        _color = color;
        Brush = new SolidColorBrush(color);
    }

    public SolidColorBrush Brush { get; }

    partial void OnColorChanged(Color value) => Brush.Color = value;
}

public sealed record TargetOption(string? DeviceId, string Name, string Detail);

public sealed partial class DeviceToggle : ObservableObject
{
    private readonly Action<DeviceToggle> _changed;

    [ObservableProperty] private bool _enabled;

    public DeviceToggle(string id, string name, string detail, bool enabled, Action<DeviceToggle> changed)
    {
        Id = id;
        Name = name;
        Detail = detail;
        _enabled = enabled;
        _changed = changed;
    }

    public string Id { get; }
    public string Name { get; }
    public string Detail { get; }

    partial void OnEnabledChanged(bool value) => _changed(this);
}

public sealed partial class LightingViewModel : ObservableObject
{
    private const int PreviewCount = 48;
    private static readonly RgbColor[] NewColorCycle =
    [
        new(34, 211, 238), new(59, 130, 246), new(244, 63, 94), new(250, 204, 21), new(74, 222, 128), new(251, 146, 60),
    ];

    private readonly ControlCenter _center;
    private readonly DispatcherTimer _applyTimer;
    private readonly DispatcherTimer _previewTimer;
    private LightingProfile _draft = new();
    private bool _loading;

    [ObservableProperty] private bool _syncAll = true;
    [ObservableProperty] private TargetOption? _selectedTarget;
    [ObservableProperty] private EffectOption _selectedEffect = EffectOption.For(EffectKind.Static);
    [ObservableProperty] private ColorSlot? _selectedSlot;
    [ObservableProperty] private Color _pickerColor = LightingEffect.DefaultAccent.ToWpf();
    [ObservableProperty] private double _speed = 50;
    [ObservableProperty] private double _brightness = 100;
    [ObservableProperty] private bool _reverse;
    [ObservableProperty] private double _temperatureLow = 35;
    [ObservableProperty] private double _temperatureHigh = 80;
    [ObservableProperty] private double _audioSensitivity = 50;
    [ObservableProperty] private bool _livePreview = true;
    [ObservableProperty] private bool _hasChanges;
    [ObservableProperty] private string _sourceName = "";
    [ObservableProperty] private string _signalText = "";

    public LightingViewModel(ControlCenter center)
    {
        _center = center;
        for (var i = 0; i < PreviewCount; i++)
        {
            PreviewLeds.Add(new LedSwatch());
        }

        _applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _applyTimer.Tick += (_, _) =>
        {
            _applyTimer.Stop();
            PushLive();
        };
        _previewTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _previewTimer.Tick += (_, _) => RenderPreview();
        _previewTimer.Start();

        center.DevicesChanged += (_, _) => RebuildDevices();
        center.PropertyChanged += OnCenterChanged;
        if (center.ActiveProfile is { } active)
        {
            LoadFrom(active);
        }
    }

    public IReadOnlyList<EffectOption> EffectOptions => EffectOption.All;

    public ObservableCollection<TargetOption> Targets { get; } = [];

    public ObservableCollection<ColorSlot> Colors { get; } = [];

    public ObservableCollection<DeviceToggle> DeviceToggles { get; } = [];

    public ObservableCollection<LedSwatch> PreviewLeds { get; } = [];

    public bool ShowColors => SelectedEffect.MaxColors > 0;
    public bool ShowSpeed => SelectedEffect.UsesSpeed;
    public bool ShowDirection => SelectedEffect.UsesDirection;
    public bool ShowTemperature => SelectedEffect.Kind == EffectKind.Temperature;
    public bool ShowAudio => SelectedEffect.Kind == EffectKind.MusicReactive;
    public bool CanAddColor => Colors.Count < SelectedEffect.MaxColors;
    public bool CanRemoveColor => Colors.Count > Math.Max(1, SelectedEffect.MinColors);
    public bool HasDevices => DeviceToggles.Count > 0;
    public string ColorHint => SelectedEffect.MaxColors == 1 ? "Color" : $"Colors ({Colors.Count}/{SelectedEffect.MaxColors})";
    public string SpeedText => SelectedEffect.Kind == EffectKind.Gradient && Speed < 1 ? "Still" : $"{Speed:0}";

    /// <summary>Loads a profile into the editor (discarding unsaved edits).</summary>
    public void LoadFrom(LightingProfile profile)
    {
        _loading = true;
        try
        {
            _draft = profile.Clone();
            SourceName = profile.Name;
            SyncAll = _draft.SyncAll;
            RebuildTargets();
            SelectedTarget = Targets.FirstOrDefault();
            LoadEffect(_draft.GlobalEffect);
            RebuildDevices();
            HasChanges = false;
        }
        finally
        {
            _loading = false;
        }
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void SelectEffect(EffectOption option) => SelectedEffect = option;

    [RelayCommand]
    private void SelectSlot(ColorSlot slot) => SelectedSlot = slot;

    [RelayCommand]
    private void AddColor()
    {
        if (!CanAddColor)
        {
            return;
        }

        var slot = new ColorSlot(NewColorCycle[Colors.Count % NewColorCycle.Length].ToWpf());
        Colors.Add(slot);
        SelectedSlot = slot;
        ColorsChanged();
        Commit();
    }

    [RelayCommand]
    private void RemoveColor()
    {
        if (!CanRemoveColor || SelectedSlot is null)
        {
            return;
        }

        var index = Colors.IndexOf(SelectedSlot);
        Colors.Remove(SelectedSlot);
        SelectedSlot = Colors.Count > 0 ? Colors[Math.Clamp(index, 0, Colors.Count - 1)] : null;
        ColorsChanged();
        Commit();
    }

    [RelayCommand]
    private void Save()
    {
        var target = _center.ActiveProfile;
        if (target is null)
        {
            SaveAsNew();
            return;
        }

        var updated = _draft.Clone();
        updated.Id = target.Id;
        updated.Name = target.Name;
        updated.Description = target.Description;
        updated.Schedule = target.Schedule;
        _center.SaveProfile(updated);
        _center.ActivateProfile(updated, announce: false);
        _center.Notify($"Saved {updated.Name}.", ToastKind.Success);
        LoadFrom(updated);
    }

    [RelayCommand]
    private void SaveAsNew()
    {
        var name = DialogWindow.Prompt("Save as new profile", "Name for the new profile:", $"{SelectedEffect.Name} profile");
        if (name is null)
        {
            return;
        }

        var created = _center.SaveAsNew(_draft, name);
        LoadFrom(created);
    }

    [RelayCommand]
    private void Revert()
    {
        if (_center.ActiveProfile is { } active)
        {
            LoadFrom(active);
            _center.RevertToActive();
        }
    }

    [RelayCommand]
    private void ApplyNow() => PushLive(force: true);

    // ------------------------------------------------------------------ change tracking

    partial void OnSyncAllChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        _draft.SyncAll = value;
        RebuildTargets();
        SelectedTarget = Targets.FirstOrDefault();
        MarkChanged();
    }

    partial void OnSelectedTargetChanged(TargetOption? value)
    {
        if (value is null)
        {
            return;
        }

        var wasLoading = _loading;
        _loading = true;
        try
        {
            LoadEffect(EffectForTarget(value));
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    partial void OnSelectedEffectChanged(EffectOption value)
    {
        OnPropertyChanged(nameof(ShowColors));
        OnPropertyChanged(nameof(ShowSpeed));
        OnPropertyChanged(nameof(ShowDirection));
        OnPropertyChanged(nameof(ShowTemperature));
        OnPropertyChanged(nameof(ShowAudio));
        OnPropertyChanged(nameof(SpeedText));
        if (_loading)
        {
            ColorsChanged();
            return;
        }

        // Keep the user's colors where possible, but respect the new effect's limits.
        var current = Colors.Select(c => c.Color.ToRgb()).ToList();
        var defaults = LightingEffect.DefaultColorsFor(value.Kind);
        var wanted = current.Count == 0 ? defaults.ToList() : current;
        if (wanted.Count < value.MinColors)
        {
            wanted.AddRange(defaults.Skip(wanted.Count).Take(value.MinColors - wanted.Count));
            while (wanted.Count < value.MinColors)
            {
                wanted.Add(NewColorCycle[wanted.Count % NewColorCycle.Length]);
            }
        }

        SetColors(value.MaxColors == 0 ? current : wanted.Take(value.MaxColors).ToList());
        Commit();
    }

    partial void OnSelectedSlotChanged(ColorSlot? oldValue, ColorSlot? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
            var wasLoading = _loading;
            _loading = true;
            PickerColor = newValue.Color;
            _loading = wasLoading;
        }
    }

    partial void OnPickerColorChanged(Color value)
    {
        if (_loading || SelectedSlot is null)
        {
            return;
        }

        SelectedSlot.Color = value;
        Commit();
    }

    partial void OnSpeedChanged(double value)
    {
        OnPropertyChanged(nameof(SpeedText));
        Commit();
    }

    partial void OnBrightnessChanged(double value) => Commit();

    partial void OnReverseChanged(bool value) => Commit();

    partial void OnTemperatureLowChanged(double value) => Commit();

    partial void OnTemperatureHighChanged(double value) => Commit();

    partial void OnAudioSensitivityChanged(double value) => Commit();

    partial void OnLivePreviewChanged(bool value)
    {
        if (value)
        {
            PushLive(force: true);
        }
        else
        {
            _center.RevertToActive();
        }
    }

    // ------------------------------------------------------------------ internals

    private LightingEffect BuildEffect() => new()
    {
        Kind = SelectedEffect.Kind,
        Colors = SelectedEffect.MaxColors == 0
            ? LightingEffect.DefaultColorsFor(SelectedEffect.Kind)
            : Colors.Select(c => c.Color.ToRgb()).ToArray(),
        Speed = Speed,
        Brightness = Brightness,
        Direction = Reverse ? EffectDirection.Reverse : EffectDirection.Forward,
        TemperatureLowC = TemperatureLow,
        TemperatureHighC = Math.Max(TemperatureHigh, TemperatureLow + 1),
        AudioSensitivity = AudioSensitivity,
    };

    private void LoadEffect(LightingEffect effect)
    {
        SelectedEffect = EffectOption.For(effect.Kind);
        Speed = effect.Speed;
        Brightness = effect.Brightness;
        Reverse = effect.Direction == EffectDirection.Reverse;
        TemperatureLow = effect.TemperatureLowC;
        TemperatureHigh = effect.TemperatureHighC;
        AudioSensitivity = effect.AudioSensitivity;
        var option = SelectedEffect;
        var colors = option.MaxColors == 0 ? new List<RgbColor>() : effect.Colors.Take(option.MaxColors).ToList();
        if (option.MaxColors > 0 && colors.Count < Math.Max(1, option.MinColors))
        {
            colors = LightingEffect.DefaultColorsFor(effect.Kind).Take(option.MaxColors).ToList();
        }

        SetColors(colors);
    }

    private void SetColors(IReadOnlyList<RgbColor> colors)
    {
        var wasLoading = _loading;
        _loading = true;
        Colors.Clear();
        foreach (var color in colors)
        {
            Colors.Add(new ColorSlot(color.ToWpf()));
        }

        SelectedSlot = Colors.FirstOrDefault();
        _loading = wasLoading;
        ColorsChanged();
    }

    private void ColorsChanged()
    {
        OnPropertyChanged(nameof(CanAddColor));
        OnPropertyChanged(nameof(CanRemoveColor));
        OnPropertyChanged(nameof(ColorHint));
    }

    private LightingEffect EffectForTarget(TargetOption target) =>
        target.DeviceId is { } id && _draft.Devices.TryGetValue(id, out var setting) && setting.Effect is not null
            ? setting.Effect
            : _draft.GlobalEffect;

    private void Commit()
    {
        if (_loading)
        {
            return;
        }

        var effect = BuildEffect();
        if (SyncAll || SelectedTarget?.DeviceId is null)
        {
            _draft.GlobalEffect = effect;
        }
        else
        {
            var id = SelectedTarget.DeviceId;
            if (!_draft.Devices.TryGetValue(id, out var setting))
            {
                setting = new DeviceLightingSetting();
                _draft.Devices[id] = setting;
            }

            setting.Effect = effect;
        }

        MarkChanged();
    }

    private void MarkChanged()
    {
        HasChanges = true;
        if (LivePreview)
        {
            _applyTimer.Stop();
            _applyTimer.Start();
        }
    }

    private void PushLive(bool force = false)
    {
        if (LivePreview && (HasChanges || force))
        {
            _center.RunTransient(_draft.Clone());
        }
    }

    private void RebuildTargets()
    {
        var previous = SelectedTarget?.DeviceId;
        Targets.Clear();
        Targets.Add(new TargetOption(null, SyncAll ? "All devices (synced)" : "Default for all devices", "Global effect"));
        if (!SyncAll)
        {
            foreach (var device in _center.Devices)
            {
                var custom = _draft.Devices.TryGetValue(device.Id, out var s) && s.Effect is not null;
                Targets.Add(new TargetOption(device.Id, device.Name, custom ? "Custom effect" : "Uses default"));
            }
        }

        if (previous is not null && !_loading)
        {
            SelectedTarget = Targets.FirstOrDefault(t => t.DeviceId == previous) ?? Targets.FirstOrDefault();
        }
    }

    private void RebuildDevices()
    {
        DeviceToggles.Clear();
        foreach (var device in _center.Devices)
        {
            var caps = DeviceCapabilities.From(device);
            var detail = $"{DeviceClassifier.TypeName(device.Type)} · {device.LedCount} LEDs" +
                         (caps.Strategy == ControlStrategy.Streaming ? "" : " · no Direct mode");
            DeviceToggles.Add(new DeviceToggle(device.Id, device.Name, detail, _draft.IsDeviceEnabled(device.Id), OnDeviceToggled));
        }

        OnPropertyChanged(nameof(HasDevices));
        RebuildTargets();
    }

    private void OnDeviceToggled(DeviceToggle toggle)
    {
        if (!_draft.Devices.TryGetValue(toggle.Id, out var setting))
        {
            setting = new DeviceLightingSetting();
            _draft.Devices[toggle.Id] = setting;
        }

        setting.Enabled = toggle.Enabled;
        MarkChanged();
    }

    private void OnCenterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ControlCenter.ActiveProfile) && !HasChanges && _center.ActiveProfile is { } active)
        {
            LoadFrom(active);
        }
    }

    private void RenderPreview()
    {
        var effect = BuildEffect();
        var temperature = _center.Services.Temperature.CurrentTemperatureC;
        var signals = new RenderSignals(temperature, _center.Services.Audio.Level);
        var colors = EffectRenderer.Render(effect, _center.Services.Engine.CurrentTimeSeconds, PreviewCount, signals);
        for (var i = 0; i < PreviewCount; i++)
        {
            PreviewLeds[i].Set(colors[i]);
        }

        SignalText = effect.Kind switch
        {
            EffectKind.Temperature => temperature is { } t
                ? string.Create(CultureInfo.CurrentCulture, $"GPU {t:0} °C ({_center.Services.Temperature.SourceName})")
                : _center.Services.Temperature.IsAvailable ? "Waiting for a GPU temperature reading… (turn on Live on devices)" : "No trusted temperature source found (nvidia-smi).",
            EffectKind.MusicReactive => _center.Services.Audio.IsAvailable
                ? string.Create(CultureInfo.CurrentCulture, $"Audio level {_center.Services.Audio.Level * 100:0}%  ·  listens to playback only, never the microphone")
                : "Audio loopback capture is unavailable on this PC.",
            _ => "",
        };
    }
}
