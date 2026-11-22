using System.Runtime.InteropServices;

namespace UniversalRGB.Hardware.WindowsPnP;

internal static class NativeMethods
{
    public const int DIGCF_PRESENT = 0x00000002;
    public const int DIGCF_ALLCLASSES = 0x00000004;

    public const int SPDRP_DEVICEDESC = 0x00000000;
    public const int SPDRP_HARDWAREID = 0x00000001;
    public const int SPDRP_SERVICE = 0x00000004;
    public const int SPDRP_CLASS = 0x00000007;
    public const int SPDRP_MFG = 0x0000000B;
    public const int SPDRP_FRIENDLYNAME = 0x0000000C;

    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_NO_MORE_ITEMS = 259;

    public const uint DN_HAS_PROBLEM = 0x00000400;
    public const int CR_SUCCESS = 0;

    public const uint DEVPROP_TYPE_STRING = 0x00000012;

    public static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>DEVPKEY_Device_BusReportedDeviceDesc — the product string the device reports about itself.</summary>
    public static DEVPROPKEY BusReportedDeviceDesc => new()
    {
        fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"),
        pid = 4,
    };

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    public static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, int memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        int property,
        out int propertyRegDataType,
        byte[]? propertyBuffer,
        int propertyBufferSize,
        out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        char[] deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDevicePropertyW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceProperty(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref DEVPROPKEY propertyKey,
        out uint propertyType,
        byte[]? propertyBuffer,
        int propertyBufferSize,
        out int requiredSize,
        int flags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, int devInst, int flags);
}
