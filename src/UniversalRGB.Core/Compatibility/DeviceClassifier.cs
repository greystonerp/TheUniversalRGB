using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Inventory;
using UniversalRGB.Core.Storage;

namespace UniversalRGB.Core.Compatibility;

public enum DeviceStatus
{
    Ready,
    DriverRequired,
    SetupRequired,
    ConflictDetected,
    Unsupported,
    Offline,
}

/// <summary>Another RGB application that is currently running.</summary>
public sealed record RunningConflict(string DisplayName, string ProcessName, IReadOnlyList<string> VendorIds)
{
    /// <summary>Apps such as SignalRGB that drive many vendors at once.</summary>
    public bool AffectsAllVendors => VendorIds.Count == 0;

    public bool Affects(string? vendorId) =>
        AffectsAllVendors || (vendorId is not null && VendorIds.Contains(vendorId, StringComparer.OrdinalIgnoreCase));
}

/// <summary>The facts the classifier needs about the machine and the backend.</summary>
public sealed record EnvironmentState
{
    public bool BackendConnected { get; init; }
    public string BackendName { get; init; } = "OpenRGB";
    public bool OpenRgbInstalled { get; init; }
    public bool PawnIoInstalled { get; init; }
    public IReadOnlyList<RunningConflict> Conflicts { get; init; } = [];

    /// <summary>What the backend's own log says about detection (SMBus access, skipped controllers).</summary>
    public BackendFindings? Findings { get; init; }

    /// <summary>Whether the backend process runs with administrator rights (null = unknown / not running).</summary>
    public bool? BackendElevated { get; init; }

    public bool SmbusBlocked => BackendConnected && Findings?.SmbusUnavailable == true;
}

/// <summary>One row of the unified device list.</summary>
public sealed record DeviceEntry
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public string Manufacturer { get; init; } = "";
    public string Connection { get; init; } = "";
    public string? HardwareKey { get; init; }
    public DeviceStatus Status { get; init; }
    public string StatusDetail { get; init; } = "";

    /// <summary>Suggested next step for the user, if any.</summary>
    public string? NextStep { get; init; }
    public RgbDevice? Controller { get; init; }
    public InventoryDevice? Inventory { get; init; }
    public CompatibilityRecord? Compatibility { get; init; }
    public int ZoneCount { get; init; }
    public int LedCount { get; init; }
    public string Backend { get; init; } = "—";
    public DateTimeOffset? LastSeen { get; init; }

    public bool IsControllable => Controller is not null && Status is DeviceStatus.Ready or DeviceStatus.ConflictDetected or DeviceStatus.SetupRequired;
}

/// <summary>
/// Turns raw facts (Windows inventory, backend controllers, environment) into the user-facing status list.
/// Pure function: no I/O, fully unit-testable.
/// </summary>
public static class DeviceClassifier
{
    private static readonly string[] GenericNames =
    [
        "HID-compliant", "USB Input Device", "USB Composite Device", "HID Keyboard Device", "HID-compliant mouse",
        "USB Root Hub", "Generic USB Hub", "USB Mass Storage", "HID-compliant consumer control device",
        "HID-compliant system controller", "HID-compliant vendor-defined device", "USB Serial Device",
    ];

    public static IReadOnlyList<DeviceEntry> Classify(
        HardwareInventory? inventory,
        IReadOnlyList<RgbDevice> controllers,
        EnvironmentState environment,
        IReadOnlyList<KnownDevice> knownDevices,
        Func<HardwareIdentity, CompatibilityRecord?> matchCompatibility)
    {
        var entries = new List<DeviceEntry>();
        var coveredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coveredTypes = new HashSet<RgbDeviceType>();

        // 1. Real controllers exposed by the backend.
        foreach (var controller in controllers)
        {
            if (controller.Identity?.Key is { } key)
            {
                coveredKeys.Add(key);
            }

            coveredTypes.Add(controller.Type);
            entries.Add(ClassifyController(controller, environment));
        }

        // 2. Components and recognized devices that the backend did not expose.
        if (inventory is not null)
        {
            var consumedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddMotherboard(entries, inventory, coveredTypes, coveredKeys, consumedKeys, environment);
            AddMemory(entries, inventory.System, coveredTypes, environment);
            AddGpus(entries, inventory.Devices, coveredTypes, environment);
            AddRecognizedUsb(entries, inventory.Devices, coveredKeys, consumedKeys, environment, matchCompatibility);
        }

        // 3. Previously seen controllers that are gone now.
        var present = controllers.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var known in knownDevices.Where(k => !present.Contains(k.Key)))
        {
            entries.Add(new DeviceEntry
            {
                Key = known.Key,
                Name = known.Name,
                Category = known.Type,
                Connection = known.HardwareKey is null ? "" : "USB/HID",
                HardwareKey = known.HardwareKey,
                Status = DeviceStatus.Offline,
                StatusDetail = environment.BackendConnected
                    ? $"Not reported by {known.Adapter} since {known.LastSeen.ToLocalTime():g}."
                    : $"{environment.BackendName} SDK server is not connected.",
                NextStep = environment.BackendConnected ? "Check the cable or hub, then rescan." : "Connect to the OpenRGB SDK server.",
                Backend = known.Adapter,
                LastSeen = known.LastSeen,
            });
        }

        return entries;
    }

    private static DeviceEntry ClassifyController(RgbDevice controller, EnvironmentState environment)
    {
        var caps = DeviceCapabilities.From(controller);
        var conflict = environment.Conflicts.FirstOrDefault(c => c.Affects(controller.Identity?.VendorId));
        var unconfigured = controller.Zones.Where(z => z.IsResizable && z.LedCount == 0).Select(z => z.Name).ToArray();

        DeviceStatus status;
        string detail;
        string? next = null;

        if (conflict is not null)
        {
            status = DeviceStatus.ConflictDetected;
            detail = $"{conflict.DisplayName} is running and may also control this device.";
            next = $"If lighting flickers or resets, close {conflict.DisplayName} (Universal RGB never closes it for you).";
        }
        else if (unconfigured.Length > 0 && caps.Strategy is ControlStrategy.None or ControlStrategy.HardwareOnly)
        {
            status = DeviceStatus.SetupRequired;
            detail = $"LED count not configured: {string.Join(", ", unconfigured)}.";
            next = "Open the device and set how many LEDs are connected to each header.";
        }
        else if (caps.Strategy == ControlStrategy.None)
        {
            status = DeviceStatus.Unsupported;
            detail = "The backend exposed this device without any controllable lighting mode.";
        }
        else
        {
            status = DeviceStatus.Ready;
            detail = caps.Strategy switch
            {
                ControlStrategy.Streaming => "Controllable · Direct mode (all effects)",
                ControlStrategy.StaticOnly => "Controllable · static colors and built-in effects",
                _ => "Controllable · built-in effects only",
            };
            if (unconfigured.Length > 0)
            {
                detail += $" · LED count not set for {string.Join(", ", unconfigured)}";
                next = "Set the LED count for unconfigured headers to light them.";
            }
        }

        return new DeviceEntry
        {
            Key = controller.Id,
            Name = controller.Name,
            Category = TypeName(controller.Type),
            Manufacturer = controller.Vendor,
            Connection = controller.Identity?.Bus ?? ConnectionFromLocation(controller.Location),
            HardwareKey = controller.Identity?.Key,
            Status = status,
            StatusDetail = detail,
            NextStep = next,
            Controller = controller,
            ZoneCount = controller.Zones.Count,
            LedCount = controller.LedCount,
            Backend = controller.AdapterName,
            LastSeen = DateTimeOffset.Now,
        };
    }

    private static void AddMotherboard(
        List<DeviceEntry> entries,
        HardwareInventory inventory,
        HashSet<RgbDeviceType> covered,
        HashSet<string> coveredKeys,
        HashSet<string> consumedKeys,
        EnvironmentState env)
    {
        var system = inventory.System;
        if (string.IsNullOrWhiteSpace(system.BoardProduct))
        {
            return;
        }

        // The board's own USB lighting controller (MSI Mystic Light, ASUS Aura, ...) is shown as part of the board.
        var controller = FindBoardController(system, inventory.Devices);
        var controllerKey = controller?.Identity?.Key;
        if (controllerKey is not null)
        {
            consumedKeys.Add(controllerKey);
        }

        if (covered.Contains(RgbDeviceType.Motherboard) || (controllerKey is not null && coveredKeys.Contains(controllerKey)))
        {
            return;
        }

        DeviceStatus status;
        string detail;
        string? next = null;
        if (!env.BackendConnected)
        {
            status = DeviceStatus.SetupRequired;
            detail = $"Start the {env.BackendName} SDK server to check onboard lighting and RGB headers.";
            next = "Drivers page → Start OpenRGB server.";
        }
        else if (controller is not null)
        {
            var label = $"{DisplayName(controller)}, USB {controllerKey}";
            var conflict = env.Conflicts.FirstOrDefault(c => c.Affects(controller.Identity?.VendorId));
            var issue = IsMsi(system) ? env.Findings?.MsiMysticLight ?? BoardControllerIssue.None : BoardControllerIssue.None;
            if (conflict is not null)
            {
                status = DeviceStatus.ConflictDetected;
                detail = $"{conflict.DisplayName} is running and is probably holding the board's lighting controller ({label}).";
                next = $"Close {conflict.DisplayName} (or remove its lighting component), then press Restart OpenRGB on the Drivers page.";
            }
            else if (issue == BoardControllerIssue.FirmwareTooOld)
            {
                status = DeviceStatus.Unsupported;
                detail = $"The board's lighting controller ({label}) runs firmware older than OpenRGB supports.";
                next = "Update the BIOS from the board maker's support page (it also updates the lighting controller), then rescan.";
            }
            else if (issue == BoardControllerIssue.NoResponse)
            {
                status = DeviceStatus.SetupRequired;
                detail = $"The board's lighting controller ({label}) is present but did not answer OpenRGB. This usually happens while the maker's RGB software (MSI Center / Mystic Light) is installed or running.";
                next = "Close MSI Center or remove its Mystic Light feature, then press Restart OpenRGB on the Drivers page.";
            }
            else if (issue == BoardControllerIssue.NoBoardConfig)
            {
                status = DeviceStatus.Unsupported;
                detail = $"OpenRGB found the board's lighting controller ({label}) but has no lighting layout for this board yet.";
            }
            else
            {
                status = DeviceStatus.SetupRequired;
                detail = $"The board's lighting controller ({label}) is present, but OpenRGB did not expose it.";
                next = "Press Restart OpenRGB on the Drivers page. If it stays missing, make sure no other RGB app is using the board.";
            }
        }
        else if (env.SmbusBlocked)
        {
            status = DeviceStatus.SetupRequired;
            detail = "No USB lighting controller was found, and OpenRGB could not reach SMBus (it is not running as administrator), where this board's RGB may be.";
            next = "Drivers page → Run OpenRGB as administrator.";
        }
        else if (!env.PawnIoInstalled)
        {
            status = DeviceStatus.Unsupported;
            detail = "No onboard RGB controller was exposed. Boards whose RGB is on SMBus also need the PawnIO driver.";
            next = "If this board has RGB, see Drivers → PawnIO.";
        }
        else
        {
            status = DeviceStatus.Unsupported;
            detail = "No onboard RGB controller was exposed for this board.";
        }

        entries.Add(new DeviceEntry
        {
            Key = "board:" + system.BoardProduct,
            Name = $"{system.BoardManufacturer} {system.BoardProduct}".Trim(),
            Category = "Motherboard",
            Manufacturer = system.BoardManufacturer ?? "",
            Connection = controllerKey is null ? "Onboard" : $"Onboard · USB {controllerKey}",
            HardwareKey = controllerKey,
            Status = status,
            StatusDetail = detail + (string.IsNullOrWhiteSpace(system.BoardVersion) ? "" : $" Revision: {system.BoardVersion}."),
            NextStep = next,
            Inventory = controller,
        });
    }

    /// <summary>Finds the board maker's USB lighting controller (MSI Mystic Light, ASUS Aura, Gigabyte RGB Fusion 2, ASRock Polychrome).</summary>
    internal static InventoryDevice? FindBoardController(SystemSummary system, IReadOnlyList<InventoryDevice> devices)
    {
        var maker = system.BoardManufacturer ?? "";
        var usb = devices.Where(d => d.Category is InventoryCategory.Usb or InventoryCategory.Hid && d.Identity?.VendorId is not null).ToList();

        if (IsMsi(system))
        {
            var code = MsiBoardCode(system.BoardProduct);
            return usb.FirstOrDefault(d => d.Identity!.VendorId == "1462" && code is not null && d.Identity.ProductId == code)
                ?? usb.FirstOrDefault(d => d.Identity!.VendorId == "1462" && NameContains(d, "MYSTIC LIGHT"));
        }

        if (maker.Contains("ASUS", StringComparison.OrdinalIgnoreCase))
        {
            return usb.FirstOrDefault(d => d.Identity!.VendorId == "0B05" && NameContains(d, "AURA"));
        }

        if (maker.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase))
        {
            return usb.FirstOrDefault(d => d.Identity!.VendorId == "048D" && d.Identity.ProductId is "5702" or "8297" or "5711");
        }

        if (maker.Contains("ASRock", StringComparison.OrdinalIgnoreCase))
        {
            return usb.FirstOrDefault(d => d.Identity!.VendorId == "26CE");
        }

        return null;
    }

    private static bool IsMsi(SystemSummary system) =>
        (system.BoardManufacturer ?? "").Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) ||
        (system.BoardManufacturer ?? "").StartsWith("MSI", StringComparison.OrdinalIgnoreCase);

    /// <summary>"MPG B550 GAMING PLUS (MS-7C56)" → "7C56" (MSI uses the board code as the Mystic Light USB product ID).</summary>
    internal static string? MsiBoardCode(string? product)
    {
        var match = System.Text.RegularExpressions.Regex.Match(product ?? "", @"MS-([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static bool NameContains(InventoryDevice d, string text) =>
        d.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        (d.BusReportedName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);

    private static string DisplayName(InventoryDevice d) =>
        !string.IsNullOrWhiteSpace(d.BusReportedName) && !IsGeneric(d.BusReportedName) ? d.BusReportedName.Trim()
        : !IsGeneric(d.Name) ? d.Name.Trim()
        : "lighting controller";

    private static void AddMemory(List<DeviceEntry> entries, SystemSummary system, HashSet<RgbDeviceType> covered, EnvironmentState env)
    {
        if (system.Memory.Count == 0 || covered.Contains(RgbDeviceType.Dram))
        {
            return;
        }

        foreach (var group in system.Memory.GroupBy(m => $"{m.Manufacturer}|{m.PartNumber}"))
        {
            var first = group.First();
            var totalGb = group.Sum(m => (double)m.CapacityBytes) / (1024d * 1024 * 1024);
            var rgbLine = MemoryRgbHints.Describe(first.PartNumber);
            var looksRgb = rgbLine is null ? "" : $"These look like RGB modules ({rgbLine}). ";
            DeviceStatus status;
            string detail;
            string? next = null;
            if (!env.BackendConnected)
            {
                status = DeviceStatus.SetupRequired;
                detail = looksRgb + $"Start the {env.BackendName} SDK server to check for RGB memory.";
                next = "Drivers page → Start OpenRGB server.";
            }
            else if (!env.PawnIoInstalled)
            {
                status = DeviceStatus.DriverRequired;
                detail = looksRgb + "RGB memory is controlled over SMBus, which needs the PawnIO driver and OpenRGB running as administrator."
                    + (rgbLine is null ? " Only relevant if these modules have RGB lighting." : "");
                next = "Drivers page → Install PawnIO.";
            }
            else if (env.SmbusBlocked)
            {
                status = DeviceStatus.SetupRequired;
                detail = looksRgb + "OpenRGB could not open SMBus because it is not running as administrator, so it could not check these modules.";
                next = "Drivers page → Run OpenRGB as administrator.";
            }
            else
            {
                status = DeviceStatus.Unsupported;
                detail = rgbLine is null
                    ? "No RGB controller was detected on these modules (they may not have RGB)."
                    : looksRgb + "OpenRGB checked SMBus but did not find their RGB controller.";
            }

            entries.Add(new DeviceEntry
            {
                Key = "dram:" + group.Key,
                Name = $"{first.Manufacturer} {first.PartNumber}".Trim() is { Length: > 0 } n ? n : "System memory",
                Category = "DRAM",
                Manufacturer = first.Manufacturer ?? "",
                Connection = "SMBus",
                Status = status,
                StatusDetail = $"{group.Count()} module(s), {totalGb:0.#} GB. {detail}",
                NextStep = next,
            });
        }
    }

    private static void AddGpus(List<DeviceEntry> entries, IReadOnlyList<InventoryDevice> devices, HashSet<RgbDeviceType> covered, EnvironmentState env)
    {
        if (covered.Contains(RgbDeviceType.Gpu))
        {
            return;
        }

        foreach (var gpu in devices.Where(d => d.Category == InventoryCategory.Gpu))
        {
            var (status, detail) = !env.BackendConnected
                ? (DeviceStatus.SetupRequired, $"Start the {env.BackendName} SDK server to check GPU lighting.")
                : (DeviceStatus.Unsupported, "No controllable RGB zones were detected on this graphics card."
                    + (env.SmbusBlocked ? " Some cards are only reachable when OpenRGB runs as administrator." : ""));

            if (gpu.ProblemCode != 0 && !ProblemCodes.IsBenign(gpu.ProblemCode))
            {
                status = DeviceStatus.DriverRequired;
                detail = ProblemCodes.Describe(gpu.ProblemCode) + ". Install the graphics driver from the GPU vendor.";
            }

            entries.Add(new DeviceEntry
            {
                Key = "gpu:" + gpu.InstanceId,
                Name = gpu.Name,
                Category = "GPU",
                Manufacturer = gpu.Manufacturer ?? "",
                Connection = "PCIe",
                HardwareKey = gpu.Identity?.Key,
                Status = status,
                StatusDetail = detail,
                Inventory = gpu,
            });
        }
    }

    private static void AddRecognizedUsb(
        List<DeviceEntry> entries,
        IReadOnlyList<InventoryDevice> devices,
        HashSet<string> coveredKeys,
        HashSet<string> consumedKeys,
        EnvironmentState env,
        Func<HardwareIdentity, CompatibilityRecord?> match)
    {
        var groups = devices
            .Where(d => d.Category is InventoryCategory.Usb or InventoryCategory.Hid && d.Identity?.Key is not null)
            .GroupBy(d => d.Identity!.Key!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            if (coveredKeys.Contains(group.Key) || consumedKeys.Contains(group.Key))
            {
                continue;
            }

            var identity = group.First().Identity!;
            var record = match(identity);
            if (record is null)
            {
                continue;
            }

            var faulty = group.FirstOrDefault(d => d.ProblemCode != 0 && !ProblemCodes.IsBenign(d.ProblemCode));
            var conflict = env.Conflicts.FirstOrDefault(c => c.Affects(identity.VendorId));

            DeviceStatus status;
            string detail;
            string? next = null;
            if (faulty is not null)
            {
                status = DeviceStatus.DriverRequired;
                detail = ProblemCodes.Describe(faulty.ProblemCode) + ".";
                next = ProblemCodes.IsDriverProblem(faulty.ProblemCode)
                    ? "Install the manufacturer's driver for this device (Drivers page)."
                    : "Open Device Manager for details.";
            }
            else if (conflict is not null)
            {
                status = DeviceStatus.ConflictDetected;
                detail = $"{conflict.DisplayName} is running and may be holding this device.";
                next = $"Close {conflict.DisplayName} yourself, then rescan, if you want OpenRGB to control it.";
            }
            else if (!env.BackendConnected)
            {
                status = DeviceStatus.SetupRequired;
                detail = $"Recognized {record.Manufacturer} hardware. Start the {env.BackendName} SDK server to check for controllable lighting.";
                next = "Drivers page → Start OpenRGB server.";
            }
            else
            {
                status = DeviceStatus.Unsupported;
                detail = $"Recognized {record.Manufacturer} hardware, but no RGB adapter exposed it. It may have no software-controllable lighting.";
            }

            entries.Add(new DeviceEntry
            {
                Key = "usb:" + group.Key,
                Name = BestName(group, record),
                Category = record.Category,
                Manufacturer = record.Manufacturer,
                Connection = identity.Bus,
                HardwareKey = group.Key,
                Status = status,
                StatusDetail = detail,
                NextStep = next,
                Inventory = group.First(),
                Compatibility = record,
            });
        }
    }

    private static string BestName(IEnumerable<InventoryDevice> group, CompatibilityRecord record)
    {
        foreach (var device in group)
        {
            if (!string.IsNullOrWhiteSpace(device.BusReportedName) && !IsGeneric(device.BusReportedName))
            {
                return device.BusReportedName.Trim();
            }
        }

        foreach (var device in group)
        {
            if (!IsGeneric(device.Name))
            {
                return device.Name.Trim();
            }
        }

        return $"{record.Manufacturer} device ({group.First().Identity?.Key})";
    }

    private static bool IsGeneric(string name) =>
        GenericNames.Any(g => name.StartsWith(g, StringComparison.OrdinalIgnoreCase));

    private static string ConnectionFromLocation(string location)
    {
        if (location.StartsWith("HID", StringComparison.OrdinalIgnoreCase))
        {
            return "HID";
        }

        if (location.StartsWith("I2C", StringComparison.OrdinalIgnoreCase))
        {
            return "SMBus/I2C";
        }

        return location.Split(':', 2)[0];
    }

    public static string TypeName(RgbDeviceType type) => type switch
    {
        RgbDeviceType.Dram => "DRAM",
        RgbDeviceType.Gpu => "GPU",
        RgbDeviceType.LedStrip => "LED Strip",
        RgbDeviceType.HeadsetStand => "Headset Stand",
        _ => type.ToString(),
    };
}
