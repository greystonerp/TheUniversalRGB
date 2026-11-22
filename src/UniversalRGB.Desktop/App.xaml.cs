using System.Windows;
using System.Windows.Threading;
using UniversalRGB.Core.Storage;
using UniversalRGB.Desktop.Services;
using UniversalRGB.Desktop.ViewModels;
using UniversalRGB.Desktop.Views;

namespace UniversalRGB.Desktop;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\UniversalRGB.Desktop.Instance";
    private const string ShowEventName = @"Local\UniversalRGB.Desktop.Show";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _exitEvent;
    private AppLog? _log;
    private AppServices? _services;
    private ControlCenter? _center;
    private TrayIconService? _tray;
    private UpdateManager? _updates;
    private MainWindow? _window;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        _log = new AppLog(AppPaths.Default.Logs);
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => _log?.Error("Unhandled exception.", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            _log?.Error("Unobserved task exception.", a.Exception);
            a.SetObserved();
        };

        // Uninstall (from Settings → Apps, or the Settings page).
        if (SelfInstaller.HasArg(args, SelfInstaller.UninstallArgument))
        {
            RunUninstall(quiet: SelfInstaller.HasArg(args, SelfInstaller.QuietArgument));
            return;
        }

        // The published EXE is its own setup: install or update first, then open the installed copy.
        var action = SelfInstaller.Decide(args);
        switch (action)
        {
            case LaunchAction.Install:
            case LaunchAction.Update:
                _log.Info($"Running setup ({action}) from {Environment.ProcessPath}.");
                new SetupWindow(action, args, _log, () => _ = StartAppAsync(args)).Show();
                return;

            case LaunchAction.LaunchInstalled:
                SelfInstaller.LaunchInstalled(args, justInstalled: false);
                Shutdown();
                return;
        }

        _ = StartAppAsync(args);
    }

    private void RunUninstall(bool quiet)
    {
        if (!quiet && !DialogWindow.Confirm(
                "Uninstall Universal RGB Control?",
                "The app, its shortcuts and its startup entry will be removed. Your settings and profiles stay in your user folder, and no drivers are touched.",
                "Uninstall",
                danger: true))
        {
            Shutdown();
            return;
        }

        try
        {
            SelfInstaller.Uninstall(_log!);
            if (!quiet)
            {
                DialogWindow.Alert("Universal RGB was uninstalled", "Thanks for trying it. Your lighting stays as it is until another app changes it.");
            }
        }
        catch (Exception ex)
        {
            _log?.Error("Uninstall failed.", ex);
            if (!quiet)
            {
                DialogWindow.Alert("Uninstall failed", ex.Message, error: true);
            }
        }

        Shutdown();
    }

    private async Task StartAppAsync(string[] args)
    {
        // Single instance: a second launch just brings the running window to the front.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirst);
        if (!isFirst)
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }

            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                if (_exiting)
                {
                    return;
                }

                Dispatcher.Invoke(ShowMainWindow);
            }
        })
        { IsBackground = true, Name = "SingleInstanceListener" };
        listener.Start();

        // Setup/uninstall of a newer version asks this copy to close so its files can be replaced.
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SelfInstaller.ExitEventName);
        new Thread(() =>
        {
            if (_exitEvent.WaitOne() && !_exiting)
            {
                Dispatcher.Invoke(ExitApplication);
            }
        })
        { IsBackground = true, Name = "ExitListener" }.Start();

        var paths = AppPaths.Default;
        var log = _log!;
        log.Info($"Universal RGB Control {SelfInstaller.CurrentVersion.ToString(3)} starting from {Environment.ProcessPath} (args: {string.Join(' ', args)}).");
        if (SelfInstaller.IsRunningInstalled && Environment.ProcessPath is { } exe)
        {
            StartupRegistration.Repoint(exe);
        }

        try
        {
            _services = new AppServices(log, paths);
        }
        catch (Exception ex)
        {
            log.Error("Startup failed.", ex);
            DialogWindow.Alert("Universal RGB could not start", ex.Message, error: true);
            Shutdown(1);
            return;
        }

        _center = new ControlCenter(_services, Dispatcher);
        _updates = new UpdateManager(_center, log, Path.Combine(paths.Packages, "updates"));
        var main = new MainViewModel(_center, _updates);
        _window = new MainWindow(main, _services.Settings);
        MainWindow = _window;
        _window.Closing += (_, args) =>
        {
            if (!_exiting && _services.Settings.MinimizeToTray)
            {
                args.Cancel = true;
                _window.Hide();
                _tray?.ShowHiddenHint();
            }
        };
        _window.Closed += (_, _) =>
        {
            if (!_exiting)
            {
                ExitApplication();
            }
        };

        _tray = new TrayIconService(_center, ShowMainWindow, ExitApplication);

        // "--page devices" opens a specific page (dashboard, devices, lighting, profiles, drivers, settings).
        var pageIndex = Array.FindIndex(args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        if (pageIndex >= 0 && pageIndex + 1 < args.Length)
        {
            main.NavigateCommand.Execute(args[pageIndex + 1].ToLowerInvariant());
        }

        var launchedAtLogin = args.Contains(StartupRegistration.StartupArgument, StringComparer.OrdinalIgnoreCase);
        if (!(launchedAtLogin && _services.Settings.StartMinimizedAtLogin))
        {
            _window.Show();
        }

        await _center.StartAsync();
        _updates.Start();

        if (SelfInstaller.HasArg(args, "--updated"))
        {
            _center.Notify($"Universal RGB was updated to version {SelfInstaller.CurrentVersion.ToString(3)}.", ToastKind.Success);
        }

        // "--profile Gaming" applies a profile by name at startup (useful for shortcuts).
        if (SelfInstaller.HasArg(args, SelfInstaller.InstalledArgument))
        {
            _center.Notify("Universal RGB is installed. You can find it in the Start menu and on your desktop.", ToastKind.Success);
        }

        var profileIndex = Array.FindIndex(args, a => a.Equals("--profile", StringComparison.OrdinalIgnoreCase));
        if (profileIndex >= 0 && profileIndex + 1 < args.Length)
        {
            var wanted = args[profileIndex + 1];
            if (_center.Profiles.FirstOrDefault(p => p.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) is { } profile)
            {
                _center.ActivateProfile(profile);
            }
            else
            {
                _center.Notify($"No profile named \"{wanted}\".", ToastKind.Warning);
            }
        }
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    private async void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _exitEvent?.Set(); // release the exit listener thread
        _log?.Info("Exiting.");
        try
        {
            if (_center is not null)
            {
                await _center.ShutdownAsync();
            }

            if (_services is not null)
            {
                await _services.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            _log?.Error("Error during shutdown.", ex);
        }

        _updates?.Dispose();
        _tray?.Dispose();
        _window?.Close();
        _showEvent?.Set();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _log?.Dispose();
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("UI exception.", e.Exception);
        e.Handled = true;
        _center?.Notify("Something went wrong: " + e.Exception.Message + " (details in the log).", ToastKind.Error);
    }
}
