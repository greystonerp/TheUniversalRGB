using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Lighting;

namespace UniversalRGB.Core.Interfaces;

public enum AdapterConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error,
}

/// <summary>
/// One RGB backend (OpenRGB, a manufacturer SDK, a native controller). Every adapter must validate
/// capabilities and handle disconnects, hardware errors, timeouts and unsupported operations.
/// </summary>
public interface IRgbDeviceAdapter : IAsyncDisposable
{
    string AdapterName { get; }

    AdapterConnectionState State { get; }

    /// <summary>Human-readable explanation of the current state (last error, server version, ...).</summary>
    string StateDetail { get; }

    event EventHandler? StateChanged;

    /// <summary>Raised when the backend reports that its device list changed.</summary>
    event EventHandler? DevicesChanged;

    Task<bool> ConnectAsync(CancellationToken cancellationToken);

    Task DisconnectAsync();

    Task<IReadOnlyList<RgbDevice>> DiscoverAsync(CancellationToken cancellationToken);

    Task<DeviceCapabilities> GetCapabilitiesAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>Switches the device into its software-controlled (Direct/custom) mode.</summary>
    Task PrepareSoftwareControlAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>Sets a whole zone (or <see cref="RgbZone.AllZones"/>) to a single color.</summary>
    Task SetColorAsync(string deviceId, string zoneId, RgbColor color, CancellationToken cancellationToken);

    /// <summary>Sets per-LED colors for a zone, or for the whole device when zoneId is <see cref="RgbZone.AllZones"/>.</summary>
    Task SetColorsAsync(string deviceId, string zoneId, IReadOnlyList<RgbColor> colors, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a built-in hardware effect that best matches <paramref name="effect"/>.
    /// Returns false when the device has no matching hardware mode.
    /// </summary>
    Task<bool> SetEffectAsync(string deviceId, string zoneId, LightingEffect effect, CancellationToken cancellationToken);

    /// <summary>Sets the LED count of a resizable zone (ARGB header / hub channel).</summary>
    Task ResizeZoneAsync(string deviceId, string zoneId, int ledCount, CancellationToken cancellationToken);
}
