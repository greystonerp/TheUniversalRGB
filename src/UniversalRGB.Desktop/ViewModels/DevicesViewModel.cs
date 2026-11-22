using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalRGB.Core.Compatibility;
using UniversalRGB.Core.Devices;
using UniversalRGB.Desktop.Services;
using UniversalRGB.Desktop.Views;

namespace UniversalRGB.Desktop.ViewModels;

public sealed partial class DevicesViewModel : ObservableObject
{
    [ObservableProperty] private string _filter = "all";
    [ObservableProperty] private DeviceEntry? _selected;
    [ObservableProperty] private DeviceDetailViewModel? _detail;
    [ObservableProperty] private int _allCount;
    [ObservableProperty] private int _controllableCount;
    [ObservableProperty] private int _attentionCount;
    [ObservableProperty] private int _offlineCount;

    public DevicesViewModel(ControlCenter center)
    {
        Center = center;
        center.PropertyChanged += OnCenterChanged;
        Rebuild();
    }

    public ControlCenter Center { get; }

    public ObservableCollection<DeviceEntry> Items { get; } = [];

    partial void OnFilterChanged(string value) => Rebuild();

    partial void OnSelectedChanged(DeviceEntry? value) =>
        Detail = value is null ? null : new DeviceDetailViewModel(value, Center);

    [RelayCommand]
    private Task Rescan() => Center.RescanAsync();

    [RelayCommand]
    private void Identify()
    {
        if (Selected?.Controller is not { } device)
        {
            return;
        }

        if (Center.LightingPaused)
        {
            Center.Notify("Resume the lighting engine to identify devices.", ToastKind.Info);
            return;
        }

        Center.Notify(Center.Services.Engine.Identify(device.Id, TimeSpan.FromSeconds(3))
            ? $"Blinking {device.Name} for 3 seconds."
            : $"{device.Name} has no Direct mode, so it cannot blink without writing to its memory.", ToastKind.Info);
    }

    [RelayCommand]
    private void Forget()
    {
        if (Selected is { Status: DeviceStatus.Offline } entry &&
            DialogWindow.Confirm("Forget device?", $"Remove {entry.Name} from the list of previously seen devices?", "Forget"))
        {
            Center.ForgetDevice(entry.Key);
        }
    }

    [RelayCommand]
    private static void OpenDeviceManager() => Shell.OpenDeviceManager();

    private void OnCenterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ControlCenter.Entries))
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var all = Center.Entries;
        AllCount = all.Count;
        ControllableCount = all.Count(e => e.Controller is not null && e.Status != DeviceStatus.Offline);
        AttentionCount = all.Count(NeedsAttention);
        OfflineCount = all.Count(e => e.Status == DeviceStatus.Offline);

        var filtered = Filter switch
        {
            "controllable" => all.Where(e => e.Controller is not null && e.Status != DeviceStatus.Offline),
            "attention" => all.Where(NeedsAttention),
            "offline" => all.Where(e => e.Status == DeviceStatus.Offline),
            _ => all,
        };

        var selectedKey = Selected?.Key;
        Items.Clear();
        foreach (var entry in filtered)
        {
            Items.Add(entry);
        }

        Selected = Items.FirstOrDefault(i => i.Key == selectedKey) ?? Items.FirstOrDefault();
    }

    private static bool NeedsAttention(DeviceEntry e) =>
        e.Status is DeviceStatus.DriverRequired or DeviceStatus.SetupRequired or DeviceStatus.ConflictDetected || (e.Controller is not null && e.NextStep is not null);
}

public sealed class DeviceDetailViewModel
{
    public DeviceDetailViewModel(DeviceEntry entry, ControlCenter center)
    {
        Entry = entry;
        var props = new List<KeyValuePair<string, string>>
        {
            new("Category", entry.Category),
            new("Manufacturer", string.IsNullOrWhiteSpace(entry.Manufacturer) ? "—" : entry.Manufacturer),
            new("Connection", string.IsNullOrWhiteSpace(entry.Connection) ? "—" : entry.Connection),
            new("Backend", entry.Backend),
        };
        if (entry.HardwareKey is not null)
        {
            props.Add(new("VID:PID", entry.HardwareKey));
        }

        if (entry.Controller is { } device)
        {
            var caps = DeviceCapabilities.From(device);
            props.Add(new("Control", caps.Strategy switch
            {
                ControlStrategy.Streaming => "Direct mode — every effect, streamed",
                ControlStrategy.StaticOnly => "Static colors + built-in effects (no streaming, protects device memory)",
                ControlStrategy.HardwareOnly => "Built-in hardware effects only",
                _ => "None",
            }));
            props.Add(new("Zones / LEDs", $"{device.Zones.Count} / {device.LedCount}"));
            if (!string.IsNullOrWhiteSpace(device.Description))
            {
                props.Add(new("Description", device.Description));
            }

            if (!string.IsNullOrWhiteSpace(device.Version))
            {
                props.Add(new("Firmware", device.Version));
            }

            if (!string.IsNullOrWhiteSpace(device.Serial))
            {
                props.Add(new("Serial", device.Serial));
            }

            props.Add(new("Location", device.Location));
            CanIdentify = caps.Strategy == ControlStrategy.Streaming;

            foreach (var zone in device.Zones)
            {
                Zones.Add(new ZoneEditViewModel(device, zone, center));
            }

            Modes = device.Modes.Select(m => m.Index == device.ActiveModeIndex ? m.Name + "  (active)" : m.Name).ToList();
        }

        if (entry.Inventory is { } inv)
        {
            HardwareIds = inv.HardwareIds.ToList();
            if (inv.Service is not null)
            {
                props.Add(new("Windows driver", inv.Service));
            }
        }

        if (entry.Compatibility is { } record)
        {
            props.Add(new("Database", $"{record.Manufacturer} · {record.Model} ({record.Status}{(record.Verified ? ", verified" : ", not verified")})"));
            if (!string.IsNullOrWhiteSpace(record.Notes))
            {
                props.Add(new("Notes", record.Notes));
            }
        }

        if (entry.LastSeen is { } seen && entry.Status == DeviceStatus.Offline)
        {
            props.Add(new("Last seen", seen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        }

        Properties = props;
    }

    public DeviceEntry Entry { get; }
    public IReadOnlyList<KeyValuePair<string, string>> Properties { get; }
    public ObservableCollection<ZoneEditViewModel> Zones { get; } = [];
    public IReadOnlyList<string> Modes { get; } = [];
    public IReadOnlyList<string> HardwareIds { get; } = [];
    public bool CanIdentify { get; }
    public bool HasZones => Zones.Count > 0;
    public bool HasModes => Modes.Count > 0;
    public bool HasHardwareIds => HardwareIds.Count > 0;
    public bool IsOffline => Entry.Status == DeviceStatus.Offline;
}

public sealed partial class ZoneEditViewModel : ObservableObject
{
    private readonly RgbDevice _device;
    private readonly RgbZone _zone;
    private readonly ControlCenter _center;

    [ObservableProperty] private string _ledCountText;

    public ZoneEditViewModel(RgbDevice device, RgbZone zone, ControlCenter center)
    {
        _device = device;
        _zone = zone;
        _center = center;
        _ledCountText = zone.LedCount.ToString(CultureInfo.InvariantCulture);
    }

    public string Name => _zone.Name;
    public string Kind => _zone.Type switch { RgbZoneType.Matrix => "Matrix", RgbZoneType.Single => "Single", _ => "Linear" };
    public int LedCount => _zone.LedCount;
    public bool IsResizable => _zone.IsResizable;
    public bool NeedsSetup => _zone.IsResizable && _zone.LedCount == 0;
    public string Range => $"{_zone.LedsMin}–{_zone.LedsMax} LEDs";
    public string Summary => IsResizable ? $"{Kind} · {LedCount} LEDs · adjustable {Range}" : $"{Kind} · {LedCount} LEDs";

    [RelayCommand]
    private async Task Apply()
    {
        if (!int.TryParse(LedCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < _zone.LedsMin || count > _zone.LedsMax)
        {
            _center.Notify($"Enter a number between {_zone.LedsMin} and {_zone.LedsMax}.", ToastKind.Warning);
            return;
        }

        await _center.ResizeZoneAsync(_device, _zone, count);
    }
}
