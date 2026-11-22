using UniversalRGB.Core.Inventory;
using UniversalRGB.Hardware.WindowsPnP;

namespace UniversalRGB.Hardware.Detection;

/// <summary>Builds a read-only snapshot of the hardware relevant to RGB control.</summary>
public sealed class HardwareInventoryService
{
    public Task<HardwareInventory> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(Scan, cancellationToken);

    public HardwareInventory Scan()
    {
        var warnings = new List<string>();
        IReadOnlyList<InventoryDevice> devices = [];
        try
        {
            devices = PnpEnumerator.EnumeratePresent();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            warnings.Add($"Windows device enumeration failed: {ex.Message}");
        }

        var system = SystemInfoReader.Read(warnings);
        return new HardwareInventory
        {
            System = system,
            Devices = devices,
            ScannedAt = DateTimeOffset.Now,
            Warnings = warnings,
        };
    }
}
