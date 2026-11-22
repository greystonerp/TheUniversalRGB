using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using UniversalRGB.Core.Storage;
using UniversalRGB.Desktop.Services;

namespace UniversalRGB.Desktop.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(ControlCenter center, UpdateManager updates)
    {
        Center = center;
        Updates = updates;
        _startWithWindows = StartupRegistration.IsEnabled();
    }

    public ControlCenter Center { get; }

    public UpdateManager Updates { get; }

    public bool AutoUpdate
    {
        get => S.AutoUpdate;
        set
        {
            S.AutoUpdate = value;
            Save();
        }
    }

    [RelayCommand]
    private Task CheckForUpdates() => Updates.CheckAsync(userInitiated: true);

    [RelayCommand]
    private Task InstallUpdate() => Updates.InstallAsync();

    [RelayCommand]
    private void OpenReleases() => Shell.Open(Updates.ReleasesPage);

    private AppSettings S => Center.Services.Settings;

    public string Version => "Universal RGB Control " + (typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "");

    public string InstallState => SelfInstaller.IsRunningInstalled
        ? $"Installed for {Environment.UserName} in {SelfInstaller.InstallDirectory}"
        : "Running without installing (portable or developer build).";

    public bool CanUninstall => SelfInstaller.IsRunningInstalled;

    public string DataFolder => Center.Services.Paths.Roaming;

    public string LocalFolder => Center.Services.Paths.Local;

    public string Host
    {
        get => S.OpenRgbHost;
        set
        {
            S.OpenRgbHost = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim();
            Save();
        }
    }

    public string Port
    {
        get => S.OpenRgbPort.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536)
            {
                S.OpenRgbPort = port;
                Save();
            }
            else
            {
                Center.Notify("Port must be a number between 1 and 65535.", ToastKind.Warning);
            }

            OnPropertyChanged();
        }
    }

    public string OpenRgbPath => S.OpenRgbExecutablePath ?? "Automatic";

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set
        {
            S.AutoConnect = value;
            Save();
        }
    }

    public bool AutoInstallRequirements
    {
        get => S.AutoInstallRequirements;
        set
        {
            S.AutoInstallRequirements = value;
            Save();
        }
    }

    public bool LaunchOpenRgbElevated
    {
        get => S.LaunchOpenRgbElevated;
        set
        {
            S.LaunchOpenRgbElevated = value;
            Save();
        }
    }

    private bool _startWithWindows;

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            try
            {
                StartupRegistration.Set(value);
                _startWithWindows = value;
                S.StartWithWindows = value;
                Save();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Center.Notify("Could not change the startup entry: " + ex.Message, ToastKind.Error);
            }

            OnPropertyChanged();
        }
    }

    public bool StartMinimizedAtLogin
    {
        get => S.StartMinimizedAtLogin;
        set
        {
            S.StartMinimizedAtLogin = value;
            Save();
        }
    }

    public bool MinimizeToTray
    {
        get => S.MinimizeToTray;
        set
        {
            S.MinimizeToTray = value;
            Save();
        }
    }

    public bool RestoreProfileOnStartup
    {
        get => S.RestoreProfileOnStartup;
        set
        {
            S.RestoreProfileOnStartup = value;
            Save();
        }
    }

    public bool RescanOnDeviceChange
    {
        get => S.RescanOnDeviceChange;
        set
        {
            S.RescanOnDeviceChange = value;
            Save();
        }
    }

    public bool EnableProfileSchedules
    {
        get => S.EnableProfileSchedules;
        set
        {
            S.EnableProfileSchedules = value;
            Save();
        }
    }

    public double FrameRate
    {
        get => S.FrameRate;
        set
        {
            var rounded = (int)Math.Round(value / 5) * 5;
            if (rounded == S.FrameRate)
            {
                return;
            }

            S.FrameRate = Math.Clamp(rounded, 10, 60);
            Center.Services.Engine.FrameRate = S.FrameRate;
            Save();
            OnPropertyChanged(nameof(FrameRateText));
        }
    }

    public string FrameRateText => $"{S.FrameRate} fps";

    [RelayCommand]
    private async Task Reconnect()
    {
        await Center.Services.OpenRgb.DisconnectAsync();
        await Center.ConnectAsync(userInitiated: true);
    }

    [RelayCommand]
    private void LocateOpenRgb()
    {
        var dialog = new OpenFileDialog { Title = "Locate OpenRGB.exe", Filter = "OpenRGB|OpenRGB.exe|Programs (*.exe)|*.exe" };
        if (dialog.ShowDialog() == true)
        {
            S.OpenRgbExecutablePath = dialog.FileName;
            Save();
            OnPropertyChanged(nameof(OpenRgbPath));
        }
    }

    [RelayCommand]
    private void ClearOpenRgbPath()
    {
        S.OpenRgbExecutablePath = null;
        Save();
        OnPropertyChanged(nameof(OpenRgbPath));
    }

    [RelayCommand]
    private void OpenLogs() => Shell.Open(Center.Services.Paths.Logs);

    [RelayCommand]
    private void OpenDataFolder() => Shell.Open(Center.Services.Paths.Roaming);

    [RelayCommand]
    private static void OpenProjectSite() => Shell.Open("https://openrgb.org/");

    /// <summary>Runs the installed uninstaller; it asks for confirmation and then closes this copy itself.</summary>
    [RelayCommand]
    private static void Uninstall() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SelfInstaller.InstalledExe, SelfInstaller.UninstallArgument)
        {
            UseShellExecute = true,
        });

    private void Save() => Center.Services.SaveSettings();
}
