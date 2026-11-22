using Microsoft.Win32;

namespace UniversalRGB.Desktop.Services;

/// <summary>Per-user "start with Windows" entry (HKCU Run key, no admin rights needed).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UniversalRGB";
    public const string StartupArgument = "--startup";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value && value.Contains(Environment.ProcessPath ?? "UniversalRGB.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>If a startup entry exists but points at another copy of the app, point it at <paramref name="exe"/>.</summary>
    public static void Repoint(string exe)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string value && !value.Contains(exe, StringComparison.OrdinalIgnoreCase))
        {
            key.SetValue(ValueName, $"\"{exe}\" {StartupArgument}");
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled && Environment.ProcessPath is { } exe)
        {
            key.SetValue(ValueName, $"\"{exe}\" {StartupArgument}");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
