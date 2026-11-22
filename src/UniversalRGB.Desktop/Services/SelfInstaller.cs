using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Win32;
using UniversalRGB.Core.Diagnostics;

namespace UniversalRGB.Desktop.Services;

public enum LaunchAction
{
    /// <summary>Run the app from where it is (installed copy, developer build or portable mode).</summary>
    RunHere,

    /// <summary>Not installed yet: install for the current user, then open the installed copy.</summary>
    Install,

    /// <summary>An older (or different) build is installed: replace it, then open it.</summary>
    Update,

    /// <summary>The same or a newer version is already installed: just open that copy.</summary>
    LaunchInstalled,
}

/// <summary>
/// Makes the published single-file EXE its own setup. Installs per user (no administrator rights) into
/// %LOCALAPPDATA%\Programs\UniversalRGB, adds Start menu / desktop shortcuts and an Apps &amp; features entry,
/// and supports clean uninstall. User data in %APPDATA% / %LOCALAPPDATA%\UniversalRGB is never touched.
/// </summary>
public static class SelfInstaller
{
    public const string ExeName = "UniversalRGB.exe";
    public const string DisplayName = "Universal RGB Control";
    public const string ExitEventName = @"Local\UniversalRGB.Desktop.Exit";
    public const string InstalledArgument = "--installed";
    public const string UninstallArgument = "--uninstall";
    public const string QuietArgument = "--quiet";

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\UniversalRGB";
    private static readonly string[] SetupOnlyArguments = ["--install", InstalledArgument, "--portable", "--no-install"];

    public static string InstallDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "UniversalRGB");

    public static string InstalledExe => Path.Combine(InstallDirectory, ExeName);

    public static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), DisplayName + ".lnk");

    public static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), DisplayName + ".lnk");

    public static Version CurrentVersion => typeof(SelfInstaller).Assembly.GetName().Version ?? new Version(0, 0);

    public static bool IsRunningInstalled =>
        Environment.ProcessPath is { } exe && SamePath(Path.GetDirectoryName(exe)!, InstallDirectory);

    public static bool IsInstalled => File.Exists(InstalledExe);

    public static LaunchAction Decide(string[] args)
    {
        if (HasArg(args, "--portable") || HasArg(args, "--no-install"))
        {
            return LaunchAction.RunHere;
        }

        // Isolated data folders are used for portable installs and test runs.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UNIVERSALRGB_HOME")))
        {
            return LaunchAction.RunHere;
        }

        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return LaunchAction.RunHere;
        }

        // Developer builds run from bin\ with loose assemblies; only the published single-file EXE acts as a setup.
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "UniversalRGB.dll")))
        {
            return LaunchAction.RunHere;
        }

        if (IsRunningInstalled)
        {
            return LaunchAction.RunHere;
        }

        if (!IsInstalled)
        {
            return LaunchAction.Install;
        }

        var installed = InstalledVersion();
        if (installed is null || CurrentVersion > installed)
        {
            return LaunchAction.Update;
        }

        if (CurrentVersion < installed)
        {
            return LaunchAction.LaunchInstalled;
        }

        return FilesDiffer(exe, InstalledExe) ? LaunchAction.Update : LaunchAction.LaunchInstalled;
    }

    public static Version? InstalledVersion()
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(InstalledExe);
            return Version.TryParse(info.FileVersion, out var v) ? v : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ install steps

    /// <summary>True when another copy of Universal RGB (installed or portable) is running.</summary>
    public static bool OtherCopyRunning()
    {
        var running = FindOtherCopies();
        running.ForEach(p => p.Dispose());
        return running.Count > 0;
    }

    /// <summary>
    /// Asks every other running copy of Universal RGB to exit (gracefully, then forcibly after a timeout) so the
    /// installed copy can take over. Only ever touches this app's own processes.
    /// </summary>
    public static void StopRunningCopies(IAppLog log)
    {
        var running = FindOtherCopies();
        if (running.Count == 0)
        {
            return;
        }

        if (EventWaitHandle.TryOpenExisting(ExitEventName, out var exitEvent))
        {
            using (exitEvent)
            {
                exitEvent.Set();
            }
        }

        foreach (var process in running)
        {
            using (process)
            {
                if (!process.WaitForExit(8000))
                {
                    log.Warn($"Universal RGB (pid {process.Id}) did not exit in time; ending it to finish setup.");
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
        }
    }

    /// <summary>Copies this EXE (and the curated manifests) into the install folder.</summary>
    public static void CopyFiles()
    {
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the running executable.");
        Directory.CreateDirectory(InstallDirectory);

        var temp = InstalledExe + ".new";
        File.Copy(source, temp, overwrite: true);
        Retry(() => File.Move(temp, InstalledExe, overwrite: true));

        var manifests = Path.Combine(InstallDirectory, "manifests");
        Directory.CreateDirectory(manifests);
        var sourceManifests = Path.Combine(Path.GetDirectoryName(source)!, "manifests");
        foreach (var name in new[] { "compatibility.json", "drivers.json" })
        {
            var fromDisk = Path.Combine(sourceManifests, name);
            if (File.Exists(fromDisk))
            {
                File.Copy(fromDisk, Path.Combine(manifests, name), overwrite: true);
                continue;
            }

            using var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("manifests." + name);
            if (embedded is not null)
            {
                using var file = File.Create(Path.Combine(manifests, name));
                embedded.CopyTo(file);
            }
        }
    }

    /// <summary>Start menu shortcut always; desktop shortcut on first install or when it already exists.</summary>
    public static void CreateShortcuts(bool isUpdate)
    {
        ShellLink.Create(StartMenuShortcut, InstalledExe, "Control your PC's RGB lighting");
        if (!isUpdate || File.Exists(DesktopShortcut))
        {
            ShellLink.Create(DesktopShortcut, InstalledExe, "Control your PC's RGB lighting");
        }
    }

    /// <summary>Registers the app in Settings → Apps &amp; features (per user).</summary>
    public static void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey, writable: true);
        var size = (int)Math.Min(int.MaxValue, new FileInfo(InstalledExe).Length / 1024);
        key.SetValue("DisplayName", DisplayName);
        key.SetValue("DisplayVersion", CurrentVersion.ToString(3));
        key.SetValue("Publisher", DisplayName);
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("InstallLocation", InstallDirectory);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        key.SetValue("UninstallString", $"\"{InstalledExe}\" {UninstallArgument}");
        key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" {UninstallArgument} {QuietArgument}");
        key.SetValue("EstimatedSize", size, RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

        // If "Start with Windows" was enabled from a portable copy, point it at the installed copy.
        StartupRegistration.Repoint(InstalledExe);
    }

    /// <summary>Starts the installed copy, forwarding the user's arguments.</summary>
    public static void LaunchInstalled(IEnumerable<string> args, bool justInstalled)
    {
        var forward = args.Where(a => !SetupOnlyArguments.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();
        if (justInstalled)
        {
            forward.Add(InstalledArgument);
        }

        var info = new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = InstallDirectory };
        foreach (var arg in forward)
        {
            info.ArgumentList.Add(arg);
        }

        Process.Start(info);
    }

    // ------------------------------------------------------------------ uninstall

    /// <summary>
    /// Removes the program, shortcuts, startup entry and Apps &amp; features entry. Settings, profiles and logs are kept.
    /// When running from the install folder, the folder is deleted a moment after this process exits.
    /// </summary>
    public static void Uninstall(IAppLog log)
    {
        StopRunningCopies(log);
        StartupRegistration.Set(false);
        TryDelete(StartMenuShortcut);
        TryDelete(DesktopShortcut);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        if (!Directory.Exists(InstallDirectory))
        {
            return;
        }

        if (IsRunningInstalled)
        {
            // This EXE is locked while it runs: delete the folder from a short-lived helper after we exit.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{InstallDirectory}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            });
        }
        else
        {
            Retry(() => Directory.Delete(InstallDirectory, recursive: true));
        }

        log.Info("Universal RGB was uninstalled. User data was kept.");
    }

    // ------------------------------------------------------------------ helpers

    public static bool HasArg(string[] args, string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Other running processes of this app (named UniversalRGB.exe, in the current session).</summary>
    private static List<Process> FindOtherCopies()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var result = new List<Process>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            if (process.Id != Environment.ProcessId && process.SessionId == session)
            {
                result.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return result;
    }

    private static bool FilesDiffer(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length)
        {
            return true;
        }

        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        return !SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < 15 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(300);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
