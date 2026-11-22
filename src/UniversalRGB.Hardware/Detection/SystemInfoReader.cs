using System.Management;
using Microsoft.Win32;
using UniversalRGB.Core.Inventory;

namespace UniversalRGB.Hardware.Detection;

/// <summary>Reads motherboard identity (SMBIOS via registry) and memory modules (WMI).</summary>
public static class SystemInfoReader
{
    public static SystemSummary Read(ICollection<string> warnings)
    {
        string? boardMfg = null, boardProduct = null, boardVersion = null, bios = null, sysMfg = null, sysProduct = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            boardMfg = Clean(key?.GetValue("BaseBoardManufacturer") as string);
            boardProduct = Clean(key?.GetValue("BaseBoardProduct") as string);
            boardVersion = Clean(key?.GetValue("BaseBoardVersion") as string);
            bios = Clean(key?.GetValue("BIOSVersion") as string);
            sysMfg = Clean(key?.GetValue("SystemManufacturer") as string);
            sysProduct = Clean(key?.GetValue("SystemProductName") as string);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            warnings.Add($"Could not read motherboard information: {ex.Message}");
        }

        return new SystemSummary
        {
            BoardManufacturer = boardMfg,
            BoardProduct = boardProduct,
            BoardVersion = boardVersion,
            BiosVersion = bios,
            SystemManufacturer = sysMfg,
            SystemProduct = sysProduct,
            Memory = ReadMemory(warnings),
        };
    }

    private static List<MemoryModule> ReadMemory(ICollection<string> warnings)
    {
        var modules = new List<MemoryModule>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, PartNumber, Capacity, DeviceLocator, Speed FROM Win32_PhysicalMemory");
            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    modules.Add(new MemoryModule(
                        Clean(obj["Manufacturer"] as string),
                        Clean(obj["PartNumber"] as string),
                        obj["Capacity"] is ulong capacity ? capacity : 0,
                        Clean(obj["DeviceLocator"] as string),
                        obj["Speed"] is uint speed ? speed : 0));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            warnings.Add($"Could not read memory modules: {ex.Message}");
        }

        return modules;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Default string", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }
}
