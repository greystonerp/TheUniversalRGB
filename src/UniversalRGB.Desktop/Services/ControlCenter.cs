using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UniversalRGB.Adapters.OpenRGB;
using UniversalRGB.Core.Compatibility;
using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Interfaces;
using UniversalRGB.Core.Inventory;
using UniversalRGB.Core.Lighting;
using UniversalRGB.Core.Profiles;
using UniversalRGB.Drivers.Detection;
using UniversalRGB.Drivers.Installation;
using UniversalRGB.Drivers.Verification;

namespace UniversalRGB.Desktop.Services;

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record ToastMessage(string Text, ToastKind Kind);

/// <summary>
/// Application state shared by every page. All public members are used from the UI thread;
/// background events are marshalled through the dispatcher.
/// </summary>
public sealed partial class ControlCenter : ObservableObject
{
    private readonly AppServices _s;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _reconnectTimer;
    private readonly DispatcherTimer _scheduleTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly HashSet<string> _noticesShown = new(StringComparer.Ordinal);
    private bool _connecting;
    private bool _settingUp;
    private DateTime _lastAutoSetup = DateTime.MinValue;
    private string? _scheduledProfileId;
    private string? _profileBeforeSchedule;
    private LightingProfile? _pendingSave;

    [ObservableProperty] private HardwareInventory? _inventory;
    [ObservableProperty] private DependencyReport? _dependencies;
    [ObservableProperty] private IReadOnlyList<DeviceEntry> _entries = [];
    [ObservableProperty] private LightingProfile? _activeProfile;

    /// <summary>Profile currently running on the engine (may be an unsaved quick effect or studio draft).</summary>
    [ObservableProperty] private LightingProfile? _liveProfile;
    [ObservableProperty] private bool _isTransient;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _connectionText = "OpenRGB not connected";
    [ObservableProperty] private string _connectionDetail = "";
    [ObservableProperty] private bool _lightingPaused;
    [ObservableProperty] private int _controllerCount;
    [ObservableProperty] private int _zoneCount;
    [ObservableProperty] private int _ledCount;
    [ObservableProperty] private DateTimeOffset? _lastScan;

    /// <summary>What OpenRGB's own log says about the last detection run.</summary>
    [ObservableProperty] private BackendFindings? _findings;

    /// <summary>Whether the running OpenRGB has administrator rights (null when it is not running).</summary>
    [ObservableProperty] private bool? _openRgbElevated;

    private bool _elevationOffered;

    public ControlCenter(AppServices services, Dispatcher ui)
    {
        _s = services;
        _ui = ui;

        _s.OpenRgb.StateChanged += (_, _) => _ui.InvokeAsync(UpdateConnection);
        _s.Registry.DevicesChanged += (_, _) => _ui.InvokeAsync(OnDevicesChanged);
        _s.Registry.RefreshFailed += (_, message) => _ui.InvokeAsync(() => Notify(message, ToastKind.Warning));
        _s.Engine.DeviceNotice += (_, e) => _ui.InvokeAsync(() =>
        {
            var key = e.DeviceId + e.Message;
            if (_noticesShown.Add(key))
            {
                Notify($"{e.DeviceName}: {e.Message}", e.IsError ? ToastKind.Error : ToastKind.Info);
            }
        });
        _s.Engine.FrameRendered += (_, frame) => Frame?.Invoke(this, frame);

        _reconnectTimer = new DispatcherTimer(TimeSpan.FromSeconds(6), DispatcherPriority.Background, async (_, _) => await AutoReconnectAsync(), _ui);
        _scheduleTimer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => EvaluateSchedules(), _ui);
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => FlushPendingSave(), _ui);
        _reconnectTimer.Stop();
        _scheduleTimer.Stop();
        _saveTimer.Stop();
    }

    public AppServices Services => _s;

    public ObservableCollection<LightingProfile> Profiles { get; } = [];

    public IReadOnlyList<RgbDevice> Devices => _s.Registry.Devices;

    public event EventHandler<ToastMessage>? Toast;

    /// <summary>Raised on the UI thread after the controllable device list changed.</summary>
    public event EventHandler? DevicesChanged;

    /// <summary>Raised on the engine thread for every rendered frame.</summary>
    public event EventHandler<LightingFrame>? Frame;

    public double MasterBrightness
    {
        get => LiveProfile?.MasterBrightness ?? 100;
        set
        {
            var live = LiveProfile;
            if (live is null || Math.Abs(live.MasterBrightness - value) < 0.01)
            {
                return;
            }

            live.MasterBrightness = Math.Clamp(value, 0, 100);
            _s.Engine.Apply(live);
            if (!IsTransient && ActiveProfile is { } active && active.Id == live.Id)
            {
                QueueSave(live);
            }

            OnPropertyChanged();
        }
    }

    public string LiveDescription => LiveProfile is null
        ? "No profile"
        : IsTransient ? $"{LightingEffect.DisplayName(LiveProfile.GlobalEffect.Kind)} (not saved)" : LiveProfile.Name;

    // ------------------------------------------------------------------ startup

    public async Task StartAsync()
    {
        LoadProfiles();
        var settings = _s.Settings;
        var id = settings.RestoreProfileOnStartup ? settings.StartupProfileId ?? settings.LastProfileId : null;
        var profile = Profiles.FirstOrDefault(p => p.Id == id) ?? Profiles.FirstOrDefault(p => p.Name == "Desktop") ?? Profiles.FirstOrDefault();
        if (profile is not null)
        {
            ActivateProfile(profile, remember: false, announce: false);
        }

        _s.Engine.Start();
        UpdateConnection();
        await RefreshHardwareAsync();

        if (settings.AutoInstallRequirements)
        {
            await EnsureRequirementsAsync(userInitiated: false);
        }
        else if (settings.AutoConnect)
        {
            await ConnectAsync(userInitiated: false);
        }

        _reconnectTimer.Start();
        _scheduleTimer.Start();
        EvaluateSchedules();
    }

    public async Task ShutdownAsync()
    {
        _reconnectTimer.Stop();
        _scheduleTimer.Stop();
        FlushPendingSave();
        await _s.Engine.StopAsync();
    }

    // ------------------------------------------------------------------ hardware & backend

    public async Task RefreshHardwareAsync()
    {
        using var busy = Busy("Scanning hardware…");
        try
        {
            Inventory = await _s.Inventory.ScanAsync();
            foreach (var warning in Inventory.Warnings)
            {
                _s.Log.Warn(warning);
            }
        }
        catch (Exception ex)
        {
            _s.Log.Error("Hardware scan failed.", ex);
            Notify("Hardware scan failed: " + ex.Message, ToastKind.Error);
        }

        await RefreshDependenciesAsync();
        LastScan = DateTimeOffset.Now;
        Reclassify();
    }

    public async Task RefreshDependenciesAsync()
    {
        try
        {
            RefreshBackendFindings();
            Dependencies = await DependencyChecker.CheckAsync(_s.Settings, Inventory, Findings, OpenRgbElevated, IsConnected);
        }
        catch (Exception ex)
        {
            _s.Log.Error("Dependency check failed.", ex);
        }

        Reclassify();
    }

    /// <summary>Full rescan: Windows inventory, requirements and backend device list.</summary>
    public async Task RescanAsync()
    {
        await RefreshHardwareAsync();
        if (IsConnected)
        {
            await _s.Registry.RefreshAsync(CancellationToken.None);
        }
    }

    public async Task<bool> ConnectAsync(bool userInitiated)
    {
        if (_connecting)
        {
            return false;
        }

        if (_s.OpenRgb.State == AdapterConnectionState.Connected)
        {
            return true;
        }

        _connecting = true;
        try
        {
            using var busy = userInitiated ? Busy("Connecting to OpenRGB…") : null;
            var ok = await _s.OpenRgb.ConnectAsync(CancellationToken.None);
            if (ok)
            {
                await _s.Registry.RefreshAsync(CancellationToken.None);
                if (userInitiated)
                {
                    Notify($"Connected to OpenRGB · {Devices.Count} controller(s).", ToastKind.Success);
                }
            }
            else if (userInitiated)
            {
                Notify(_s.OpenRgb.StateDetail, ToastKind.Warning);
            }

            await RefreshDependenciesAsync();
            return ok;
        }
        finally
        {
            _connecting = false;
        }
    }

    public async Task DisconnectAsync()
    {
        await _s.OpenRgb.DisconnectAsync();
        Notify("Disconnected from OpenRGB.", ToastKind.Info);
    }

    /// <summary>
    /// Makes the RGB backend ready: installs the pinned OpenRGB release if it is missing (verified download,
    /// per-user, no admin), starts its SDK server in the background, and connects.
    /// </summary>
    public async Task<bool> EnsureRequirementsAsync(bool userInitiated)
    {
        if (_settingUp)
        {
            return false;
        }

        _settingUp = true;
        _lastAutoSetup = DateTime.UtcNow;
        try
        {
            if (IsConnected)
            {
                return true;
            }

            var settings = _s.Settings;
            if (OpenRgbInstallation.FindExecutable(settings.OpenRgbExecutablePath) is null && await InstallOpenRgbAsync() is null)
            {
                return false;
            }

            if (await OpenRgbInstallation.IsServerReachableAsync(settings.OpenRgbHost, settings.OpenRgbPort, TimeSpan.FromMilliseconds(500)))
            {
                return await ConnectAsync(userInitiated);
            }

            return await StartOpenRgbServerAsync(quiet: !userInitiated);
        }
        finally
        {
            _settingUp = false;
        }
    }

    /// <summary>Downloads the pinned OpenRGB release, verifies its SHA-256 and unpacks it for this user.</summary>
    public async Task<string?> InstallOpenRgbAsync()
    {
        var package = _s.Drivers.Find("openrgb");
        if (package is not { CanDownload: true, Format: "zip" })
        {
            Notify("No verified OpenRGB download is configured. Install it from openrgb.org and use Locate OpenRGB….", ToastKind.Warning);
            return null;
        }

        using var busy = Busy($"Downloading {package.DisplayName}…");
        var progress = new Progress<DownloadProgress>(p => BusyText = p.Fraction is { } f
            ? $"Downloading {package.DisplayName}… {f:P0}"
            : $"Downloading {package.DisplayName}… {p.Received / 1024d / 1024d:0.0} MB");
        try
        {
            var zip = await _s.Requirements.DownloadVerifiedAsync(package, progress, CancellationToken.None);
            BusyText = $"Installing {package.DisplayName}…";
            var exe = await _s.Requirements.InstallPortableAsync(package, zip, CancellationToken.None);
            _s.Settings.OpenRgbExecutablePath = exe;
            _s.SaveSettings();
            Notify($"Installed {package.DisplayName} (official download, SHA-256 verified).", ToastKind.Success);
            await RefreshDependenciesAsync();
            return exe;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException
                                       or TaskCanceledException or UnauthorizedAccessException)
        {
            _s.Log.Error($"Automatic {package.Name} setup failed.", ex);
            Notify($"Could not install {package.Name} automatically: {ex.Message}", ToastKind.Error);
            return null;
        }
    }

    /// <summary>
    /// Downloads an installer-based requirement (e.g. the PawnIO kernel driver), verifies hash and signer, and runs it
    /// only after the user approves the consent dialog. Windows then shows its own UAC prompt.
    /// </summary>
    public async Task InstallPackageAsync(DriverPackage package)
    {
        if (package.Format == "zip")
        {
            if (await InstallOpenRgbAsync() is not null)
            {
                await EnsureRequirementsAsync(userInitiated: true);
            }

            return;
        }

        PackageVerification verification;
        using (Busy($"Downloading {package.DisplayName}…"))
        {
            try
            {
                var progress = new Progress<DownloadProgress>(p => BusyText = p.Fraction is { } f
                    ? $"Downloading {package.DisplayName}… {f:P0}"
                    : $"Downloading {package.DisplayName}…");
                var file = await _s.Requirements.DownloadVerifiedAsync(package, progress, CancellationToken.None);
                BusyText = "Checking signature…";
                verification = await PackageVerifier.VerifyAsync(file, package);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException
                                           or TaskCanceledException or UnauthorizedAccessException)
            {
                _s.Log.Error($"Download of {package.Name} failed.", ex);
                Notify($"Could not download {package.Name}: {ex.Message}", ToastKind.Error);
                return;
            }
        }

        var approved = Views.DialogWindow.ConsentToInstall(verification);
        var outcome = await _s.Installer.InstallAsync(verification, approved, CancellationToken.None);
        Notify(outcome.Message, outcome.Result switch
        {
            InstallResult.Succeeded => ToastKind.Success,
            InstallResult.RestartRequired => ToastKind.Warning,
            InstallResult.Cancelled => ToastKind.Info,
            _ => ToastKind.Error,
        });
        InstallHistoryChanged?.Invoke(this, EventArgs.Empty);
        if (outcome.Result is InstallResult.Succeeded or InstallResult.RestartRequired)
        {
            await RescanAsync();
        }
    }

    /// <summary>Raised after something was installed or an install was declined.</summary>
    public event EventHandler? InstallHistoryChanged;

    /// <summary>
    /// Restarts the background OpenRGB server (e.g. after closing MSI Center, or to gain administrator rights for
    /// SMBus devices). Only the headless server Universal RGB started is stopped; an OpenRGB window is never closed.
    /// </summary>
    public async Task<bool> RestartOpenRgbAsync(bool asAdministrator)
    {
        var settings = _s.Settings;
        var exe = OpenRgbInstallation.FindExecutable(settings.OpenRgbExecutablePath);
        if (exe is null)
        {
            return await EnsureRequirementsAsync(userInitiated: true);
        }

        if (asAdministrator && !settings.LaunchOpenRgbElevated)
        {
            settings.LaunchOpenRgbElevated = true;
            _s.SaveSettings();
        }

        _settingUp = true;
        try
        {
            if (OpenRgbInstallation.IsProcessRunning())
            {
                if (asAdministrator && OpenRgbInstallation.IsRunningElevated() == true)
                {
                    Notify("OpenRGB is already running as administrator.", ToastKind.Info);
                    return await ConnectAsync(userInitiated: true);
                }

                await _s.OpenRgb.DisconnectAsync();
                if (!OpenRgbInstallation.StopHeadlessServer(exe))
                {
                    Notify("An OpenRGB window is open. Close it yourself, then try again (Universal RGB never closes it for you).", ToastKind.Warning);
                    await ConnectAsync(userInitiated: false);
                    return false;
                }
            }
            else
            {
                await _s.OpenRgb.DisconnectAsync();
            }

            _lastAutoSetup = DateTime.UtcNow;
            var ok = await StartOpenRgbServerAsync(quiet: false);
            await RefreshDependenciesAsync();
            return ok;
        }
        finally
        {
            _settingUp = false;
        }
    }

    /// <summary>
    /// When OpenRGB could not reach SMBus and the PC has memory that looks like RGB memory, offer (once) to run OpenRGB
    /// as administrator. The user's answer is remembered.
    /// </summary>
    private async void OfferElevationIfUseful()
    {
        var settings = _s.Settings;
        if (_elevationOffered || settings.LaunchOpenRgbElevated || settings.ElevationOfferDismissed ||
            Findings?.SmbusUnavailable != true || OpenRgbElevated == true || Dependencies?.PawnIoInstalled != true)
        {
            return;
        }

        var rgbMemory = Inventory?.System.Memory.Select(m => MemoryRgbHints.Describe(m.PartNumber)).FirstOrDefault(h => h is not null);
        if (rgbMemory is null)
        {
            return;
        }

        _elevationOffered = true;
        var accept = Views.DialogWindow.Confirm(
            "Let OpenRGB reach your RGB memory?",
            $"Your memory looks like {rgbMemory}. RGB memory is controlled over SMBus through PawnIO, which only works when OpenRGB runs as administrator. " +
            "Windows will ask for permission each time OpenRGB starts. Universal RGB itself keeps running as a normal app.",
            "Run OpenRGB as administrator",
            cancelText: "Not now");
        if (accept)
        {
            await RestartOpenRgbAsync(asAdministrator: true);
        }
        else
        {
            settings.ElevationOfferDismissed = true;
            _s.SaveSettings();
            Notify("You can do this later from Drivers → Run OpenRGB as administrator.", ToastKind.Info);
        }
    }

    /// <summary>Launches OpenRGB's SDK server in the background and connects.</summary>
    public async Task<bool> StartOpenRgbServerAsync(bool quiet = false)
    {
        var settings = _s.Settings;
        var exe = OpenRgbInstallation.FindExecutable(settings.OpenRgbExecutablePath);
        if (exe is null)
        {
            if (!quiet)
            {
                Notify("OpenRGB is not installed yet. Use Install OpenRGB on the Drivers page.", ToastKind.Warning);
            }

            return false;
        }

        using var busy = Busy("Starting OpenRGB SDK server…");
        if (OpenRgbInstallation.IsProcessRunning())
        {
            if (!await OpenRgbInstallation.IsServerReachableAsync(settings.OpenRgbHost, settings.OpenRgbPort, TimeSpan.FromSeconds(1)))
            {
                if (!quiet)
                {
                    Notify("OpenRGB is already running without its SDK server. Open OpenRGB → SDK Server → Start Server.", ToastKind.Warning);
                }

                return false;
            }
        }
        else
        {
            try
            {
                OpenRgbInstallation.StartServer(exe, settings.OpenRgbPort, settings.LaunchOpenRgbElevated);
                _s.Log.Info($"Started {exe} --server (elevated: {settings.LaunchOpenRgbElevated}).");
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Notify("Starting OpenRGB as administrator was declined.", ToastKind.Info);
                return false;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Notify("Could not start OpenRGB: " + ex.Message, ToastKind.Error);
                return false;
            }
        }

        // OpenRGB runs device detection before its server answers; allow up to ~25 s.
        for (var i = 0; i < 50; i++)
        {
            if (await OpenRgbInstallation.IsServerReachableAsync(settings.OpenRgbHost, settings.OpenRgbPort, TimeSpan.FromMilliseconds(400)))
            {
                await Task.Delay(800);
                busy?.Dispose();
                return await ConnectAsync(userInitiated: !quiet);
            }

            await Task.Delay(500);
        }

        if (!quiet)
        {
            Notify("OpenRGB started, but its SDK server did not answer. Check OpenRGB → SDK Server.", ToastKind.Warning);
        }

        return false;
    }

    private async Task AutoReconnectAsync()
    {
        var settings = _s.Settings;
        if (IsConnected || _connecting || _settingUp || IsBusy || !(settings.AutoConnect || settings.AutoInstallRequirements))
        {
            return;
        }

        if (await OpenRgbInstallation.IsServerReachableAsync(settings.OpenRgbHost, settings.OpenRgbPort, TimeSpan.FromMilliseconds(300)))
        {
            await ConnectAsync(userInitiated: false);
            return;
        }

        // Automatic setup brings the background server back if it stopped (at most every two minutes,
        // and never while the user has their own OpenRGB window open).
        if (settings.AutoInstallRequirements && !OpenRgbInstallation.IsProcessRunning() && DateTime.UtcNow - _lastAutoSetup > TimeSpan.FromMinutes(2))
        {
            await EnsureRequirementsAsync(userInitiated: false);
        }
    }

    private void UpdateConnection()
    {
        var state = _s.OpenRgb.State;
        IsConnected = state == AdapterConnectionState.Connected;
        ConnectionText = state switch
        {
            AdapterConnectionState.Connected => "OpenRGB connected",
            AdapterConnectionState.Connecting => "Connecting…",
            _ => "OpenRGB not connected",
        };
        ConnectionDetail = _s.OpenRgb.StateDetail;
    }

    private void OnDevicesChanged()
    {
        var devices = Devices;
        ControllerCount = devices.Count;
        ZoneCount = devices.Sum(d => d.Zones.Count);
        LedCount = devices.Sum(d => d.LedCount);
        if (devices.Count > 0)
        {
            try
            {
                _s.Database.RecordSeen(devices);
            }
            catch (Exception ex)
            {
                _s.Log.Warn("Could not record seen devices.", ex);
            }
        }

        // OpenRGB finishes detection before it announces its device list, so its log is complete now.
        RefreshBackendFindings();
        Reclassify();
        DevicesChanged?.Invoke(this, EventArgs.Empty);
        if (IsConnected)
        {
            _ui.InvokeAsync(OfferElevationIfUseful, DispatcherPriority.Background);
        }
    }

    private void RefreshBackendFindings()
    {
        try
        {
            Findings = OpenRgbLogAnalyzer.ReadLatest();
            OpenRgbElevated = OpenRgbInstallation.IsRunningElevated();
        }
        catch (Exception ex)
        {
            _s.Log.Warn("Could not read OpenRGB's log.", ex);
        }
    }

    public void Reclassify()
    {
        var deps = Dependencies;
        var environment = new EnvironmentState
        {
            BackendConnected = IsConnected,
            OpenRgbInstalled = deps?.OpenRgbPath is not null,
            PawnIoInstalled = deps?.PawnIoInstalled ?? false,
            Conflicts = deps?.Conflicts ?? [],
            Findings = Findings,
            BackendElevated = OpenRgbElevated,
        };

        try
        {
            Entries = DeviceClassifier.Classify(Inventory, Devices, environment, _s.Database.GetKnownDevices(), _s.Database.Match);
        }
        catch (Exception ex)
        {
            _s.Log.Error("Device classification failed.", ex);
        }
    }

    // ------------------------------------------------------------------ profiles

    public void LoadProfiles()
    {
        Profiles.Clear();
        foreach (var profile in _s.Profiles.EnsureDefaults())
        {
            Profiles.Add(profile);
        }
    }

    /// <summary>Makes a saved profile the active one and runs it.</summary>
    public void ActivateProfile(LightingProfile profile, bool remember = true, bool announce = true)
    {
        ActiveProfile = profile;
        IsTransient = false;
        RunOnEngine(profile);
        if (remember)
        {
            _s.Settings.LastProfileId = profile.Id;
            _s.SaveSettings();
        }

        if (announce)
        {
            Notify($"Applied {profile.Name}.", ToastKind.Success);
        }
    }

    /// <summary>Runs an unsaved profile (quick effect or studio draft) without changing the active profile.</summary>
    public void RunTransient(LightingProfile draft)
    {
        IsTransient = true;
        RunOnEngine(draft);
    }

    /// <summary>Drops unsaved changes and runs the active profile again.</summary>
    public void RevertToActive()
    {
        if (ActiveProfile is { } active)
        {
            IsTransient = false;
            RunOnEngine(active);
        }
    }

    public void ApplyQuickEffect(LightingEffect effect)
    {
        var baseProfile = (LiveProfile ?? ActiveProfile ?? new LightingProfile()).Clone();
        baseProfile.Name = "Quick effect";
        baseProfile.SyncAll = true;
        baseProfile.GlobalEffect = effect;
        RunTransient(baseProfile);
    }

    public void SaveProfile(LightingProfile profile)
    {
        _s.Profiles.Save(profile);
        var index = IndexOf(profile.Id);
        if (index >= 0)
        {
            Profiles[index] = profile;
        }
        else
        {
            Profiles.Add(profile);
        }

        if (ActiveProfile?.Id == profile.Id)
        {
            ActiveProfile = profile;
            if (!IsTransient)
            {
                RunOnEngine(profile);
            }
        }
    }

    public LightingProfile SaveAsNew(LightingProfile source, string name)
    {
        var copy = source.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = string.IsNullOrWhiteSpace(name) ? "New profile" : name.Trim();
        copy.Schedule.Enabled = false;
        _s.Profiles.Save(copy);
        Profiles.Add(copy);
        ActivateProfile(copy, remember: true, announce: false);
        Notify($"Saved new profile {copy.Name}.", ToastKind.Success);
        return copy;
    }

    public LightingProfile Duplicate(LightingProfile profile)
    {
        var copy = _s.Profiles.Duplicate(profile);
        Profiles.Add(copy);
        return copy;
    }

    public void DeleteProfile(LightingProfile profile)
    {
        _s.Profiles.Delete(profile.Id);
        var index = IndexOf(profile.Id);
        if (index >= 0)
        {
            Profiles.RemoveAt(index);
        }

        if (_s.Settings.StartupProfileId == profile.Id)
        {
            _s.Settings.StartupProfileId = null;
            _s.SaveSettings();
        }

        if (ActiveProfile?.Id == profile.Id)
        {
            if (Profiles.FirstOrDefault() is { } next)
            {
                ActivateProfile(next, announce: false);
            }
            else
            {
                LoadProfiles();
                if (Profiles.FirstOrDefault() is { } fallback)
                {
                    ActivateProfile(fallback, announce: false);
                }
            }
        }
    }

    public void SetStartupProfile(LightingProfile? profile)
    {
        _s.Settings.StartupProfileId = profile?.Id;
        _s.SaveSettings();
        OnPropertyChanged(nameof(Profiles));
    }

    public async Task SetPausedAsync(bool paused)
    {
        if (paused)
        {
            await _s.Engine.StopAsync();
        }
        else
        {
            _s.Engine.Start();
        }

        LightingPaused = paused;
        Notify(paused ? "Lighting engine paused. Devices keep their current colors." : "Lighting engine resumed.", ToastKind.Info);
    }

    private void RunOnEngine(LightingProfile profile)
    {
        LiveProfile = profile;
        _s.Engine.Apply(profile);
        OnPropertyChanged(nameof(MasterBrightness));
        OnPropertyChanged(nameof(LiveDescription));
    }

    partial void OnIsTransientChanged(bool value) => OnPropertyChanged(nameof(LiveDescription));

    private int IndexOf(string id)
    {
        for (var i = 0; i < Profiles.Count; i++)
        {
            if (Profiles[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private void QueueSave(LightingProfile profile)
    {
        _pendingSave = profile;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushPendingSave()
    {
        _saveTimer.Stop();
        if (_pendingSave is { } profile)
        {
            _pendingSave = null;
            _s.Profiles.Save(profile);
        }
    }

    private void EvaluateSchedules()
    {
        if (!_s.Settings.EnableProfileSchedules)
        {
            return;
        }

        var now = TimeOnly.FromDateTime(DateTime.Now);
        var due = Profiles.FirstOrDefault(p => p.Schedule.IsActiveAt(now));
        if (due?.Id == _scheduledProfileId)
        {
            return;
        }

        if (due is not null)
        {
            if (_scheduledProfileId is null)
            {
                _profileBeforeSchedule = ActiveProfile?.Id;
            }

            ActivateProfile(due, remember: false, announce: false);
            Notify($"Schedule: switched to {due.Name}.", ToastKind.Info);
        }
        else if (Profiles.FirstOrDefault(p => p.Id == _profileBeforeSchedule) is { } previous)
        {
            ActivateProfile(previous, remember: false, announce: false);
            Notify($"Schedule ended: back to {previous.Name}.", ToastKind.Info);
        }

        _scheduledProfileId = due?.Id;
    }

    // ------------------------------------------------------------------ devices

    public async Task<bool> ResizeZoneAsync(RgbDevice device, RgbZone zone, int ledCount)
    {
        try
        {
            await _s.OpenRgb.ResizeZoneAsync(device.Id, zone.Id, ledCount, CancellationToken.None);
            await _s.Registry.RefreshAsync(CancellationToken.None);
            Notify($"{zone.Name} set to {ledCount} LEDs.", ToastKind.Success);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or KeyNotFoundException)
        {
            Notify(ex.Message, ToastKind.Error);
            return false;
        }
    }

    public void ForgetDevice(string key)
    {
        _s.Database.ForgetKnownDevice(key);
        Reclassify();
    }

    // ------------------------------------------------------------------ helpers

    public void Notify(string text, ToastKind kind = ToastKind.Info)
    {
        if (kind is ToastKind.Error or ToastKind.Warning)
        {
            _s.Log.Warn(text);
        }

        Toast?.Invoke(this, new ToastMessage(text, kind));
    }

    private BusyScope Busy(string text) => new(this, text);

    private sealed class BusyScope : IDisposable
    {
        private readonly ControlCenter _owner;
        private bool _disposed;

        public BusyScope(ControlCenter owner, string text)
        {
            _owner = owner;
            owner.BusyText = text;
            owner.IsBusy = true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.IsBusy = false;
            _owner.BusyText = "";
        }
    }

    partial void OnLiveProfileChanged(LightingProfile? value)
    {
        OnPropertyChanged(nameof(LiveDescription));
        OnPropertyChanged(nameof(MasterBrightness));
    }
}
