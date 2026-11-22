using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Inventory;
using static UniversalRGB.Hardware.WindowsPnP.NativeMethods;

namespace UniversalRGB.Hardware.WindowsPnP;

/// <summary>Read-only enumeration of present Plug and Play devices through SetupAPI and Configuration Manager.</summary>
public static class PnpEnumerator
{
    /// <summary>Enumerates present USB, HID and display (GPU) devices.</summary>
    public static IReadOnlyList<InventoryDevice> EnumeratePresent(bool includeAll = false)
    {
        var set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == InvalidHandle || set == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed.");
        }

        var devices = new List<InventoryDevice>();
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (var index = 0; SetupDiEnumDeviceInfo(set, index, ref data); index++)
            {
                var hardwareIds = GetMultiString(set, ref data, SPDRP_HARDWAREID);
                var className = GetString(set, ref data, SPDRP_CLASS) ?? "";
                var category = Categorize(hardwareIds, className);
                if (category == InventoryCategory.Other && !includeAll)
                {
                    continue;
                }

                var instanceId = GetInstanceId(set, ref data) ?? $"dev{index}";
                var name = GetString(set, ref data, SPDRP_FRIENDLYNAME) ?? GetString(set, ref data, SPDRP_DEVICEDESC) ?? instanceId;
                devices.Add(new InventoryDevice
                {
                    InstanceId = instanceId,
                    Name = name,
                    BusReportedName = GetBusReportedName(set, ref data),
                    Manufacturer = GetString(set, ref data, SPDRP_MFG),
                    ClassName = className,
                    Service = GetString(set, ref data, SPDRP_SERVICE),
                    HardwareIds = hardwareIds,
                    ProblemCode = GetProblemCode(data.DevInst),
                    Identity = HardwareIdParser.ParseBest(hardwareIds.Append(instanceId)),
                    Category = category,
                });
            }

            var error = Marshal.GetLastWin32Error();
            if (error != 0 && error != ERROR_NO_MORE_ITEMS)
            {
                throw new Win32Exception(error, "SetupDiEnumDeviceInfo failed.");
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return devices;
    }

    internal static InventoryCategory Categorize(IReadOnlyList<string> hardwareIds, string className)
    {
        var first = hardwareIds.Count > 0 ? hardwareIds[0] : "";
        if (className.Equals("Display", StringComparison.OrdinalIgnoreCase) && first.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
        {
            return InventoryCategory.Gpu;
        }

        if (first.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
        {
            return first.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase) ? InventoryCategory.Other : InventoryCategory.Usb;
        }

        if (first.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) && first.Contains("VID_", StringComparison.OrdinalIgnoreCase))
        {
            return InventoryCategory.Hid;
        }

        return InventoryCategory.Other;
    }

    private static int GetProblemCode(int devInst)
    {
        if (CM_Get_DevNode_Status(out var status, out var problem, devInst, 0) != CR_SUCCESS)
        {
            return 0;
        }

        return (status & DN_HAS_PROBLEM) != 0 ? (int)problem : 0;
    }

    private static string? GetInstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var buffer = new char[512];
        if (SetupDiGetDeviceInstanceId(set, ref data, buffer, buffer.Length, out var required))
        {
            return new string(buffer, 0, Math.Max(0, required - 1)).TrimEnd('\0');
        }

        if (Marshal.GetLastWin32Error() == ERROR_INSUFFICIENT_BUFFER && required > 0)
        {
            buffer = new char[required];
            if (SetupDiGetDeviceInstanceId(set, ref data, buffer, buffer.Length, out required))
            {
                return new string(buffer).TrimEnd('\0');
            }
        }

        return null;
    }

    private static byte[]? GetRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, int property)
    {
        SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, null, 0, out var required);
        if (required <= 0)
        {
            return null;
        }

        var buffer = new byte[required];
        return SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, buffer, buffer.Length, out _) ? buffer : null;
    }

    private static string? GetString(IntPtr set, ref SP_DEVINFO_DATA data, int property)
    {
        var bytes = GetRegistryProperty(set, ref data, property);
        if (bytes is null)
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }

    private static string[] GetMultiString(IntPtr set, ref SP_DEVINFO_DATA data, int property)
    {
        var bytes = GetRegistryProperty(set, ref data, property);
        return bytes is null
            ? []
            : Encoding.Unicode.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? GetBusReportedName(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var key = BusReportedDeviceDesc;
        SetupDiGetDeviceProperty(set, ref data, ref key, out _, null, 0, out var required, 0);
        if (required <= 0)
        {
            return null;
        }

        var buffer = new byte[required];
        if (!SetupDiGetDeviceProperty(set, ref data, ref key, out var type, buffer, buffer.Length, out _, 0) || type != DEVPROP_TYPE_STRING)
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }
}
