using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UniversalRGB.Core.Diagnostics;
using UniversalRGB.Drivers.Installation;
using UniversalRGB.Drivers.Updates;

namespace UniversalRGB.Desktop.Services;

/// <summary>
/// Keeps the installed app up to date from the project's GitHub Releases. Updates are only installed after their
/// ECDSA signature verifies against the release key built into this app.
/// </summary>
public sealed partial class UpdateManager : ObservableObject, IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly ControlCenter _center;
    private readonly UpdateClient _client;
    private readonly IAppLog _log;
    private readonly DispatcherTimer _timer;
    private bool _working;

    [ObservableProperty] private string _status = "Not checked yet.";
    [ObservableProperty] private ReleaseInfo? _available;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private DateTimeOffset? _lastChecked;

    public UpdateManager(ControlCenter center, IAppLog log, string downloadDirectory)
    {
        _center = center;
        _log = log;
        _client = new UpdateClient(Repository, ReleaseKey.PublicKey, downloadDirectory, log);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = CheckInterval;
            await CheckAsync(userInitiated: false);
        };

        if (!_client.IsConfigured)
        {
            Status = "Updates are not configured for this build.";
        }
        else if (!CanInstall)
        {
            Status = "Updates apply to the installed app (this copy runs without installing).";
        }
    }

    /// <summary>GitHub "owner/repo" baked into the build (AssemblyMetadata UpdateRepository).</summary>
    public static string Repository { get; } = typeof(UpdateManager).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value ?? "";

    public string ReleasesPage => _client.ReleasesPage;

    public string CurrentVersion => SelfInstaller.CurrentVersion.ToString(3);

    /// <summary>Only the installed copy updates itself; developer and portable builds just report.</summary>
    public bool CanInstall => SelfInstaller.IsRunningInstalled;

    public bool IsUpdateAvailable => Available is not null;

    public void Start()
    {
        if (_client.IsConfigured)
        {
            _timer.Start();
        }
    }

    public async Task CheckAsync(bool userInitiated)
    {
        if (_working || !_client.IsConfigured)
        {
            if (userInitiated && !_client.IsConfigured)
            {
                _center.Notify("Updates are not configured for this build.", ToastKind.Info);
            }

            return;
        }

        _working = true;
        IsBusy = true;
        Status = "Checking for updates…";
        try
        {
            var latest = await _client.GetLatestAsync(CancellationToken.None);
            LastChecked = DateTimeOffset.Now;
            if (latest is null || !UpdateClient.IsNewer(latest, SelfInstaller.CurrentVersion))
            {
                Available = null;
                Status = $"Up to date (version {CurrentVersion}).";
                if (userInitiated)
                {
                    _center.Notify(Status, ToastKind.Success);
                }

                return;
            }

            Available = latest;
            Status = $"Version {latest.Version.ToString(3)} is available.";
            _log.Info($"Update available: {latest.Tag}.");
            if (CanInstall && _center.Services.Settings.AutoUpdate)
            {
                _working = false;
                await InstallAsync();
                return;
            }

            _center.Notify($"Universal RGB {latest.Version.ToString(3)} is available. Install it from Settings → Updates.", ToastKind.Info);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            Status = "Could not check for updates: " + ex.Message;
            _log.Warn("Update check failed.", ex);
            if (userInitiated)
            {
                _center.Notify(Status, ToastKind.Warning);
            }
        }
        finally
        {
            _working = false;
            IsBusy = false;
        }
    }

    /// <summary>Downloads, verifies and starts the setup for the available release. The setup closes and reopens this app.</summary>
    public async Task InstallAsync()
    {
        if (Available is not { } release || _working)
        {
            return;
        }

        if (!CanInstall)
        {
            _center.Notify("This copy runs without installing. Run UniversalRGB-Setup.exe to install, then updates are automatic.", ToastKind.Info);
            return;
        }

        _working = true;
        IsBusy = true;
        try
        {
            var progress = new Progress<DownloadProgress>(p => Status = p.Fraction is { } f
                ? $"Downloading version {release.Version.ToString(3)}… {f:P0}"
                : $"Downloading version {release.Version.ToString(3)}…");
            var setup = await _client.DownloadAsync(release, progress, CancellationToken.None);
            if (!_client.VerifyDownloaded(setup))
            {
                throw new InvalidDataException("The downloaded update failed its final signature check.");
            }

            Status = $"Installing version {release.Version.ToString(3)}…";
            _center.Notify($"Updating Universal RGB to {release.Version.ToString(3)}. It will reopen in a few seconds.", ToastKind.Info);
            await Task.Delay(2500);

            // The setup asks this copy to close, replaces it and reopens the installed app with --updated.
            Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, Arguments = "--updated" });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException
                                       or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Status = "Update failed: " + ex.Message;
            _log.Error("Update failed.", ex);
            _center.Notify(Status, ToastKind.Error);
        }
        finally
        {
            _working = false;
            IsBusy = false;
        }
    }

    partial void OnAvailableChanged(ReleaseInfo? value) => OnPropertyChanged(nameof(IsUpdateAvailable));

    public void Dispose()
    {
        _timer.Stop();
        _client.Dispose();
    }
}
