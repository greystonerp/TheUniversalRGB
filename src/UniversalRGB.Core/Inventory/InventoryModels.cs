using UniversalRGB.Core.Devices;

namespace UniversalRGB.Core.Inventory;

public enum InventoryCategory
{
    Usb,
    Hid,
    Gpu,
    Other,
}

/// <summary>A device present in the Windows Plug and Play tree.</summary>
public sealed record InventoryDevice
{
    public required string InstanceId { get; init; }
    public required string Name { get; init; }

    /// <summary>Product string reported by the device itself (often the real model name).</summary>
    public string? BusReportedName { get; init; }
    public string? Manufacturer { get; init; }
    public string ClassName { get; init; } = "";
    public string? Service { get; init; }
    public IReadOnlyList<string> HardwareIds { get; init; } = [];

    /// <summary>Windows Configuration Manager problem code (0 = working).</summary>
    public int ProblemCode { get; init; }
    public HardwareIdentity? Identity { get; init; }
    public InventoryCategory Category { get; init; } = InventoryCategory.Other;
}

public sealed record MemoryModule(string? Manufacturer, string? PartNumber, ulong CapacityBytes, string? Slot, uint SpeedMts);

public sealed record SystemSummary
{
    public string? BoardManufacturer { get; init; }
    public string? BoardProduct { get; init; }
    public string? BoardVersion { get; init; }
    public string? BiosVersion { get; init; }
    public string? SystemManufacturer { get; init; }
    public string? SystemProduct { get; init; }
    public IReadOnlyList<MemoryModule> Memory { get; init; } = [];
}

public sealed record HardwareInventory
{
    public required SystemSummary System { get; init; }
    public IReadOnlyList<InventoryDevice> Devices { get; init; } = [];
    public DateTimeOffset ScannedAt { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static HardwareInventory Empty { get; } = new() { System = new SystemSummary() };
}

/// <summary>Descriptions of Windows Configuration Manager (Device Manager) problem codes.</summary>
public static class ProblemCodes
{
    public static string Describe(int code) => code switch
    {
        0 => "Working properly",
        1 => "Device is not configured correctly (code 1)",
        3 => "Driver may be corrupted or the system is low on memory (code 3)",
        10 => "Device cannot start (code 10)",
        12 => "Not enough free resources (code 12)",
        14 => "Restart required before the device works (code 14)",
        18 => "Drivers need to be reinstalled (code 18)",
        19 => "Configuration in the registry is incomplete or damaged (code 19)",
        21 => "Windows is removing this device (code 21)",
        22 => "Device is disabled (code 22)",
        24 => "Device is not present or not all drivers are installed (code 24)",
        28 => "Drivers are not installed (code 28)",
        31 => "Windows cannot load the required drivers (code 31)",
        32 => "A required driver service is disabled (code 32)",
        37 => "Driver returned a failure while initializing (code 37)",
        39 => "Driver is corrupted or missing (code 39)",
        43 => "Device reported a problem and was stopped (code 43)",
        45 => "Device is not currently connected (code 45)",
        52 => "Driver signature cannot be verified (code 52)",
        _ => $"Windows reports problem code {code}",
    };

    /// <summary>Codes where installing or reinstalling a driver is the expected fix.</summary>
    public static bool IsDriverProblem(int code) => code is 18 or 28 or 31 or 32 or 37 or 39 or 52;

    /// <summary>Codes that mean "not connected / being removed" rather than a fault.</summary>
    public static bool IsBenign(int code) => code is 0 or 21 or 24 or 45;
}
