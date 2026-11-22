using System.Security.Principal;
using Microsoft.Win32;
using UniversalRGB.Adapters.OpenRGB;
using UniversalRGB.Core.Compatibility;
using UniversalRGB.Core.Inventory;
using UniversalRGB.Core.Storage;

namespace UniversalRGB.Drivers.Detection;

public enum RequirementState
{
    Ok,
    Missing,
    Warning,
    Info,
}

public sealed record RequirementStatus(string Id, string Title, RequirementState State, string Detail, string? Hint = null);

public sealed record DependencyReport
{
    public string? OpenRgbPath { get; init; }
    public bool OpenRgbRunning { get; init; }
    public bool ServerReachable { get; init; }
    public bool PawnIoInstalled { get; init; }
    public bool IsElevated { get; init; }
    public IReadOnlyList<RunningConflict> Conflicts { get; init; } = [];
    public IReadOnlyList<InventoryDevice> ProblemDevices { get; init; } = [];
    public IReadOnlyList<RequirementStatus> Items { get; init; } = [];
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>Diagnoses what is missing before anything is installed. Read-only.</summary>
public static class DependencyChecker
{
    public static async Task<DependencyReport> CheckAsync(
        AppSettings settings,
        HardwareInventory? inventory,
        BackendFindings? findings = null,
        bool? openRgbElevated = null,
        bool backendConnected = false,
        CancellationToken cancellationToken = default)
    {
        var openRgbPath = await Task.Run(() => OpenRgbInstallation.FindExecutable(settings.OpenRgbExecutablePath), cancellationToken).ConfigureAwait(false);
        var running = OpenRgbInstallation.IsProcessRunning();

        // Probing an already-connected server only adds noise to OpenRGB's log.
        var reachable = backendConnected || await OpenRgbInstallation.IsServerReachableAsync(settings.OpenRgbHost, settings.OpenRgbPort, TimeSpan.FromSeconds(1), cancellationToken)
            .ConfigureAwait(false);
        var pawnIo = IsPawnIoInstalled();
        var elevated = IsElevated();
        var smbusBlocked = running && findings?.SmbusUnavailable == true;
        var conflicts = ConflictDetector.Detect();
        var problems = inventory?.Devices
            .Where(d => d.ProblemCode != 0 && !ProblemCodes.IsBenign(d.ProblemCode))
            .ToList() ?? [];

        var items = new List<RequirementStatus>
        {
            openRgbPath is not null
                ? new("openrgb", "OpenRGB", RequirementState.Ok, $"Found at {openRgbPath}.")
                : new("openrgb", "OpenRGB", RequirementState.Missing,
                    "OpenRGB is not installed. It is the backend that talks to most RGB controllers.",
                    settings.AutoInstallRequirements
                        ? "Universal RGB installs the official release for you (SHA-256 verified, no administrator rights)."
                        : "Press Install OpenRGB to download the official release (SHA-256 verified, no administrator rights)."),

            reachable
                ? new("openrgb-sdk-server", "OpenRGB SDK server", RequirementState.Ok, $"Listening on {settings.OpenRgbHost}:{settings.OpenRgbPort}.")
                : new("openrgb-sdk-server", "OpenRGB SDK server", RequirementState.Missing,
                    running
                        ? "OpenRGB is open but its SDK server is not reachable."
                        : "The SDK server is not running.",
                    running
                        ? "In the OpenRGB window open SDK Server and press Start Server (Universal RGB does not close your OpenRGB window)."
                        : settings.AutoInstallRequirements
                            ? "Universal RGB starts it in the background automatically."
                            : "Press Start OpenRGB server to run it in the background."),

            pawnIo
                ? new("pawnio", "PawnIO SMBus driver", RequirementState.Ok, "Installed. OpenRGB can reach SMBus devices when run as administrator.")
                : new("pawnio", "PawnIO SMBus driver", RequirementState.Warning,
                    "Not installed. Only needed for SMBus/I2C lighting: RGB memory, some motherboards and some graphics cards.",
                    "Install PawnIO downloads the signed official installer and asks for your approval (kernel driver, needs administrator rights)."),

            AdministratorItem(pawnIo, smbusBlocked, openRgbElevated, settings.LaunchOpenRgbElevated, elevated),

            conflicts.Count == 0
                ? new("conflicts", "Other RGB software", RequirementState.Ok, "No known conflicting RGB applications are running.")
                : new("conflicts", "Other RGB software", RequirementState.Warning,
                    $"Running: {string.Join(", ", conflicts.Select(c => c.DisplayName))}. They may override or lock lighting.",
                    "Close them yourself if lighting flickers. Universal RGB never terminates other programs."),

            problems.Count == 0
                ? new("windows-drivers", "Windows device drivers", RequirementState.Ok, "No USB, HID or GPU devices report driver problems.")
                : new("windows-drivers", "Windows device drivers", RequirementState.Warning,
                    $"{problems.Count} device(s) report problems: " + string.Join("; ", problems.Take(4).Select(p => $"{p.Name} — {ProblemCodes.Describe(p.ProblemCode)}")),
                    "Install the device manufacturer's driver, or open Device Manager for details."),
        };

        return new DependencyReport
        {
            OpenRgbPath = openRgbPath,
            OpenRgbRunning = running,
            ServerReachable = reachable,
            PawnIoInstalled = pawnIo,
            IsElevated = elevated,
            Conflicts = conflicts,
            ProblemDevices = problems,
            Items = items,
        };
    }

    private static RequirementStatus AdministratorItem(bool pawnIo, bool smbusBlocked, bool? openRgbElevated, bool elevatedSetting, bool appElevated)
    {
        const string Title = "OpenRGB administrator rights";
        if (smbusBlocked && pawnIo && openRgbElevated == true)
        {
            return new("administrator", Title, RequirementState.Warning,
                "OpenRGB runs as administrator but still could not open PawnIO, so SMBus lighting (RGB memory) is unavailable.",
                "Reinstall PawnIO from this page, restart Windows, then press Restart OpenRGB.");
        }

        if (smbusBlocked && pawnIo)
        {
            return new("administrator", Title, RequirementState.Warning,
                "OpenRGB is running without administrator rights, so it cannot use PawnIO. RGB memory and other SMBus lighting stay hidden.",
                "Press Run OpenRGB as administrator. Windows asks once each time OpenRGB starts; Universal RGB itself stays a normal app.");
        }

        if (openRgbElevated == true || elevatedSetting)
        {
            return new("administrator", Title, RequirementState.Ok, "OpenRGB is started as administrator, so SMBus devices such as RGB memory are reachable.");
        }

        return new("administrator", Title, RequirementState.Info,
            appElevated
                ? "Universal RGB is running as administrator."
                : "OpenRGB runs without administrator rights. That is enough for USB devices; SMBus devices (RGB memory) need administrator rights and PawnIO.");
    }

    public static bool IsPawnIoInstalled()
    {
        try
        {
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            if (service is not null)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }

        return Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO"));
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
