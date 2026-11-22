using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalRGB.Core.Profiles;
using UniversalRGB.Desktop.Services;

namespace UniversalRGB.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _toastTimer;

    [ObservableProperty] private string _currentKey = "dashboard";
    [ObservableProperty] private object _currentPage;
    [ObservableProperty] private ToastMessage? _toast;

    public MainViewModel(ControlCenter center, UpdateManager updates)
    {
        Center = center;
        Updates = updates;
        Dashboard = new DashboardViewModel(center, this);
        Devices = new DevicesViewModel(center);
        Lighting = new LightingViewModel(center);
        Profiles = new ProfilesViewModel(center, this);
        Drivers = new DriversViewModel(center);
        Settings = new SettingsViewModel(center, updates);
        _currentPage = Dashboard;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast = null;
        };
        center.Toast += (_, message) =>
        {
            Toast = message;
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromSeconds(message.Kind is ToastKind.Error or ToastKind.Warning ? 7 : 4.5);
            _toastTimer.Start();
        };
    }

    public ControlCenter Center { get; }
    public UpdateManager Updates { get; }
    public DashboardViewModel Dashboard { get; }
    public DevicesViewModel Devices { get; }
    public LightingViewModel Lighting { get; }
    public ProfilesViewModel Profiles { get; }
    public DriversViewModel Drivers { get; }
    public SettingsViewModel Settings { get; }

    public string Version { get; } = "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    public string PageTitle => CurrentKey switch
    {
        "devices" => "Devices",
        "lighting" => "Lighting Studio",
        "profiles" => "Profiles",
        "drivers" => "Drivers & Dependencies",
        "settings" => "Settings",
        _ => "Dashboard",
    };

    public string PageSubtitle => CurrentKey switch
    {
        "devices" => "Detected hardware, what is actually controllable, and why.",
        "lighting" => "Pick an effect, tune it, and preview it live on your devices.",
        "profiles" => "Save, schedule, import and export lighting configurations.",
        "drivers" => "Diagnose missing requirements. Nothing is installed without your approval.",
        "settings" => "Backend connection, startup behaviour and engine options.",
        _ => "Lighting overview and quick controls.",
    };

    [RelayCommand]
    private void Navigate(string key)
    {
        CurrentKey = key;
        CurrentPage = key switch
        {
            "devices" => Devices,
            "lighting" => Lighting,
            "profiles" => Profiles,
            "drivers" => Drivers,
            "settings" => Settings,
            _ => Dashboard,
        };
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
    }

    public void EditProfile(LightingProfile profile)
    {
        Center.ActivateProfile(profile, announce: false);
        Lighting.LoadFrom(profile);
        Navigate("lighting");
    }

    [RelayCommand]
    private async Task Connect()
    {
        if (Center.IsConnected)
        {
            await Center.DisconnectAsync();
        }
        else
        {
            await Center.ConnectAsync(userInitiated: true);
        }
    }

    [RelayCommand]
    private Task StartServer() => Center.EnsureRequirementsAsync(userInitiated: true);

    [RelayCommand]
    private Task TogglePause() => Center.SetPausedAsync(!Center.LightingPaused);

    [RelayCommand]
    private void DismissToast()
    {
        _toastTimer.Stop();
        Toast = null;
    }
}
