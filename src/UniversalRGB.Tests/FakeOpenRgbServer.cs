using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UniversalRGB.Adapters.OpenRGB;
using UniversalRGB.Core.Devices;

namespace UniversalRGB.Tests;

/// <summary>
/// In-process OpenRGB SDK server that follows the real wire protocol, including unsolicited packets
/// (server name, ACKs) that a robust client must ignore.
/// </summary>
internal sealed class FakeOpenRgbServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public FakeOpenRgbServer(params OpenRgbControllerData[] controllers)
    {
        Controllers.AddRange(controllers);
        _listener.Start();
        _acceptLoop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public List<OpenRgbControllerData> Controllers { get; } = [];

    public uint ServerProtocol { get; set; } = 5;

    /// <summary>When false the server ignores protocol-version requests (behaves like a protocol 0 server).</summary>
    public bool AnswersProtocolRequest { get; set; } = true;

    public uint NegotiatedProtocol { get; private set; }

    public string? ClientName { get; private set; }

    public ConcurrentQueue<(uint Device, uint PacketId, byte[] Payload)> Received { get; } = new();

    public async Task<(uint Device, uint PacketId, byte[] Payload)> WaitForAsync(uint packetId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (DateTime.UtcNow < deadline)
        {
            var hit = Received.LastOrDefault(p => p.PacketId == packetId);
            if (hit.Payload is not null)
            {
                return hit;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Packet {packetId} was not received.");
    }

    public Task SendDeviceListUpdatedAsync() => SendAsync(0, OpenRgbProtocol.DeviceListUpdated, []);

    public void DropClient()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is null)
        {
            return;
        }

        try
        {
            client.Client.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }

        client.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        DropClient();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (Exception)
        {
        }
    }

    private async Task AcceptAsync()
    {
        _client = await _listener.AcceptTcpClientAsync(_cts.Token);
        _stream = _client.GetStream();
        var header = new byte[OpenRgbProtocol.HeaderSize];
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _stream.ReadExactlyAsync(header, _cts.Token);
            }
            catch (Exception)
            {
                return;
            }

            var dev = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            var id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            var size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
            var payload = new byte[size];
            if (size > 0)
            {
                await _stream.ReadExactlyAsync(payload, _cts.Token);
            }

            Received.Enqueue((dev, id, payload));
            await HandleAsync(dev, id, payload);
        }
    }

    private async Task HandleAsync(uint dev, uint id, byte[] payload)
    {
        switch (id)
        {
            case OpenRgbProtocol.RequestProtocolVersion when AnswersProtocolRequest:
                NegotiatedProtocol = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(payload), ServerProtocol);
                await SendAsync(0, OpenRgbProtocol.RequestProtocolVersion, U32(ServerProtocol));
                await SendAsync(0, OpenRgbProtocol.SetServerName, Encoding.UTF8.GetBytes("Fake OpenRGB\0"));
                break;

            case OpenRgbProtocol.SetClientName:
                ClientName = Encoding.UTF8.GetString(payload).TrimEnd('\0');
                break;

            case OpenRgbProtocol.RequestControllerCount:
                await SendAsync(0, OpenRgbProtocol.RequestControllerCount, U32((uint)Controllers.Count));
                break;

            case OpenRgbProtocol.RequestControllerData:
            {
                var version = payload.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0;
                await SendAsync(dev, OpenRgbProtocol.Ack, new byte[12]); // unsolicited noise first
                await SendAsync(dev, OpenRgbProtocol.RequestControllerData, OpenRgbSerializer.SerializeController(Controllers[(int)dev], version));
                break;
            }

            case OpenRgbProtocol.RgbControllerResizeZone:
            {
                var zone = BinaryPrimitives.ReadInt32LittleEndian(payload);
                var newSize = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4));
                Controllers[(int)dev] = Resize(Controllers[(int)dev], zone, newSize);
                await SendDeviceListUpdatedAsync();
                break;
            }
        }
    }

    private static OpenRgbControllerData Resize(OpenRgbControllerData c, int zoneIndex, int newSize)
    {
        var zones = c.Zones.ToList();
        zones[zoneIndex] = zones[zoneIndex] with { LedCount = newSize };
        var start = 0;
        for (var i = 0; i < zones.Count; i++)
        {
            zones[i] = zones[i] with { StartIndex = start };
            start += zones[i].LedCount;
        }

        return c with
        {
            Zones = zones,
            Leds = Enumerable.Range(0, start).Select(i => new OpenRgbLed($"LED {i}", (uint)i)).ToList(),
            Colors = Enumerable.Repeat(RgbColor.Black, start).ToList(),
        };
    }

    private async Task SendAsync(uint dev, uint id, byte[] payload)
    {
        var stream = _stream;
        if (stream is null)
        {
            return;
        }

        await _writeLock.WaitAsync();
        try
        {
            await stream.WriteAsync(PacketWriter.Header(dev, id, payload.Length));
            await stream.WriteAsync(payload);
        }
        catch (IOException)
        {
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static byte[] U32(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }
}
