using System.Globalization;
using System.Text.RegularExpressions;
using UniversalRGB.Core.Compatibility;

namespace UniversalRGB.Adapters.OpenRGB;

/// <summary>
/// Reads OpenRGB's own log (%APPDATA%\OpenRGB\logs) to explain why hardware was or was not exposed:
/// SMBus/PawnIO failures, MSI Mystic Light firmware or probe problems, and the controllers it registered.
/// </summary>
public static partial class OpenRgbLogAnalyzer
{
    public static string DefaultLogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRGB", "logs");

    [GeneratedRegex(@"Registering RGB controller (?<name>.+)$")]
    private static partial Regex Registered();

    [GeneratedRegex(@"OpenRGB v(?<version>\S+)")]
    private static partial Regex VersionLine();

    [GeneratedRegex(@"Launched:\s*(?<stamp>\d{8}_\d{6})")]
    private static partial Regex LaunchedLine();

    /// <summary>Analyzes the newest OpenRGB log, or returns null when there is none.</summary>
    public static BackendFindings? ReadLatest(string? directory = null)
    {
        var dir = directory ?? DefaultLogDirectory;
        if (!Directory.Exists(dir))
        {
            return null;
        }

        var latest = new DirectoryInfo(dir).GetFiles("OpenRGB_*.log")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (latest is null)
        {
            return null;
        }

        try
        {
            // OpenRGB keeps the file open while it runs.
            using var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            return Analyze(lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static BackendFindings Analyze(IEnumerable<string> lines)
    {
        string? version = null;
        DateTime? started = null;
        var smbusFailed = false;
        var msi = BoardControllerIssue.None;
        var registered = new List<string>();

        foreach (var line in lines)
        {
            if (version is null && VersionLine().Match(line) is { Success: true } v)
            {
                version = v.Groups["version"].Value;
            }

            if (started is null && LaunchedLine().Match(line) is { Success: true } l &&
                DateTime.TryParseExact(l.Groups["stamp"].Value, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var when))
            {
                started = when;
            }

            if (line.Contains("Could not open PawnIO", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("I2C/SMBus interfaces failed to initialize", StringComparison.OrdinalIgnoreCase))
            {
                smbusFailed = true;
            }

            if (line.Contains("[MSIMotherboard", StringComparison.Ordinal) && line.Contains("firmware below minimum", StringComparison.OrdinalIgnoreCase))
            {
                msi = BoardControllerIssue.FirmwareTooOld;
            }
            else if (line.Contains("No matching driver found for MSI", StringComparison.OrdinalIgnoreCase) && msi == BoardControllerIssue.None)
            {
                msi = BoardControllerIssue.NoResponse;
            }
            else if (line.Contains("but does not have valid config", StringComparison.OrdinalIgnoreCase) && msi == BoardControllerIssue.None)
            {
                msi = BoardControllerIssue.NoBoardConfig;
            }

            if (Registered().Match(line) is { Success: true } r)
            {
                registered.Add(r.Groups["name"].Value.Trim());
            }
        }

        return new BackendFindings
        {
            Version = version,
            StartedAt = started,
            SmbusUnavailable = smbusFailed,
            MsiMysticLight = msi,
            RegisteredControllers = registered,
        };
    }
}
