using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using UniversalRGB.Core.Inventory;
using UniversalRGB.Core.Storage;
using UniversalRGB.Desktop.Services;
using UniversalRGB.Desktop.Views;
using UniversalRGB.Drivers.Detection;
using UniversalRGB.Drivers.Installation;
using UniversalRGB.Drivers.Verification;

namespace UniversalRGB.Desktop.ViewModels;

public sealed record PackageCard(DriverPackage Package, RequirementState State, string StateText)
{
    public string KindText => Package.Kind switch
    {
        "kernel-driver" => "Kernel driver",
        "service" => "Service",
        _ => "Application",
    };

    public bool CanVerify => Package.Installable;

    /// <summary>Installer packages can also be verified from a file the user downloaded themselves.</summary>
    public bool CanVerifyFile => Package.Installable && Package.Format != "zip";

    /// <summary>One-click download is offered while the requirement is not satisfied.</summary>
    public bool CanDownload => Package.CanDownload && State != RequirementState.Ok;

    public string InstallText => Package.AutoInstall ? $"Install {Package.DisplayName}" : $"Install {Package.DisplayName}…";

    public string SourceText => Package.Version is null ? Package.Publisher : $"{Package.Publisher} · version {Package.Version} · SHA-256 pinned";
}

public sealed record ProblemDevice(string Name, string Problem, string HardwareId);

public sealed partial class DriversViewModel : ObservableObject
{
    [ObservableProperty] private string _openRgbPath = "Not found";
    [ObservableProperty] private string _serverText = "";
    [ObservableProperty] private bool _isChecking;

    public DriversViewModel(ControlCenter center)
    {
        Center = center;
        center.PropertyChanged += OnCenterChanged;
        center.InstallHistoryChanged += (_, _) => RefreshHistory();
        Rebuild();
        RefreshHistory();
    }

    /// <summary>True when OpenRGB or its SDK server still needs setting up.</summary>
    public bool HasMissing => Requirements.Any(r => r.State == RequirementState.Missing);

    public ControlCenter Center { get; }

    public ObservableCollection<RequirementStatus> Requirements { get; } = [];

    public ObservableCollection<PackageCard> Packages { get; } = [];

    public ObservableCollection<ProblemDevice> ProblemDevices { get; } = [];

    public ObservableCollection<InstallRecord> History { get; } = [];

    public bool HasProblems => ProblemDevices.Count > 0;

    public bool HasHistory => History.Count > 0;

    [RelayCommand]
    private async Task Recheck()
    {
        IsChecking = true;
        try
        {
            await Center.RefreshHardwareAsync();
            Center.Notify("Requirements re-checked.", ToastKind.Info);
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private Task StartServer() => Center.EnsureRequirementsAsync(userInitiated: true);

    /// <summary>Installs OpenRGB (verified download) if needed, starts its server and connects.</summary>
    [RelayCommand]
    private async Task InstallMissing()
    {
        await Center.EnsureRequirementsAsync(userInitiated: true);
        RefreshHistory();
    }

    /// <summary>Restarts the background OpenRGB server elevated (one UAC prompt) so it can use PawnIO / SMBus.</summary>
    [RelayCommand]
    private Task RunOpenRgbAsAdministrator() => Center.RestartOpenRgbAsync(asAdministrator: true);

    /// <summary>Restarts the background OpenRGB server, e.g. after closing other RGB software.</summary>
    [RelayCommand]
    private Task RestartOpenRgb() => Center.RestartOpenRgbAsync(Center.Services.Settings.LaunchOpenRgbElevated);

    [RelayCommand]
    private async Task InstallPackage(PackageCard card)
    {
        await Center.InstallPackageAsync(card.Package);
        RefreshHistory();
    }

    [RelayCommand]
    private async Task InstallPackageById(string id)
    {
        if (Center.Services.Drivers.Find(id) is { } package)
        {
            await Center.InstallPackageAsync(package);
            RefreshHistory();
        }
    }

    [RelayCommand]
    private async Task LocateOpenRgb()
    {
        var dialog = new OpenFileDialog { Title = "Locate OpenRGB.exe", Filter = "OpenRGB|OpenRGB.exe|Programs (*.exe)|*.exe" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        Center.Services.Settings.OpenRgbExecutablePath = dialog.FileName;
        Center.Services.SaveSettings();
        await Center.RefreshDependenciesAsync();
        Center.Notify("OpenRGB location saved.", ToastKind.Success);
    }

    [RelayCommand]
    private static void OpenPage(PackageCard card) => Shell.Open(card.Package.OfficialPage);

    [RelayCommand]
    private void OpenPackagePage(string id)
    {
        if (Center.Services.Drivers.Find(id) is { } package)
        {
            Shell.Open(package.OfficialPage);
        }
    }

    [RelayCommand]
    private static void OpenDeviceManager() => Shell.OpenDeviceManager();

    /// <summary>Verify a downloaded installer, show the consent dialog, and only then run it.</summary>
    [RelayCommand]
    private async Task VerifyAndInstall(PackageCard? card)
    {
        var dialog = new OpenFileDialog
        {
            Title = card is null ? "Choose an installer to verify" : $"Choose the {card.Package.Name} installer you downloaded",
            Filter = "Installers (*.exe;*.msi)|*.exe;*.msi",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var package = card?.Package ?? Center.Services.Drivers.GuessFromFileName(dialog.FileName);
        PackageVerification verification;
        IsChecking = true;
        try
        {
            verification = await PackageVerifier.VerifyAsync(dialog.FileName, package);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Center.Notify("Could not read the file: " + ex.Message, ToastKind.Error);
            return;
        }
        finally
        {
            IsChecking = false;
        }

        var approved = DialogWindow.ConsentToInstall(verification);
        var outcome = await Center.Services.Installer.InstallAsync(verification, approved, CancellationToken.None);
        RefreshHistory();

        var kind = outcome.Result switch
        {
            InstallResult.Succeeded => ToastKind.Success,
            InstallResult.RestartRequired => ToastKind.Warning,
            InstallResult.Cancelled => ToastKind.Info,
            _ => ToastKind.Error,
        };
        Center.Notify(outcome.Message, kind);

        if (outcome.Result is InstallResult.Succeeded or InstallResult.RestartRequired)
        {
            await Center.RescanAsync();
        }
    }

    private void OnCenterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ControlCenter.Dependencies) or nameof(ControlCenter.Inventory))
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var report = Center.Dependencies;
        Requirements.Clear();
        foreach (var item in report?.Items ?? [])
        {
            Requirements.Add(item);
        }

        OpenRgbPath = report?.OpenRgbPath ?? "Not found";
        ServerText = report is null ? "" : report.ServerReachable ? "SDK server reachable" : report.OpenRgbRunning ? "Running, SDK server off" : "Not running";

        Packages.Clear();
        foreach (var package in Center.Services.Drivers.Packages)
        {
            var requirement = report?.Items.FirstOrDefault(i => i.Id == package.RequirementId);
            var state = requirement?.State ?? RequirementState.Info;
            var text = state switch
            {
                RequirementState.Ok => "Installed",
                RequirementState.Missing => "Not installed",
                RequirementState.Warning => "Not installed (optional)",
                _ => "Unknown",
            };
            Packages.Add(new PackageCard(package, state, text));
        }

        ProblemDevices.Clear();
        foreach (var device in report?.ProblemDevices ?? [])
        {
            ProblemDevices.Add(new ProblemDevice(device.Name, ProblemCodes.Describe(device.ProblemCode), device.Identity?.ToString() ?? device.InstanceId));
        }

        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(HasMissing));
    }

    private void RefreshHistory()
    {
        History.Clear();
        foreach (var record in Center.Services.Database.GetInstallHistory(50))
        {
            History.Add(record);
        }

        OnPropertyChanged(nameof(HasHistory));
    }
}
