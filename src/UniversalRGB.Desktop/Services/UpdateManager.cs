using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UniversalRGB.Core.Diagnostics;
using UniversalRGB.Desktop.ViewModels;
using UniversalRGB.Desktop.Views;
using UniversalRGB.Drivers.Installation;
using UniversalRGB.Drivers.Updates;

namespace UniversalRGB.Desktop.Services;

/// <summary>
/// Keeps the installed app up to date from the project's GitHub Releases. Updates are only installed after their
/// ECDSA signature verifies against the release key built into this app.
/// <list type="bullet">
/// <item>When the window is open (launch, reopened from the tray, or Check now) an available update is offered in a popup.</item>
/// <item>When the app runs quietly in the tray, updates install automatically (if enabled).</item>
/// </list>
/// </summary>
public sealed partial class UpdateManager : ObservableObject, IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly ControlCenter _center;
    private readonly UpdateClient _client;
    private readonly IAppLog _log;
    private readonly Func<bool> _windowVisible;
    private readonly DispatcherTimer _timer;
    private bool _working;
    private string? _offeredTag;

    [ObservableProperty] private string _status = "Not checked yet.";
    [ObservableProperty] private ReleaseInfo? _available;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private DateTimeOffset? _lastChecked;

    public UpdateManager(ControlCenter center, IAppLog log, string downloadDirectory, Func<bool> windowVisible)
    {
        _center = center;
        _log = log;
        _windowVisible = windowVisible;
        _client = new UpdateClient(Repository, ReleaseKey.PublicKey, downloadDirectory, log);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
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

        ReleaseInfo? newer = null;
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

            if (!latest.IsComplete)
            {
                Available = null;
                Status = $"Version {latest.Version.ToString(3)} is being published right now. Check again in a minute.";
                if (userInitiated)
                {
                    _center.Notify(Status, ToastKind.Info);
                }

                return;
            }

            Available = latest;
            Status = $"Version {latest.Version.ToString(3)} is available.";
            _log.Info($"Update available: {latest.Tag}.");
            newer = latest;
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

        if (newer is null)
        {
            return;
        }

        if (userInitiated || _windowVisible())
        {
            await OfferAsync(newer, force: userInitiated);
        }
        else if (CanInstall && _center.Services.Settings.AutoUpdate)
        {
            _log.Info("Window is hidden; installing the update automatically.");
            await InstallAsync();
        }
        else
        {
            _center.Notify($"Universal RGB {newer.Version.ToString(3)} is available. Open the app to install it.", ToastKind.Info);
        }
    }

    /// <summary>Called whenever the main window is shown again (e.g. reopened from the tray).</summary>
    public async void OnWindowShown()
    {
        if (Available is { } release)
        {
            await OfferAsync(release, force: false);
        }
        else if (_client.IsConfigured && (LastChecked is null || DateTimeOffset.Now - LastChecked > TimeSpan.FromHours(1)))
        {
            await CheckAsync(userInitiated: false);
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
        var handedOff = false;
        var version = release.Version.ToString(3);
        try
        {
            var progress = new Progress<DownloadProgress>(p => Status = p.Fraction is { } f
                ? $"Downloading version {version}… {f:P0}"
                : $"Downloading version {version}…");
            var setup = await _client.DownloadAsync(release, progress, CancellationToken.None);
            if (!_client.VerifyDownloaded(setup))
            {
                throw new InvalidDataException("The downloaded update failed its final signature check.");
            }

            Status = $"Installing version {version}…";
            _center.Notify($"Updating Universal RGB to {version}. It will reopen in a few seconds.", ToastKind.Info);
            await Task.Delay(2500);

            // The setup asks this copy to close, replaces it and reopens the installed app with --updated.
            Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, Arguments = "--updated" });
            handedOff = true;
            Status = $"Installing version {version}. Universal RGB reopens in a few seconds.";
            _ = UnlockIfStillRunningAsync(version);
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
            // Once the setup is running, stay locked so another check or click cannot start a second download and
            // setup in the moment before this copy is closed.
            if (!handedOff)
            {
                _working = false;
                IsBusy = false;
            }
        }
    }

    /// <summary>The setup normally closes this copy within seconds. If it did not (e.g. it was closed), allow another try.</summary>
    private async Task UnlockIfStillRunningAsync(string version)
    {
        await Task.Delay(TimeSpan.FromMinutes(2));
        _working = false;
        IsBusy = false;
        Status = $"Version {version} was not installed. Use Install update to try again.";
    }

    /// <summary>The "Update available" popup (shown at most once per release per session unless the user asked).</summary>
    private async Task OfferAsync(ReleaseInfo release, bool force)
    {
        if (_working || (!force && _offeredTag == release.Tag))
        {
            return;
        }

        _offeredTag = release.Tag;
        var version = release.Version.ToString(3);
        _log.Info($"Offering update {release.Tag}.");
        var notes = UpdateClient.SummarizeNotes(release.Notes);
        var message = $"Universal RGB {version} is ready. You have version {CurrentVersion}."
            + (notes.Length > 0 ? $"\n\nWhat's new:\n{notes}" : "");

        if (!CanInstall)
        {
            if (DialogWindow.Confirm("Update available", message + "\n\nThis copy runs without installing, so get the new setup from GitHub.", "Open download page", cancelText: "Later"))
            {
                Shell.Open(release.PageUrl.Length > 0 ? release.PageUrl : ReleasesPage);
            }

            return;
        }

        if (DialogWindow.Confirm("Update available", message + "\n\nUpdating takes a few seconds; your lighting keeps running.", "Update now", cancelText: "Later"))
        {
            await InstallAsync();
        }
        else
        {
            Status = $"Version {version} is available. Install it here whenever you like.";
        }
    }

    partial void OnAvailableChanged(ReleaseInfo? value) => OnPropertyChanged(nameof(IsUpdateAvailable));

    public void Dispose()
    {
        _timer.Stop();
        _client.Dispose();
    }
}
