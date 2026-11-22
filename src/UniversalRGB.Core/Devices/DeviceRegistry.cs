using UniversalRGB.Core.Interfaces;

namespace UniversalRGB.Core.Devices;

/// <summary>Aggregates the controllable devices of every connected adapter.</summary>
public sealed class DeviceRegistry
{
    private readonly List<IRgbDeviceAdapter> _adapters = [];
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private volatile IReadOnlyList<RgbDevice> _devices = [];
    private Dictionary<string, IRgbDeviceAdapter> _owners = new(StringComparer.Ordinal);

    public event EventHandler? DevicesChanged;

    /// <summary>Raised when an adapter fails during discovery; the message is user-presentable.</summary>
    public event EventHandler<string>? RefreshFailed;

    public IReadOnlyList<IRgbDeviceAdapter> Adapters => _adapters;

    public IReadOnlyList<RgbDevice> Devices => _devices;

    public void AddAdapter(IRgbDeviceAdapter adapter)
    {
        _adapters.Add(adapter);
        adapter.DevicesChanged += async (_, _) => await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        adapter.StateChanged += async (_, _) =>
        {
            if (adapter.State != AdapterConnectionState.Connecting)
            {
                await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            }
        };
    }

    public RgbDevice? Find(string deviceId) => _devices.FirstOrDefault(d => d.Id == deviceId);

    public IRgbDeviceAdapter? GetAdapter(string deviceId)
    {
        var owners = _owners;
        return owners.TryGetValue(deviceId, out var adapter) ? adapter : null;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var devices = new List<RgbDevice>();
            var owners = new Dictionary<string, IRgbDeviceAdapter>(StringComparer.Ordinal);
            foreach (var adapter in _adapters)
            {
                if (adapter.State != AdapterConnectionState.Connected)
                {
                    continue;
                }

                try
                {
                    foreach (var device in await adapter.DiscoverAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (owners.TryAdd(device.Id, adapter))
                        {
                            devices.Add(device);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    RefreshFailed?.Invoke(this, $"{adapter.AdapterName}: {ex.Message}");
                }
            }

            _owners = owners;
            _devices = devices;
        }
        finally
        {
            _refreshGate.Release();
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }
}
