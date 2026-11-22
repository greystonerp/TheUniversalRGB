namespace UniversalRGB.Adapters.OpenRGB;

/// <summary>
/// OpenRGB SDK network protocol constants (see OpenRGB NetworkProtocol.h).
/// Every packet: 16-byte header { "ORGB", uint dev_idx, uint pkt_id, uint pkt_size } + pkt_size bytes of little-endian data.
/// </summary>
public static class OpenRgbProtocol
{
    public const int DefaultPort = 6742;
    public const int HeaderSize = 16;
    public const int MaxPacketSize = 8 * 1024 * 1024;

    /// <summary>
    /// Highest protocol version this client speaks. Version 4 is supported by OpenRGB 0.9 and later.
    /// Servers newer than this downgrade the data format for us; with versions &lt; 6 devices are addressed by index.
    /// </summary>
    public const uint ClientProtocolVersion = 4;

    public static ReadOnlySpan<byte> Magic => "ORGB"u8;

    public const uint RequestControllerCount = 0;
    public const uint RequestControllerData = 1;
    public const uint Ack = 10;
    public const uint RequestProtocolVersion = 40;
    public const uint SetClientName = 50;
    public const uint SetServerName = 51;
    public const uint DeviceListUpdated = 100;
    public const uint RgbControllerResizeZone = 1000;
    public const uint RgbControllerUpdateLeds = 1050;
    public const uint RgbControllerUpdateZoneLeds = 1051;
    public const uint RgbControllerUpdateSingleLed = 1052;
    public const uint RgbControllerSetCustomMode = 1100;
    public const uint RgbControllerUpdateMode = 1101;
    public const uint RgbControllerSaveMode = 1102;
}
