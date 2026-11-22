using System.Diagnostics;
using UniversalRGB.Core.Compatibility;

namespace UniversalRGB.Drivers.Detection;

/// <summary>
/// Detects other RGB applications that may fight over the same devices. Detection only — Universal RGB never
/// terminates another program; it tells the user so they can decide.
/// </summary>
public static class ConflictDetector
{
    private sealed record KnownApp(string DisplayName, string[] ProcessNames, string[] VendorIds);

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
        new("MSI Center / Mystic Light", ["MSI Center", "MSI.CentralServer", "MSI.TerminalServer", "MSI.NotifyServer", "MSI_Central_Service",
            "Mystic_Light_Service", "MysticLight", "LEDKeeper2", "LightKeeperService"], ["1462"]),
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

    internal static IReadOnlyList<RunningConflict> Match(IReadOnlySet<string> runningProcessNames)
    {
        var result = new List<RunningConflict>();
        foreach (var app in KnownApps)
        {
            var hit = app.ProcessNames.FirstOrDefault(runningProcessNames.Contains);
            if (hit is not null)
            {
                result.Add(new RunningConflict(app.DisplayName, hit, app.VendorIds));
            }
        }

        return result;
    }
}
