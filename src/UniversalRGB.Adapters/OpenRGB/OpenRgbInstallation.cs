using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Win32;

namespace UniversalRGB.Adapters.OpenRGB;

/// <summary>Finds a local OpenRGB installation and starts its SDK server on request.</summary>
public static class OpenRgbInstallation
{
    public const string OfficialSite = "https://openrgb.org/";
    public const string ReleasesPage = "https://openrgb.org/releases.html";

    /// <summary>Returns the path to OpenRGB.exe, or null when it cannot be found.</summary>
    public static string? FindExecutable(string? configuredPath)
    {
        if (IsExe(configuredPath))
        {
            return configuredPath;
        }

        foreach (var candidate in CandidatePaths())
        {
            if (IsExe(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static bool IsProcessRunning() => Process.GetProcessesByName("OpenRGB").Length > 0;

    /// <summary>
    /// True when a running OpenRGB has administrator rights, false when it does not, null when none is running.
    /// A process this app cannot inspect is running at a higher integrity level, i.e. elevated.
    /// </summary>
    public static bool? IsRunningElevated()
    {
        var processes = Process.GetProcessesByName("OpenRGB");
        try
        {
            if (processes.Length == 0)
            {
                return null;
            }

            foreach (var process in processes)
            {
                if (TokenIsElevated(process.Id) != false)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }

    /// <summary>
    /// Stops the headless OpenRGB server that Universal RGB started (no window, running from <paramref name="executable"/>).
    /// An OpenRGB window the user opened is never closed. Returns true when no such server is left running.
    /// </summary>
    public static bool StopHeadlessServer(string executable)
    {
        var stopped = true;
        foreach (var process in Process.GetProcessesByName("OpenRGB"))
        {
            using (process)
            {
                string? path;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    stopped = false; // elevated or protected: not ours to touch
                    continue;
                }

                var ours = path is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
                if (!ours || process.MainWindowHandle != IntPtr.Zero)
                {
                    stopped = false;
                    continue;
                }

                process.Kill();
                process.WaitForExit(5000);
            }
        }

        return stopped;
    }

    private static bool? TokenIsElevated(int processId)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint TOKEN_QUERY = 0x0008;
        const int TokenElevation = 20;

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero)
        {
            return true;
        }

        try
        {
            if (!OpenProcessToken(handle, TOKEN_QUERY, out var token))
            {
                return true;
            }

            try
            {
                return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _) ? elevated != 0 : null;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returnLength);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static async Task<bool> IsServerReachableAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts OpenRGB as a headless SDK server (with only --server, OpenRGB shows no window, tray icon or dialogs).
    /// When <paramref name="elevated"/> is true Windows shows a UAC prompt (OpenRGB needs administrator rights for
    /// SMBus devices such as RGB memory).
    /// </summary>
    public static Process? StartServer(string executable, int port, bool elevated)
    {
        var info = new ProcessStartInfo(executable)
        {
            Arguments = $"--server --server-port {port}",
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = true,
        };
        if (elevated)
        {
            info.Verb = "runas";
        }

        return Process.Start(info);
    }

    private static bool IsExe(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> CandidatePaths()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return Path.Combine(programFiles, "OpenRGB", "OpenRGB.exe");
        yield return Path.Combine(programFilesX86, "OpenRGB", "OpenRGB.exe");
        yield return Path.Combine(local, "Programs", "OpenRGB", "OpenRGB.exe");
        yield return Path.Combine(local, "OpenRGB", "OpenRGB.exe");

        foreach (var path in FromUninstallRegistry())
        {
            yield return path;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return Path.Combine(dir.Trim(), "OpenRGB.exe");
        }

        // Portable builds are commonly unzipped into Downloads, Desktop or Documents.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in new[] { Path.Combine(profile, "Downloads"), Path.Combine(profile, "Desktop"), Path.Combine(profile, "Documents"), @"C:\OpenRGB" })
        {
            foreach (var found in SafeSearch(root))
            {
                yield return found;
            }
        }
    }

    private static IEnumerable<string> SafeSearch(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
            return Directory.EnumerateFiles(root, "OpenRGB.exe", options).Take(3).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> FromUninstallRegistry()
    {
        var results = new List<string>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                using var key = hive.OpenSubKey(path);
                if (key is null)
                {
                    continue;
                }

                foreach (var name in key.GetSubKeyNames())
                {
                    using var app = key.OpenSubKey(name);
                    if (app?.GetValue("DisplayName") is not string display || !display.Contains("OpenRGB", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (app.GetValue("InstallLocation") is string location && location.Length > 0)
                    {
                        results.Add(Path.Combine(location.Trim('"'), "OpenRGB.exe"));
                    }

                    if (app.GetValue("DisplayIcon") is string icon && icon.Length > 0)
                    {
                        results.Add(icon.Split(',')[0].Trim('"'));
                    }
                }
            }
        }

        return results;
    }
}
