// Simulated OpenRGB SDK server for developing and testing Universal RGB without RGB hardware.
// Usage: UniversalRGB.Simulator [--port 6742]
// It speaks the real OpenRGB wire protocol (server protocol 5) and exposes four simulated controllers that
// cover every control strategy: Direct streaming, static-only (no Direct mode), hardware-effects-only, and a
// resizable ARGB header that still needs its LED count configured.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UniversalRGB.Adapters.OpenRGB;
using UniversalRGB.Core.Devices;

const uint ServerProtocol = 5;

var port = 6742;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var p))
    {
        port = p;
    }
}

var gate = new object();
var devices = SimulatedDevices.Create();
var clients = new ConcurrentDictionary<int, Connection>();
var frames = new ConcurrentDictionary<int, int>();
var nextClient = 0;

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
Console.WriteLine($"Simulated OpenRGB SDK server listening on 127.0.0.1:{port} (protocol {ServerProtocol}).");
foreach (var d in devices)
{
    Console.WriteLine($"  [{devices.IndexOf(d)}] {d.Name} — {d.Leds.Count} LEDs, modes: {string.Join(", ", d.Modes.Select(m => m.Name))}");
}

Console.WriteLine("Press Ctrl+C to stop.");

_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(2000);
        if (frames.IsEmpty)
        {
            continue;
        }

        var summary = string.Join("  ", frames.OrderBy(f => f.Key).Select(f => $"#{f.Key}: {f.Value / 2.0:0.#} fps"));
        frames.Clear();
        Console.WriteLine($"{DateTime.Now:HH:mm:ss}  LED updates  {summary}");
    }
});

while (true)
{
    var tcp = await listener.AcceptTcpClientAsync();
    var id = Interlocked.Increment(ref nextClient);
    var connection = new Connection(tcp);
    clients[id] = connection;
    _ = Task.Run(async () =>
    {
        try
        {
            await ServeAsync(connection);
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException or ObjectDisposedException)
        {
        }
        finally
        {
            clients.TryRemove(id, out _);
            Console.WriteLine($"Client {connection.Name ?? id.ToString()} disconnected.");
            tcp.Dispose();
        }
    });
}

async Task ServeAsync(Connection c)
{
    var header = new byte[OpenRgbProtocol.HeaderSize];
    while (true)
    {
        await c.Stream.ReadExactlyAsync(header);
        var dev = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var pkt = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        var data = new byte[size];
        if (size > 0)
        {
            await c.Stream.ReadExactlyAsync(data);
        }

        switch (pkt)
        {
            case OpenRgbProtocol.RequestProtocolVersion:
                c.Protocol = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data), ServerProtocol);
                await c.SendAsync(0, pkt, U32(ServerProtocol));
                await c.SendAsync(0, OpenRgbProtocol.SetServerName, Encoding.UTF8.GetBytes("Universal RGB Simulator\0"));
                break;

            case OpenRgbProtocol.SetClientName:
                c.Name = Encoding.UTF8.GetString(data).TrimEnd('\0');
                Console.WriteLine($"Client connected: {c.Name} (protocol {c.Protocol}).");
                break;

            case OpenRgbProtocol.RequestControllerCount:
                await c.SendAsync(0, pkt, U32((uint)devices.Count));
                break;

            case OpenRgbProtocol.RequestControllerData:
            {
                var version = data.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0;
                byte[] payload;
                lock (gate)
                {
                    payload = OpenRgbSerializer.SerializeController(devices[(int)dev], version);
                }

                await c.SendAsync(dev, pkt, payload);
                break;
            }

            case OpenRgbProtocol.RgbControllerUpdateLeds:
            {
                var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
                lock (gate)
                {
                    var colors = devices[(int)dev].Colors.ToArray();
                    for (var i = 0; i < count && i < colors.Length; i++)
                    {
                        colors[i] = RgbColor.FromOpenRgb(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(6 + (i * 4))));
                    }

                    devices[(int)dev] = devices[(int)dev] with { Colors = colors };
                }

                frames.AddOrUpdate((int)dev, 1, (_, n) => n + 1);
                break;
            }

            case OpenRgbProtocol.RgbControllerUpdateZoneLeds:
                frames.AddOrUpdate((int)dev, 1, (_, n) => n + 1);
                break;

            case OpenRgbProtocol.RgbControllerSetCustomMode:
                lock (gate)
                {
                    var d = devices[(int)dev];
                    var direct = d.Modes.FirstOrDefault(m => m.IsDirect) ?? d.Modes.FirstOrDefault(m => m.Has(ModeFlags.HasPerLedColor));
                    if (direct is not null)
                    {
                        devices[(int)dev] = d with { ActiveMode = direct.Index };
                        Console.WriteLine($"{d.Name}: switched to {direct.Name} mode.");
                    }
                }

                break;

            case OpenRgbProtocol.RgbControllerUpdateMode:
            {
                var modeIndex = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
                var reader = new PacketReader(data.AsSpan(8));
                var mode = OpenRgbSerializer.ReadMode(ref reader, modeIndex, c.Protocol);
                lock (gate)
                {
                    var d = devices[(int)dev];
                    var modes = d.Modes.ToList();
                    modes[modeIndex] = mode;
                    devices[(int)dev] = d with { Modes = modes, ActiveMode = modeIndex };
                    Console.WriteLine($"{d.Name}: hardware mode {mode.Name} (speed {mode.Speed}, colors {string.Join(' ', mode.Colors)}).");
                }

                break;
            }

            case OpenRgbProtocol.RgbControllerResizeZone:
            {
                var zone = BinaryPrimitives.ReadInt32LittleEndian(data);
                var newSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
                lock (gate)
                {
                    devices[(int)dev] = SimulatedDevices.Resize(devices[(int)dev], zone, newSize);
                    Console.WriteLine($"{devices[(int)dev].Name}: zone {zone} resized to {newSize} LEDs.");
                }

                foreach (var other in clients.Values)
                {
                    await other.SendAsync(0, OpenRgbProtocol.DeviceListUpdated, []);
                }

                break;
            }
        }
    }
}

static byte[] U32(uint value)
{
    var b = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(b, value);
    return b;
}

internal sealed class Connection(TcpClient tcp)
{
    private readonly SemaphoreSlim _write = new(1, 1);

    public NetworkStream Stream { get; } = tcp.GetStream();
    public uint Protocol { get; set; }
    public string? Name { get; set; }

    public async Task SendAsync(uint dev, uint pkt, byte[] payload)
    {
        await _write.WaitAsync();
        try
        {
            await Stream.WriteAsync(PacketWriter.Header(dev, pkt, payload.Length));
            await Stream.WriteAsync(payload);
        }
        finally
        {
            _write.Release();
        }
    }
}

internal static class SimulatedDevices
{
    public static List<OpenRgbControllerData> Create() =>
    [
        Build("Simulated Aura Motherboard", RgbDeviceType.Motherboard, "ASUS",
            [
                Mode(0, "Direct", ModeFlags.HasPerLedColor, ModeColorMode.PerLed),
                Mode(1, "Static", ModeFlags.HasModeSpecificColor, ModeColorMode.ModeSpecific, colorsMax: 1),
                Mode(2, "Breathing", ModeFlags.HasSpeed | ModeFlags.HasModeSpecificColor, ModeColorMode.ModeSpecific, speedMin: 0, speedMax: 4, colorsMax: 1),
                Mode(3, "Rainbow", ModeFlags.HasSpeed | ModeFlags.HasRandomColor, ModeColorMode.None, speedMin: 0, speedMax: 4),
                Mode(4, "Off", ModeFlags.None, ModeColorMode.None),
            ],
            [("Aura Mainboard", RgbZoneType.Linear, 4, 4, 4), ("Aura Addressable 1", RgbZoneType.Linear, 0, 120, 24), ("Aura Addressable 2", RgbZoneType.Linear, 0, 120, 0)]),

        BuildKeyboard(),

        Build("Simulated Fan Hub (no Direct mode)", RgbDeviceType.Cooler, "Generic",
            [
                Mode(0, "Static", ModeFlags.HasPerLedColor | ModeFlags.HasModeSpecificColor, ModeColorMode.PerLed, colorsMax: 1),
                Mode(1, "Breathing", ModeFlags.HasSpeed | ModeFlags.HasModeSpecificColor, ModeColorMode.ModeSpecific, speedMin: 1, speedMax: 5, colorsMax: 2),
                Mode(2, "Spectrum Cycle", ModeFlags.HasSpeed, ModeColorMode.None, speedMin: 1, speedMax: 5),
            ],
            [("Fan 1", RgbZoneType.Linear, 8, 8, 8), ("Fan 2", RgbZoneType.Linear, 8, 8, 8), ("Fan 3", RgbZoneType.Linear, 8, 8, 8)]),

        Build("Simulated GPU (hardware effects only)", RgbDeviceType.Gpu, "Generic",
            [
                Mode(0, "Static", ModeFlags.HasModeSpecificColor | ModeFlags.HasBrightness, ModeColorMode.ModeSpecific, colorsMax: 1),
                Mode(1, "Breathing", ModeFlags.HasSpeed | ModeFlags.HasModeSpecificColor, ModeColorMode.ModeSpecific, speedMin: 1, speedMax: 3, colorsMax: 1),
                Mode(2, "Rainbow Wave", ModeFlags.HasSpeed, ModeColorMode.None, speedMin: 1, speedMax: 3),
                Mode(3, "Off", ModeFlags.None, ModeColorMode.None),
            ],
            [("Logo", RgbZoneType.Single, 1, 1, 1)]),
    ];

    public static OpenRgbControllerData Resize(OpenRgbControllerData d, int zoneIndex, int newSize)
    {
        var zones = d.Zones.ToList();
        zones[zoneIndex] = zones[zoneIndex] with { LedCount = newSize };
        return WithLeds(d with { Zones = zones });
    }

    private static OpenRgbControllerData BuildKeyboard()
    {
        const int width = 18, height = 6;
        var map = new List<uint>();
        uint next = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                map.Add((y == 5 && x is > 3 and < 10) ? RgbMatrix.NoLed : next++);
            }
        }

        var zone = new RgbZone
        {
            Id = "0", Index = 0, Name = "Keyboard", Type = RgbZoneType.Matrix, LedsMin = (int)next, LedsMax = (int)next, LedCount = (int)next,
            Matrix = new RgbMatrix(width, height, map),
        };
        return WithLeds(new OpenRgbControllerData
        {
            Type = RgbDeviceType.Keyboard,
            Name = "Simulated Keyboard",
            Vendor = "Generic",
            Description = "Simulated per-key RGB keyboard",
            Location = "SIM: keyboard",
            Modes = [Mode(0, "Direct", ModeFlags.HasPerLedColor, ModeColorMode.PerLed), Mode(1, "Spectrum Cycle", ModeFlags.HasSpeed, ModeColorMode.None, speedMin: 1, speedMax: 3)],
            ActiveMode = 1,
            Zones = [zone],
        });
    }

    private static OpenRgbControllerData Build(string name, RgbDeviceType type, string vendor, List<RgbMode> modes,
        (string Name, RgbZoneType Type, int Min, int Max, int Count)[] zones) =>
        WithLeds(new OpenRgbControllerData
        {
            Type = type,
            Name = name,
            Vendor = vendor,
            Description = "Universal RGB simulator device",
            Version = "sim-1.0",
            Location = "SIM: " + name.ToLowerInvariant().Replace(' ', '-'),
            Modes = modes,
            ActiveMode = 0,
            Zones = zones.Select((z, i) => new RgbZone { Id = i.ToString(), Index = i, Name = z.Name, Type = z.Type, LedsMin = z.Min, LedsMax = z.Max, LedCount = z.Count }).ToList(),
        });

    private static OpenRgbControllerData WithLeds(OpenRgbControllerData d)
    {
        var start = 0;
        var zones = new List<RgbZone>();
        foreach (var z in d.Zones)
        {
            zones.Add(z with { StartIndex = start });
            start += z.LedCount;
        }

        return d with
        {
            Zones = zones,
            Leds = Enumerable.Range(0, start).Select(i => new OpenRgbLed($"LED {i + 1}", (uint)i)).ToList(),
            Colors = Enumerable.Repeat(RgbColor.Black, start).ToList(),
        };
    }

    private static RgbMode Mode(int index, string name, ModeFlags flags, ModeColorMode colorMode, uint speedMin = 0, uint speedMax = 0, uint colorsMax = 0) => new()
    {
        Index = index,
        Name = name,
        Value = index,
        Flags = flags,
        SpeedMin = speedMin,
        SpeedMax = speedMax,
        Speed = speedMin,
        ColorsMin = colorsMax > 0 ? 1u : 0u,
        ColorsMax = colorsMax,
        BrightnessMin = 0,
        BrightnessMax = flags.HasFlag(ModeFlags.HasBrightness) ? 100u : 0u,
        Brightness = flags.HasFlag(ModeFlags.HasBrightness) ? 100u : 0u,
        ColorMode = colorMode,
        Colors = colorsMax > 0 ? [RgbColor.White] : [],
    };
}
