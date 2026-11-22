using System.Diagnostics;
using UniversalRGB.Core.Compatibility;

namespace UniversalRGB.Drivers.Detection;

/// <summary>
/// Detects other RGB applications that may fight over the same devices. Detection only — Universal RGB never
/// terminates another program; it tells the user so they can decide.
/// </summary>
public static class ConflictDetector
{
    /// <param name="OnlyWithFeature">When set, a running process only counts if one of these folders exists, i.e. the
    /// app's lighting feature is installed (MSI Center's core services start with Windows even without Mystic Light).</param>
    private sealed record KnownApp(string DisplayName, string[] ProcessNames, string[] VendorIds, string[]? OnlyWithFeature = null);

    private static readonly string[] MysticLightFolders =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MSI", "MSI Center", "Mystic Light"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MSI", "MSI Center", "Mystic Light"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MSI", "One Dragon Center", "Mystic_Light"),
    ];

    private static readonly KnownApp[] KnownApps =
    [
        new("ASUS Armoury Crate", ["ArmouryCrate", "ArmouryCrate.Service", "ArmourySocketServer"], ["0B05"]),
        new("ASUS Aura lighting service", ["LightingService"], ["0B05"]),
        new("Razer Synapse", ["Razer Synapse 3", "Razer Synapse Service", "RazerAppEngine", "Razer Synapse"], ["1532"]),
        new("Razer Chroma SDK", ["RzChromaSDKService", "RzChromaStreamServer"], ["1532"]),
        new("Corsair iCUE", ["iCUE", "Corsair.Service"], ["1B1C"]),
        new("Logitech G HUB", ["lghub", "lghub_agent", "lghub_system_tray"], ["046D"]),
        new("SteelSeries GG", ["SteelSeriesGG", "SteelSeriesEngine", "SteelSeriesEngine3"], ["1038"]),
        new("NZXT CAM", ["NZXT CAM"], ["1E71"]),
        // Mystic Light's own processes (MSI Center feature, Dragon Center or the standalone app).
        new("MSI Mystic Light", ["LEDKeeper2", "LEDKeeper", "LightKeeperService", "MysticLight2_Service", "Mystic_Light_Service", "MysticLight", "MysticLight3"], ["1462"]),
        // MSI Center itself only drives the lights when its Mystic Light feature is installed.
        new("MSI Center with Mystic Light", ["MSI Center", "MSI.CentralServer", "MSI_Central_Service"], ["1462"], MysticLightFolders),
        new("Gigabyte RGB Fusion", ["RGBFusion", "RGBFusion2"], ["048D"]),
        new("Lian Li L-Connect", ["L-Connect 3", "LConnect3"], ["0CF2"]),
        new("Thermaltake TT RGB Plus", ["TT RGB Plus", "TTRGBPlus"], ["264A"]),
        new("Cooler Master MasterPlus+", ["MasterPlus", "MasterPlus+"], ["2516"]),
        new("SignalRGB", ["SignalRgb", "SignalRGB", "SignalRgbLauncher"], []),
    ];

    public static IReadOnlyList<RunningConflict> Detect()
    {
        HashSet<string> running;
        try
        {
            running = Process.GetProcesses()
                .Select(p =>
                {
                    using (p)
                    {
                        return p.ProcessName;
                    }
                })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return [];
        }

        return Match(running);
    }

    internal static IReadOnlyList<RunningConflict> Match(IReadOnlySet<string> runningProcessNames, Func<string, bool>? directoryExists = null)
    {
        directoryExists ??= Directory.Exists;
        var result = new List<RunningConflict>();
        foreach (var app in KnownApps)
        {
            var hit = app.ProcessNames.FirstOrDefault(runningProcessNames.Contains);
            if (hit is not null && (app.OnlyWithFeature is null || app.OnlyWithFeature.Any(directoryExists)))
            {
                result.Add(new RunningConflict(app.DisplayName, hit, app.VendorIds));
            }
        }

        return result;
    }
}
