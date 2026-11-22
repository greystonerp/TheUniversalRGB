namespace UniversalRGB.Core.Storage;

/// <summary>Per-user storage locations.</summary>
public sealed class AppPaths
{
    public AppPaths(string roamingRoot, string localRoot)
    {
        Roaming = roamingRoot;
        Local = localRoot;
    }

    /// <summary>
    /// Per-user folders, or a self-contained folder when the UNIVERSALRGB_HOME environment variable is set
    /// (portable installs and isolated test runs).
    /// </summary>
    public static AppPaths Default { get; } = Create();

    private static AppPaths Create()
    {
        var home = Environment.GetEnvironmentVariable("UNIVERSALRGB_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return new AppPaths(Path.Combine(home, "roaming"), Path.Combine(home, "local"));
        }

        return new AppPaths(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UniversalRGB"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalRGB"));
    }

    /// <summary>Settings and profiles (roams with the user).</summary>
    public string Roaming { get; }

    /// <summary>Database, logs and verified package cache (machine-local).</summary>
    public string Local { get; }

    public string Profiles => Path.Combine(Roaming, "profiles");

    public string SettingsFile => Path.Combine(Roaming, "settings.json");

    public string Database => Path.Combine(Local, "universalrgb.db");

    public string Logs => Path.Combine(Local, "logs");

    public string Packages => Path.Combine(Local, "packages");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Roaming);
        Directory.CreateDirectory(Local);
        Directory.CreateDirectory(Profiles);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Packages);
    }
}
