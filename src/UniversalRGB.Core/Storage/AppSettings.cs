using System.Text.Json;

namespace UniversalRGB.Core.Storage;

public sealed class AppSettings
{
    // RGB backend
    public string OpenRgbHost { get; set; } = "127.0.0.1";
    public int OpenRgbPort { get; set; } = 6742;
    public string? OpenRgbExecutablePath { get; set; }
    public bool AutoConnect { get; set; } = true;

    /// <summary>
    /// Automatic setup: download and unpack the pinned OpenRGB release if it is missing, start its SDK server
    /// in the background and connect. Kernel drivers (PawnIO) are never installed without a prompt.
    /// </summary>
    public bool AutoInstallRequirements { get; set; } = true;

    /// <summary>Launch OpenRGB elevated (needed for SMBus devices such as RGB RAM). Shows a UAC prompt.</summary>
    public bool LaunchOpenRgbElevated { get; set; }

    /// <summary>The user declined the offer to run OpenRGB as administrator; do not ask again automatically.</summary>
    public bool ElevationOfferDismissed { get; set; }

    // Startup
    public bool StartWithWindows { get; set; }
    public bool StartMinimizedAtLogin { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool RestoreProfileOnStartup { get; set; } = true;
    public string? StartupProfileId { get; set; }
    public string? LastProfileId { get; set; }

    // Updates
    /// <summary>Download and install signed updates from GitHub Releases automatically.</summary>
    public bool AutoUpdate { get; set; } = true;

    // Engine
    public int FrameRate { get; set; } = 30;
    public bool RescanOnDeviceChange { get; set; } = true;
    public bool EnableProfileSchedules { get; set; } = true;
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _path;

    public SettingsStore(string path) => _path = path;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings();
                settings.FrameRate = Math.Clamp(settings.FrameRate, 5, 60);
                settings.OpenRgbPort = settings.OpenRgbPort is > 0 and < 65536 ? settings.OpenRgbPort : 6742;
                return settings;
            }
        }
        catch (JsonException)
        {
            // Fall back to defaults; the broken file is preserved for inspection.
            File.Copy(_path, _path + ".broken", overwrite: true);
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
