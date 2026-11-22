using System.Buffers.Binary;
using UniversalRGB.Adapters.OpenRGB;
using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Interfaces;
using UniversalRGB.Core.Lighting;

namespace UniversalRGB.Tests;

public class OpenRgbSerializerTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    public void Controller_round_trips_at_every_protocol_version(uint protocol)
    {
        var original = TestData.Motherboard(argbLeds: 12);
        var bytes = OpenRgbSerializer.SerializeController(original, protocol);

        Assert.Equal((uint)bytes.Length, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        var parsed = OpenRgbSerializer.ParseController(bytes, protocol);

        Assert.Equal(original.Name, parsed.Name);
        Assert.Equal(protocol >= 1 ? original.Vendor : "", parsed.Vendor);
        Assert.Equal(original.Location, parsed.Location);
        Assert.Equal(original.Modes.Count, parsed.Modes.Count);
        Assert.Equal(original.Modes.Select(m => m.Name), parsed.Modes.Select(m => m.Name));
        Assert.Equal(original.Modes[2].SpeedMin, parsed.Modes[2].SpeedMin);
        Assert.Equal(protocol >= 3 ? original.Modes[2].BrightnessMax : 0u, parsed.Modes[2].BrightnessMax);
        Assert.Equal(original.Modes[1].Colors, parsed.Modes[1].Colors);
        Assert.Equal(2, parsed.Zones.Count);
        Assert.Equal(3, parsed.Zones[1].StartIndex);
        Assert.Equal(12, parsed.Zones[1].LedCount);
        Assert.Equal(120, parsed.Zones[1].LedsMax);
        Assert.Equal(15, parsed.Leds.Count);
        Assert.Equal(15, parsed.Colors.Count);
    }

    [Fact]
    public void Matrix_zone_round_trips()
    {
        var bytes = OpenRgbSerializer.SerializeController(TestData.Keyboard(), 4);
        var parsed = OpenRgbSerializer.ParseController(bytes, 4);
        var matrix = parsed.Zones[0].Matrix;
        Assert.NotNull(matrix);
        Assert.Equal(3, matrix.Width);
        Assert.Equal(2, matrix.Height);
        Assert.Equal(RgbMatrix.NoLed, matrix.Map[5]);
    }

    [Fact]
    public void Truncated_controller_data_is_rejected()
    {
        var bytes = OpenRgbSerializer.SerializeController(TestData.Keyboard(), 4);
        Assert.Throws<InvalidDataException>(() => OpenRgbSerializer.ParseController(bytes.AsSpan(0, bytes.Length - 3), 4));
    }

    [Fact]
    public void UpdateLeds_payload_matches_server_expectations()
    {
        var payload = OpenRgbSerializer.UpdateLeds([new RgbColor(0x11, 0x22, 0x33), new RgbColor(1, 2, 3)]);

        Assert.Equal(4 + 2 + 8, payload.Length);
        Assert.Equal((uint)payload.Length, BinaryPrimitives.ReadUInt32LittleEndian(payload)); // data_size == packet size
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4)));
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x00 }, payload[6..10]); // R, G, B, 0
    }

    [Fact]
    public void UpdateZoneLeds_payload_has_zone_index_and_size()
    {
        var payload = OpenRgbSerializer.UpdateZoneLeds(3, [RgbColor.White]);
        Assert.Equal((uint)payload.Length, BinaryPrimitives.ReadUInt32LittleEndian(payload));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8)));
    }

    [Fact]
    public void UpdateMode_payload_starts_with_size_and_mode_index()
    {
        var mode = TestData.Motherboard().Modes[2];
        var payload = OpenRgbSerializer.UpdateMode(2, mode, 4);
        Assert.Equal((uint)payload.Length, BinaryPrimitives.ReadUInt32LittleEndian(payload));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4)));

        var reader = new PacketReader(payload.AsSpan(8));
        var decoded = OpenRgbSerializer.ReadMode(ref reader, 2, 4);
        Assert.Equal(mode.Name, decoded.Name);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void ResizeZone_payload_is_two_ints() =>
        Assert.Equal(8, OpenRgbSerializer.ResizeZone(1, 30).Length);

    [Fact]
    public void Header_has_magic_and_little_endian_fields()
    {
        var header = PacketWriter.Header(7, OpenRgbProtocol.RgbControllerUpdateLeds, 42);
        Assert.Equal("ORGB"u8.ToArray(), header[..4]);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        Assert.Equal(1050u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)));
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)));
    }
}

public class OpenRgbAdapterTests
{
    private static OpenRgbAdapter Adapter(FakeOpenRgbServer server) => new(() => ("127.0.0.1", server.Port));

    [Fact]
    public async Task Connects_negotiates_protocol_and_discovers_devices()
    {
        await using var server = new FakeOpenRgbServer(TestData.Motherboard(argbLeds: 8), TestData.Keyboard());
        await using var adapter = Adapter(server);

        Assert.True(await adapter.ConnectAsync(CancellationToken.None));
        Assert.Equal(AdapterConnectionState.Connected, adapter.State);
        Assert.Equal(OpenRgbProtocol.ClientProtocolVersion, adapter.ProtocolVersion);
        Assert.Equal(4u, server.NegotiatedProtocol);

        var devices = await adapter.DiscoverAsync(CancellationToken.None);
        Assert.Equal(2, devices.Count);
        Assert.Equal("ASUS PRIME B550M-A WIFI II", devices[0].Name);
        Assert.Equal("0B05:19AF", devices[0].Identity?.Key);
        Assert.Equal(11, devices[0].LedCount);
        Assert.Equal(RgbDeviceType.Keyboard, devices[1].Type);
        Assert.Equal("Universal RGB Control", server.ClientName);
    }

    [Fact]
    public async Task Works_with_servers_that_do_not_answer_protocol_requests()
    {
        await using var server = new FakeOpenRgbServer(TestData.Keyboard()) { AnswersProtocolRequest = false };
        await using var adapter = Adapter(server);

        Assert.True(await adapter.ConnectAsync(CancellationToken.None));
        Assert.Equal(0u, adapter.ProtocolVersion);
        var devices = await adapter.DiscoverAsync(CancellationToken.None);
        Assert.Single(devices);
        Assert.Equal("", devices[0].Vendor); // vendor only exists from protocol 1
    }

    [Fact]
    public async Task Connection_refused_reports_disconnected_without_throwing()
    {
        await using var adapter = new OpenRgbAdapter(() => ("127.0.0.1", 1));
        Assert.False(await adapter.ConnectAsync(CancellationToken.None));
        Assert.Equal(AdapterConnectionState.Disconnected, adapter.State);
        Assert.Contains("No OpenRGB SDK server", adapter.StateDetail);
    }

    [Fact]
    public async Task Sends_colors_for_whole_device_and_single_zone()
    {
        await using var server = new FakeOpenRgbServer(TestData.Motherboard(argbLeds: 2));
        await using var adapter = Adapter(server);
        await adapter.ConnectAsync(CancellationToken.None);
        var device = (await adapter.DiscoverAsync(CancellationToken.None))[0];

        await adapter.PrepareSoftwareControlAsync(device.Id, CancellationToken.None);
        await adapter.SetColorAsync(device.Id, RgbZone.AllZones, new RgbColor(10, 20, 30), CancellationToken.None);
        var all = await server.WaitForAsync(OpenRgbProtocol.RgbControllerUpdateLeds);
        Assert.Equal(5, BinaryPrimitives.ReadUInt16LittleEndian(all.Payload.AsSpan(4)));
        Assert.Equal(new byte[] { 10, 20, 30, 0 }, all.Payload[6..10]);

        await adapter.SetColorsAsync(device.Id, "1", [RgbColor.White, RgbColor.White], CancellationToken.None);
        var zone = await server.WaitForAsync(OpenRgbProtocol.RgbControllerUpdateZoneLeds);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(zone.Payload.AsSpan(4)));
        Assert.Contains(server.Received, p => p.PacketId == OpenRgbProtocol.RgbControllerSetCustomMode);
    }

    [Fact]
    public async Task Hardware_effect_uses_matching_mode_and_device_limits()
    {
        await using var server = new FakeOpenRgbServer(TestData.Motherboard());
        await using var adapter = Adapter(server);
        await adapter.ConnectAsync(CancellationToken.None);
        var device = (await adapter.DiscoverAsync(CancellationToken.None))[0];

        var effect = new LightingEffect { Kind = EffectKind.Breathing, Speed = 100, Colors = [new RgbColor(255, 0, 0)] };
        Assert.True(await adapter.SetEffectAsync(device.Id, RgbZone.AllZones, effect, CancellationToken.None));

        var packet = await server.WaitForAsync(OpenRgbProtocol.RgbControllerUpdateMode);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.AsSpan(4)));
        var reader = new PacketReader(packet.Payload.AsSpan(8));
        var mode = OpenRgbSerializer.ReadMode(ref reader, 2, 4);
        Assert.Equal(0u, mode.Speed); // inverted range 4..0 → fastest is 0
        Assert.Equal(ModeColorMode.ModeSpecific, mode.ColorMode);
        Assert.Equal(new RgbColor(255, 0, 0), mode.Colors[0]);

        var music = new LightingEffect { Kind = EffectKind.MusicReactive };
        Assert.False(await adapter.SetEffectAsync(device.Id, RgbZone.AllZones, music, CancellationToken.None));
    }

    [Fact]
    public async Task Resize_validates_range_and_triggers_device_list_update()
    {
        await using var server = new FakeOpenRgbServer(TestData.Motherboard(argbLeds: 0));
        await using var adapter = Adapter(server);
        await adapter.ConnectAsync(CancellationToken.None);
        var device = (await adapter.DiscoverAsync(CancellationToken.None))[0];

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => adapter.ResizeZoneAsync(device.Id, "1", 500, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ResizeZoneAsync(device.Id, "0", 5, CancellationToken.None));

        var changed = new TaskCompletionSource();
        adapter.DevicesChanged += (_, _) => changed.TrySetResult();
        await adapter.ResizeZoneAsync(device.Id, "1", 30, CancellationToken.None);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var refreshed = (await adapter.DiscoverAsync(CancellationToken.None))[0];
        Assert.Equal(33, refreshed.LedCount);
    }

    [Fact]
    public async Task Server_device_list_update_is_forwarded()
    {
        await using var server = new FakeOpenRgbServer(TestData.Keyboard());
        await using var adapter = Adapter(server);
        await adapter.ConnectAsync(CancellationToken.None);

        var changed = new TaskCompletionSource();
        adapter.DevicesChanged += (_, _) => changed.TrySetResult();
        await server.SendDeviceListUpdatedAsync();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Dropped_connection_moves_to_disconnected()
    {
        await using var server = new FakeOpenRgbServer(TestData.Keyboard());
        await using var adapter = Adapter(server);
        await adapter.ConnectAsync(CancellationToken.None);

        var lost = new TaskCompletionSource();
        adapter.StateChanged += (_, _) =>
        {
            if (adapter.State == AdapterConnectionState.Disconnected)
            {
                lost.TrySetResult();
            }
        };
        server.DropClient();
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<IOException>(() => adapter.DiscoverAsync(CancellationToken.None));
    }
}
