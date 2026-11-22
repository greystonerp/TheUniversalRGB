using System.Globalization;
using System.Text;
using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Diagnostics;
using UniversalRGB.Core.Interfaces;
using UniversalRGB.Core.Lighting;

namespace UniversalRGB.Adapters.OpenRGB;

/// <summary>RGB backend that talks to a running OpenRGB SDK server.</summary>
public sealed class OpenRgbAdapter : IRgbDeviceAdapter
{
    public const string Name = "OpenRGB";

    private readonly Func<(string Host, int Port)> _endpoint;
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _discoverGate = new(1, 1);

    private OpenRgbClient? _client;
    private Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private AdapterConnectionState _state = AdapterConnectionState.Disconnected;
    private string _stateDetail = "Not connected.";

    public OpenRgbAdapter(Func<(string Host, int Port)> endpoint, IAppLog? log = null)
    {
        _endpoint = endpoint;
        _log = log ?? NullLog.Instance;
    }

    public string AdapterName => Name;

    public AdapterConnectionState State => _state;

    public string StateDetail => _stateDetail;

    public uint ProtocolVersion => _client?.ProtocolVersion ?? 0;

    public uint ServerProtocolVersion => _client?.ServerProtocolVersion ?? 0;

    public event EventHandler? StateChanged;

    public event EventHandler? DevicesChanged;

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken)
    {
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is { IsConnected: true })
            {
                return true;
            }

            await DisposeClientAsync().ConfigureAwait(false);
            var (host, port) = _endpoint();
            SetState(AdapterConnectionState.Connecting, $"Connecting to {host}:{port}…");

            var client = new OpenRgbClient();
            try
            {
                await client.ConnectAsync(host, port, "Universal RGB Control", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                var reason = ex is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused }
                    ? $"No OpenRGB SDK server is listening on {host}:{port}."
                    : ex.Message;
                SetState(AdapterConnectionState.Disconnected, reason);
                return false;
            }

            client.DeviceListUpdated += OnDeviceListUpdated;
            client.Disconnected += OnDisconnected;
            _client = client;
            var server = string.IsNullOrWhiteSpace(client.ServerName) ? "OpenRGB" : client.ServerName;
            SetState(AdapterConnectionState.Connected,
                $"Connected to {server} at {host}:{port} (protocol {client.ProtocolVersion}, server {client.ServerProtocolVersion}).");
            _log.Info(_stateDetail);
            return true;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeClientAsync().ConfigureAwait(false);
            _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            SetState(AdapterConnectionState.Disconnected, "Disconnected.");
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task<IReadOnlyList<RgbDevice>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var client = RequireClient();
        await _discoverGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var count = await client.GetControllerCountAsync(cancellationToken).ConfigureAwait(false);
            var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            var devices = new List<RgbDevice>(count);
            for (var i = 0; i < count; i++)
            {
                OpenRgbControllerData data;
                try
                {
                    data = await client.GetControllerAsync(i, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException ex)
                {
                    _log.Warn($"Skipping OpenRGB controller {i}: malformed data.", ex);
                    continue;
                }

                var id = MakeId(data);
                for (var n = 2; entries.ContainsKey(id); n++)
                {
                    id = MakeId(data) + "#" + n.ToString(CultureInfo.InvariantCulture);
                }

                var device = ToDevice(id, data);
                entries[id] = new Entry(i, device);
                devices.Add(device);
            }

            _entries = entries;
            return devices;
        }
        finally
        {
            _discoverGate.Release();
        }
    }

    public Task<DeviceCapabilities> GetCapabilitiesAsync(string deviceId, CancellationToken cancellationToken) =>
        Task.FromResult(DeviceCapabilities.From(RequireEntry(deviceId).Device));

    public Task PrepareSoftwareControlAsync(string deviceId, CancellationToken cancellationToken)
    {
        var entry = RequireEntry(deviceId);
        return RequireClient().SetCustomModeAsync(entry.Index, cancellationToken);
    }

    public Task SetColorAsync(string deviceId, string zoneId, RgbColor color, CancellationToken cancellationToken)
    {
        var entry = RequireEntry(deviceId);
        var count = zoneId == RgbZone.AllZones ? entry.Device.LedCount : RequireZone(entry, zoneId).LedCount;
        return SetColorsAsync(deviceId, zoneId, Enumerable.Repeat(color, count).ToArray(), cancellationToken);
    }

    public Task SetColorsAsync(string deviceId, string zoneId, IReadOnlyList<RgbColor> colors, CancellationToken cancellationToken)
    {
        var entry = RequireEntry(deviceId);
        var client = RequireClient();
        if (zoneId == RgbZone.AllZones)
        {
            return client.UpdateLedsAsync(entry.Index, Fit(colors, entry.Device.LedCount), cancellationToken);
        }

        var zone = RequireZone(entry, zoneId);
        return client.UpdateZoneLedsAsync(entry.Index, zone.Index, Fit(colors, zone.LedCount), cancellationToken);
    }

    public async Task<bool> SetEffectAsync(string deviceId, string zoneId, LightingEffect effect, CancellationToken cancellationToken)
    {
        var entry = RequireEntry(deviceId);
        var choice = HardwareEffectMapper.Choose(entry.Device.Modes, effect);
        if (choice is null)
        {
            return false;
        }

        var client = RequireClient();
        await client.UpdateModeAsync(entry.Index, choice.ModeIndex, choice.Mode, cancellationToken).ConfigureAwait(false);
        if (choice.NeedsPerLedColors && entry.Device.LedCount > 0)
        {
            var colors = Enumerable.Repeat(choice.PerLedColor, entry.Device.LedCount).ToArray();
            await client.UpdateLedsAsync(entry.Index, colors, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public async Task ResizeZoneAsync(string deviceId, string zoneId, int ledCount, CancellationToken cancellationToken)
    {
        var entry = RequireEntry(deviceId);
        var zone = RequireZone(entry, zoneId);
        if (!zone.IsResizable)
        {
            throw new InvalidOperationException($"Zone '{zone.Name}' has a fixed size.");
        }

        if (ledCount < zone.LedsMin || ledCount > zone.LedsMax)
        {
            throw new ArgumentOutOfRangeException(nameof(ledCount), $"'{zone.Name}' accepts {zone.LedsMin}–{zone.LedsMax} LEDs.");
        }

        await RequireClient().ResizeZoneAsync(entry.Index, zone.Index, ledCount, cancellationToken).ConfigureAwait(false);
        _log.Info($"Resized {entry.Device.Name} / {zone.Name} to {ledCount} LEDs.");
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeClientAsync().ConfigureAwait(false);
        _connectGate.Dispose();
        _discoverGate.Dispose();
    }

    // ------------------------------------------------------------------ helpers

    internal static string MakeId(OpenRgbControllerData data)
    {
        var raw = $"{data.Name}|{data.Location}|{data.Serial}";
        var sb = new StringBuilder("openrgb:");
        foreach (var ch in raw)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch is '|' or '-' or '_' or '.' ? char.ToLowerInvariant(ch) : '_');
        }

        return sb.ToString();
    }

    internal static RgbDevice ToDevice(string id, OpenRgbControllerData data) => new()
    {
        Id = id,
        AdapterName = Name,
        Name = data.Name,
        Vendor = data.Vendor,
        Description = data.Description,
        Version = data.Version,
        Serial = data.Serial,
        Location = data.Location,
        Type = data.Type,
        Zones = data.Zones,
        Modes = data.Modes,
        ActiveModeIndex = data.ActiveMode,
        LedCount = Math.Max(data.Leds.Count, data.Zones.Sum(z => z.LedCount)),
        Colors = data.Colors,
        Identity = HardwareIdParser.Parse(data.Location),
    };

    private static RgbColor[] Fit(IReadOnlyList<RgbColor> colors, int count)
    {
        var result = new RgbColor[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = i < colors.Count ? colors[i] : (colors.Count > 0 ? colors[^1] : RgbColor.Black);
        }

        return result;
    }

    private OpenRgbClient RequireClient() =>
        _client is { IsConnected: true } client ? client : throw new IOException("Not connected to the OpenRGB SDK server.");

    private Entry RequireEntry(string deviceId) =>
        _entries.TryGetValue(deviceId, out var entry) ? entry : throw new KeyNotFoundException($"OpenRGB device '{deviceId}' is not available.");

    private static RgbZone RequireZone(Entry entry, string zoneId) =>
        entry.Device.FindZone(zoneId) ?? throw new KeyNotFoundException($"Zone '{zoneId}' does not exist on {entry.Device.Name}.");

    private void OnDeviceListUpdated(object? sender, EventArgs e)
    {
        _log.Info("OpenRGB reported a device list change.");
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDisconnected(object? sender, Exception? error)
    {
        _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var reason = error is null ? "OpenRGB closed the connection." : $"Connection to OpenRGB lost: {error.Message}";
        _log.Warn(reason);
        SetState(AdapterConnectionState.Disconnected, reason);
    }

    private async Task DisposeClientAsync()
    {
        var client = _client;
        _client = null;
        if (client is not null)
        {
            client.DeviceListUpdated -= OnDeviceListUpdated;
            client.Disconnected -= OnDisconnected;
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void SetState(AdapterConnectionState state, string detail)
    {
        _state = state;
        _stateDetail = detail;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed record Entry(int Index, RgbDevice Device);
}
