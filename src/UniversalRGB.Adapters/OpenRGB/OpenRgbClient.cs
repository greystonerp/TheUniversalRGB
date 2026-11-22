using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using UniversalRGB.Core.Devices;

namespace UniversalRGB.Adapters.OpenRGB;

/// <summary>
/// Minimal, thread-safe OpenRGB SDK client. One request awaits a reply at a time; writes are serialized.
/// Unsolicited packets (ACKs, server name, detection progress) are ignored, DEVICE_LIST_UPDATED is surfaced as an event.
/// </summary>
public sealed class OpenRgbClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly Lock _pendingGate = new();

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;
    private PendingRequest? _pending;
    private int _disconnectSignalled;
    private volatile bool _closed;

    public event EventHandler? DeviceListUpdated;

    public event EventHandler<Exception?>? Disconnected;

    /// <summary>Negotiated protocol version (min of client and server).</summary>
    public uint ProtocolVersion { get; private set; }

    public uint ServerProtocolVersion { get; private set; }

    public string? ServerName { get; private set; }

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public bool IsConnected => !_closed && _tcp?.Connected == true && _readLoop is { IsCompleted: false };

    public async Task ConnectAsync(string host, int port, string clientName, CancellationToken cancellationToken)
    {
        if (_tcp is not null)
        {
            throw new InvalidOperationException("Client is already connected.");
        }

        _tcp = new TcpClient { NoDelay = true };
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await _tcp.ConnectAsync(host, port, connectTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out connecting to {host}:{port}.");
            }
        }

        _stream = _tcp.GetStream();
        _readCts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_readCts.Token));

        // Protocol negotiation. Servers older than protocol 1 never answer this request.
        try
        {
            var reply = await RequestAsync(0, OpenRgbProtocol.RequestProtocolVersion, U32(OpenRgbProtocol.ClientProtocolVersion), TimeSpan.FromSeconds(1.5), cancellationToken)
                .ConfigureAwait(false);
            ServerProtocolVersion = reply.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(reply) : 0;
        }
        catch (TimeoutException)
        {
            ServerProtocolVersion = 0;
        }

        ProtocolVersion = Math.Min(OpenRgbProtocol.ClientProtocolVersion, ServerProtocolVersion);

        var name = Encoding.UTF8.GetBytes(clientName + "\0");
        await SendAsync(0, OpenRgbProtocol.SetClientName, name, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> GetControllerCountAsync(CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(0, OpenRgbProtocol.RequestControllerCount, [], RequestTimeout, cancellationToken).ConfigureAwait(false);
        if (reply.Length < 4)
        {
            throw new InvalidDataException("Controller count reply is truncated.");
        }

        return (int)BinaryPrimitives.ReadUInt32LittleEndian(reply);
    }

    public async Task<OpenRgbControllerData> GetControllerAsync(int index, CancellationToken cancellationToken)
    {
        var payload = ProtocolVersion >= 1 ? U32(ProtocolVersion) : [];
        var reply = await RequestAsync((uint)index, OpenRgbProtocol.RequestControllerData, payload, RequestTimeout, cancellationToken).ConfigureAwait(false);
        return OpenRgbSerializer.ParseController(reply, ProtocolVersion);
    }

    public Task UpdateLedsAsync(int deviceIndex, IReadOnlyList<RgbColor> colors, CancellationToken cancellationToken) =>
        SendAsync((uint)deviceIndex, OpenRgbProtocol.RgbControllerUpdateLeds, OpenRgbSerializer.UpdateLeds(colors), cancellationToken);

    public Task UpdateZoneLedsAsync(int deviceIndex, int zoneIndex, IReadOnlyList<RgbColor> colors, CancellationToken cancellationToken) =>
        SendAsync((uint)deviceIndex, OpenRgbProtocol.RgbControllerUpdateZoneLeds, OpenRgbSerializer.UpdateZoneLeds(zoneIndex, colors), cancellationToken);

    /// <summary>Asks the controller to switch to its Direct (or best per-LED) mode.</summary>
    public Task SetCustomModeAsync(int deviceIndex, CancellationToken cancellationToken) =>
        SendAsync((uint)deviceIndex, OpenRgbProtocol.RgbControllerSetCustomMode, [], cancellationToken);

    public Task UpdateModeAsync(int deviceIndex, int modeIndex, RgbMode mode, CancellationToken cancellationToken) =>
        SendAsync((uint)deviceIndex, OpenRgbProtocol.RgbControllerUpdateMode, OpenRgbSerializer.UpdateMode(modeIndex, mode, ProtocolVersion), cancellationToken);

    public Task ResizeZoneAsync(int deviceIndex, int zoneIndex, int newSize, CancellationToken cancellationToken) =>
        SendAsync((uint)deviceIndex, OpenRgbProtocol.RgbControllerResizeZone, OpenRgbSerializer.ResizeZone(zoneIndex, newSize), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        _readCts?.Cancel();
        try
        {
            _tcp?.Close();
        }
        catch (SocketException)
        {
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Read loop errors were already reported through Disconnected.
            }
        }

        FailPending(new ObjectDisposedException(nameof(OpenRgbClient)));
        _readCts?.Dispose();
        _tcp?.Dispose();
        _tcp = null;
    }

    // ------------------------------------------------------------------ transport

    private async Task SendAsync(uint deviceIndex, uint packetId, byte[] payload, CancellationToken cancellationToken)
    {
        var stream = _stream;
        if (stream is null || _closed)
        {
            throw new IOException("Not connected to the OpenRGB SDK server.");
        }

        var packet = new byte[OpenRgbProtocol.HeaderSize + payload.Length];
        PacketWriter.Header(deviceIndex, packetId, payload.Length).CopyTo(packet, 0);
        payload.CopyTo(packet, OpenRgbProtocol.HeaderSize);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<byte[]> RequestAsync(uint deviceIndex, uint packetId, byte[] payload, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var pending = new PendingRequest(deviceIndex, packetId);
        try
        {
            lock (_pendingGate)
            {
                _pending = pending;
            }

            // The read loop sets _closed before failing pending requests, so a request registered after
            // the connection died is caught here instead of waiting for the timeout.
            if (_closed)
            {
                throw new IOException("Connection to the OpenRGB SDK server is closed.");
            }

            await SendAsync(deviceIndex, packetId, payload, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                return await pending.Completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"OpenRGB did not answer request {packetId} within {timeout.TotalSeconds:0.#} s.");
            }
        }
        finally
        {
            lock (_pendingGate)
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                }
            }

            _requestLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        var stream = _stream!;
        var header = new byte[OpenRgbProtocol.HeaderSize];
        Exception? error = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
                if (!header.AsSpan(0, 4).SequenceEqual(OpenRgbProtocol.Magic))
                {
                    throw new InvalidDataException("Received a packet without the ORGB magic header.");
                }

                var deviceIndex = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                var packetId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
                if (size > OpenRgbProtocol.MaxPacketSize)
                {
                    throw new InvalidDataException($"Packet size {size} exceeds the protocol limit.");
                }

                var payload = new byte[size];
                if (size > 0)
                {
                    await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
                }

                Dispatch(deviceIndex, packetId, payload);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            error = ex;
        }

        _closed = true;
        FailPending(error ?? new IOException("Connection closed."));
        if (!token.IsCancellationRequested && Interlocked.Exchange(ref _disconnectSignalled, 1) == 0)
        {
            Disconnected?.Invoke(this, error);
        }
    }

    private void Dispatch(uint deviceIndex, uint packetId, byte[] payload)
    {
        switch (packetId)
        {
            case OpenRgbProtocol.DeviceListUpdated:
                ThreadPool.QueueUserWorkItem(_ => DeviceListUpdated?.Invoke(this, EventArgs.Empty));
                return;
            case OpenRgbProtocol.SetServerName:
                ServerName = Encoding.UTF8.GetString(payload).TrimEnd('\0');
                return;
        }

        PendingRequest? pending;
        lock (_pendingGate)
        {
            pending = _pending;
        }

        if (pending is not null && pending.PacketId == packetId && (pending.DeviceIndex == deviceIndex || packetId != OpenRgbProtocol.RequestControllerData))
        {
            pending.Completion.TrySetResult(payload);
        }
    }

    private void FailPending(Exception error)
    {
        lock (_pendingGate)
        {
            _pending?.Completion.TrySetException(error);
        }
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private sealed class PendingRequest(uint deviceIndex, uint packetId)
    {
        public uint DeviceIndex { get; } = deviceIndex;
        public uint PacketId { get; } = packetId;
        public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
