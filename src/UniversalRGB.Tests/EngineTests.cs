using System.Collections.Concurrent;
using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Interfaces;
using UniversalRGB.Core.Lighting;
using UniversalRGB.Core.Profiles;

namespace UniversalRGB.Tests;

/// <summary>Records every call so tests can assert exactly what would reach hardware.</summary>
internal sealed class RecordingAdapter : IRgbDeviceAdapter
{
    private readonly List<RgbDevice> _devices;

    public RecordingAdapter(params RgbDevice[] devices) => _devices = devices.ToList();

    public ConcurrentQueue<string> Calls { get; } = new();

    public ConcurrentQueue<(string Device, RgbColor[] Colors)> Frames { get; } = new();

    public bool HardwareEffectResult { get; set; } = true;

    public string AdapterName => "Test";

    public AdapterConnectionState State => AdapterConnectionState.Connected;

    public string StateDetail => "test";

    public event EventHandler? StateChanged { add { } remove { } }

    public event EventHandler? DevicesChanged { add { } remove { } }

    public Task<bool> ConnectAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task DisconnectAsync() => Task.CompletedTask;

    public Task<IReadOnlyList<RgbDevice>> DiscoverAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RgbDevice>>(_devices);

    public Task<DeviceCapabilities> GetCapabilitiesAsync(string deviceId, CancellationToken cancellationToken) =>
        Task.FromResult(DeviceCapabilities.From(_devices.Single(d => d.Id == deviceId)));

    public Task PrepareSoftwareControlAsync(string deviceId, CancellationToken cancellationToken)
    {
        Calls.Enqueue($"prepare:{deviceId}");
        return Task.CompletedTask;
    }

    public Task SetColorAsync(string deviceId, string zoneId, RgbColor color, CancellationToken cancellationToken)
    {
        Calls.Enqueue($"color:{deviceId}");
        return Task.CompletedTask;
    }

    public Task SetColorsAsync(string deviceId, string zoneId, IReadOnlyList<RgbColor> colors, CancellationToken cancellationToken)
    {
        Calls.Enqueue($"colors:{deviceId}");
        Frames.Enqueue((deviceId, colors.ToArray()));
        return Task.CompletedTask;
    }

    public Task<bool> SetEffectAsync(string deviceId, string zoneId, LightingEffect effect, CancellationToken cancellationToken)
    {
        Calls.Enqueue($"effect:{deviceId}:{effect.Kind}");
        return Task.FromResult(HardwareEffectResult);
    }

    public Task ResizeZoneAsync(string deviceId, string zoneId, int ledCount, CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public int Count(string prefix) => Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));
}

public class LightingEngineTests
{
    private static async Task<(LightingEngine Engine, RecordingAdapter Adapter)> StartAsync(params RgbDevice[] devices)
    {
        var adapter = new RecordingAdapter(devices);
        var registry = new DeviceRegistry();
        registry.AddAdapter(adapter);
        await registry.RefreshAsync(CancellationToken.None);
        var engine = new LightingEngine(registry) { FrameRate = 60 };
        return (engine, adapter);
    }

    private static async Task RunFor(LightingEngine engine, LightingProfile profile, int milliseconds = 350)
    {
        engine.Apply(profile);
        engine.Start();
        await Task.Delay(milliseconds);
        await engine.StopAsync();
    }

    [Fact]
    public async Task Static_effect_is_sent_once_to_direct_devices()
    {
        var device = TestData.Device(TestData.Motherboard(argbLeds: 4));
        var (engine, adapter) = await StartAsync(device);

        await RunFor(engine, new LightingProfile { GlobalEffect = LightingEffect.StaticColor(new RgbColor(10, 20, 30)) });

        Assert.Equal(1, adapter.Count("prepare:"));
        Assert.Equal(1, adapter.Count("colors:"));
        var frame = Assert.Single(adapter.Frames);
        Assert.Equal(7, frame.Colors.Length);
        Assert.All(frame.Colors, c => Assert.Equal(new RgbColor(10, 20, 30), c));
    }

    [Fact]
    public async Task Animated_effect_streams_to_direct_devices()
    {
        var device = TestData.Device(TestData.Keyboard());
        var (engine, adapter) = await StartAsync(device);

        await RunFor(engine, new LightingProfile { GlobalEffect = new LightingEffect { Kind = EffectKind.RainbowWave, Speed = 100 } });

        Assert.True(adapter.Count("colors:") > 5, $"Expected a stream of frames, got {adapter.Count("colors:")}.");
        Assert.Equal(0, adapter.Count("effect:"));
    }

    [Fact]
    public async Task Animated_effect_is_never_streamed_to_devices_without_direct_mode()
    {
        var device = TestData.Device(TestData.Motherboard(argbLeds: 0, withDirect: false));
        var (engine, adapter) = await StartAsync(device);

        await RunFor(engine, new LightingProfile { GlobalEffect = new LightingEffect { Kind = EffectKind.Breathing } });

        Assert.Equal(1, adapter.Count("effect:"));
        Assert.Equal(0, adapter.Count("colors:"));
    }

    [Fact]
    public async Task Static_only_device_gets_static_fallback_when_no_hardware_mode_matches()
    {
        var device = TestData.Device(TestData.Motherboard(argbLeds: 0, withDirect: false));
        var (engine, adapter) = await StartAsync(device);
        adapter.HardwareEffectResult = false;
        var notices = new List<string>();
        engine.DeviceNotice += (_, e) => notices.Add(e.Message);

        await RunFor(engine, new LightingProfile { GlobalEffect = new LightingEffect { Kind = EffectKind.Comet } });

        Assert.Equal(1, adapter.Count("effect:"));
        Assert.Equal(1, adapter.Count("colors:"));
        Assert.Contains(notices, n => n.Contains("static color"));
    }

    [Fact]
    public async Task Disabled_devices_are_left_alone()
    {
        var device = TestData.Device(TestData.Keyboard());
        var (engine, adapter) = await StartAsync(device);
        var profile = new LightingProfile { SyncAll = true, GlobalEffect = new LightingEffect { Kind = EffectKind.RainbowWave } };
        profile.Devices[device.Id] = new DeviceLightingSetting { Enabled = false };

        await RunFor(engine, profile, 200);

        Assert.Empty(adapter.Calls);
    }

    [Fact]
    public async Task Master_brightness_scales_output()
    {
        var device = TestData.Device(TestData.Keyboard());
        var (engine, adapter) = await StartAsync(device);

        await RunFor(engine, new LightingProfile { MasterBrightness = 50, GlobalEffect = LightingEffect.StaticColor(new RgbColor(200, 100, 50)) });

        Assert.All(adapter.Frames.Single().Colors, c => Assert.Equal(new RgbColor(100, 50, 25), c));
    }

    [Fact]
    public async Task Frames_are_published_for_previews()
    {
        var device = TestData.Device(TestData.Keyboard());
        var (engine, _) = await StartAsync(device);
        LightingFrame? last = null;
        engine.FrameRendered += (_, f) => last = f;

        await RunFor(engine, new LightingProfile { GlobalEffect = new LightingEffect { Kind = EffectKind.SpectrumCycle } }, 200);

        Assert.NotNull(last);
        Assert.Equal(6, last.Colors[device.Id].Length);
    }
}
