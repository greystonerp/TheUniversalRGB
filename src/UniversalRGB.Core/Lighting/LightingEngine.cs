using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Diagnostics;
using UniversalRGB.Core.Profiles;

namespace UniversalRGB.Core.Lighting;

public sealed class LightingFrame : EventArgs
{
    public LightingFrame(IReadOnlyDictionary<string, RgbColor[]> colors, double timeSeconds)
    {
        Colors = colors;
        TimeSeconds = timeSeconds;
    }

    /// <summary>Rendered colors per device id (device-wide LED order).</summary>
    public IReadOnlyDictionary<string, RgbColor[]> Colors { get; }

    public double TimeSeconds { get; }
}

public sealed class DeviceNoticeEventArgs : EventArgs
{
    public DeviceNoticeEventArgs(string deviceId, string deviceName, string message, bool isError)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        Message = message;
        IsError = isError;
    }

    public string DeviceId { get; }
    public string DeviceName { get; }
    public string Message { get; }
    public bool IsError { get; }
}

/// <summary>
/// Renders the active profile and drives devices through their adapters.
/// <list type="bullet">
/// <item>Direct-mode devices are streamed at the configured frame rate (only changed frames are sent).</item>
/// <item>Per-LED devices without Direct mode only receive colors when they change, never a stream.</item>
/// <item>Devices with hardware effects only get the closest matching built-in mode.</item>
/// </list>
/// The engine never touches fan or pump speed; it only calls lighting operations.
/// </summary>
public sealed class LightingEngine : IAsyncDisposable
{
    private static readonly TimeSpan StaticRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly DeviceRegistry _registry;
    private readonly ITemperatureSource? _temperature;
    private readonly IAudioLevelSource? _audio;
    private readonly IAppLog _log;
    private readonly ConcurrentDictionary<string, DeviceRuntime> _runtime = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _identify = new(StringComparer.Ordinal);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private LightingProfile? _profile;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _frameRate = 30;

    public LightingEngine(DeviceRegistry registry, ITemperatureSource? temperature = null, IAudioLevelSource? audio = null, IAppLog? log = null)
    {
        _registry = registry;
        _temperature = temperature;
        _audio = audio;
        _log = log ?? NullLog.Instance;
        _registry.DevicesChanged += (_, _) => PruneRuntime();
    }

    public event EventHandler<LightingFrame>? FrameRendered;

    public event EventHandler<DeviceNoticeEventArgs>? DeviceNotice;

    public int FrameRate
    {
        get => _frameRate;
        set => _frameRate = Math.Clamp(value, 5, 60);
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    public LightingProfile? ActiveProfile => Volatile.Read(ref _profile);

    public double CurrentTimeSeconds => _clock.Elapsed.TotalSeconds;

    /// <summary>Activates a profile. A private copy is taken, so later edits require another Apply.</summary>
    public void Apply(LightingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Volatile.Write(ref _profile, profile.Clone());
        foreach (var runtime in _runtime.Values)
        {
            runtime.HardwareKey = null;
            runtime.NextRetryUtc = DateTime.MinValue;
        }

        UpdateSignalSources();
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token), token);
        UpdateSignalSources();
        _log.Info($"Lighting engine started at {FrameRate} fps.");
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        if (cts is null || loop is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        cts.Dispose();
        _cts = null;
        _loop = null;
        _temperature?.Stop();
        _audio?.Stop();
        _log.Info("Lighting engine stopped.");
    }

    /// <summary>Blinks a Direct-mode device white so the user can find it. Returns false if unsupported.</summary>
    public bool Identify(string deviceId, TimeSpan duration)
    {
        var device = _registry.Find(deviceId);
        if (device is null || DeviceCapabilities.From(device).Strategy != ControlStrategy.Streaming)
        {
            return false;
        }

        _identify[deviceId] = DateTime.UtcNow + duration;
        return true;
    }

    /// <summary>Renders what a device would show at time t for the given profile (used for previews).</summary>
    public static RgbColor[] RenderDevice(RgbDevice device, LightingProfile profile, double timeSeconds, in RenderSignals signals)
    {
        var colors = new RgbColor[device.LedCount];
        foreach (var zone in device.Zones)
        {
            if (zone.LedCount <= 0 || zone.StartIndex < 0 || zone.StartIndex + zone.LedCount > colors.Length)
            {
                continue;
            }

            var effect = profile.ResolveEffect(device.Id, zone.Name);
            EffectRenderer.Render(effect, timeSeconds, colors.AsSpan(zone.StartIndex, zone.LedCount), signals);
        }

        EffectRenderer.ApplyBrightness(colors, profile.MasterBrightness / 100.0);
        return colors;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var frameStart = _clock.Elapsed;
            try
            {
                await RenderFrameAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error("Lighting frame failed.", ex);
            }

            var budget = TimeSpan.FromSeconds(1.0 / FrameRate);
            var remaining = budget - (_clock.Elapsed - frameStart);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, token).ConfigureAwait(false);
            }
        }
    }

    private async Task RenderFrameAsync(CancellationToken token)
    {
        var profile = Volatile.Read(ref _profile);
        if (profile is null)
        {
            return;
        }

        var devices = _registry.Devices;
        if (devices.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var t = _clock.Elapsed.TotalSeconds;
        var signals = new RenderSignals(_temperature?.CurrentTemperatureC, _audio?.Level ?? 0);
        var frame = new Dictionary<string, RgbColor[]>(devices.Count, StringComparer.Ordinal);

        foreach (var device in devices)
        {
            token.ThrowIfCancellationRequested();
            if (!profile.IsDeviceEnabled(device.Id))
            {
                continue;
            }

            var runtime = _runtime.GetOrAdd(device.Id, _ => new DeviceRuntime());
            runtime.Sync(device);

            var identifying = _identify.TryGetValue(device.Id, out var until) && until > now;
            if (!identifying)
            {
                _identify.TryRemove(device.Id, out _);
            }

            RgbColor[] colors;
            bool animated;
            if (identifying)
            {
                colors = new RgbColor[device.LedCount];
                Array.Fill(colors, ((int)(t * 4) % 2 == 0) ? RgbColor.White : RgbColor.Black);
                animated = true;
            }
            else
            {
                colors = RenderDevice(device, profile, t, signals);
                animated = device.Zones.Any(z => z.LedCount > 0 && profile.ResolveEffect(device.Id, z.Name).IsAnimated);
            }

            frame[device.Id] = colors;

            if (now < runtime.NextRetryUtc)
            {
                continue;
            }

            var adapter = _registry.GetAdapter(device.Id);
            if (adapter is null)
            {
                continue;
            }

            try
            {
                switch (runtime.Capabilities!.Strategy)
                {
                    case ControlStrategy.Streaming:
                    {
                        if (!runtime.Prepared)
                        {
                            await adapter.PrepareSoftwareControlAsync(device.Id, token).ConfigureAwait(false);
                            runtime.Prepared = true;
                            runtime.LastSent = null;
                        }

                        var changed = runtime.LastSent is null || !colors.AsSpan().SequenceEqual(runtime.LastSent);
                        var refresh = !animated && now - runtime.LastSendUtc > StaticRefreshInterval;
                        if (changed || refresh)
                        {
                            await adapter.SetColorsAsync(device.Id, RgbZone.AllZones, colors, token).ConfigureAwait(false);
                            runtime.LastSent = colors;
                            runtime.LastSendUtc = now;
                        }

                        break;
                    }

                    case ControlStrategy.StaticOnly:
                        await DriveStaticOnlyAsync(adapter, device, profile, colors, animated, runtime, token).ConfigureAwait(false);
                        break;

                    case ControlStrategy.HardwareOnly:
                        await DriveHardwareAsync(adapter, device, profile, runtime, token).ConfigureAwait(false);
                        break;

                    case ControlStrategy.None:
                        break;
                }

                runtime.Failures = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                runtime.Failures++;
                runtime.Prepared = false;
                runtime.LastSent = null;
                runtime.HardwareKey = null;
                runtime.NextRetryUtc = now + TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, runtime.Failures)));
                if (runtime.Failures == 1)
                {
                    _log.Warn($"Lighting update failed for {device.Name}.", ex);
                    DeviceNotice?.Invoke(this, new DeviceNoticeEventArgs(device.Id, device.Name, ex.Message, isError: true));
                }
            }
        }

        FrameRendered?.Invoke(this, new LightingFrame(frame, t));
    }

    private async Task DriveStaticOnlyAsync(
        Interfaces.IRgbDeviceAdapter adapter,
        RgbDevice device,
        LightingProfile profile,
        RgbColor[] colors,
        bool animated,
        DeviceRuntime runtime,
        CancellationToken token)
    {
        if (animated)
        {
            // Never stream to a device without Direct mode: try a matching hardware effect instead.
            var effect = profile.ResolveEffect(device.Id, null);
            var key = "hw:" + JsonSerializer.Serialize(effect, ProfileJson.Options) + profile.MasterBrightness;
            if (runtime.HardwareKey == key)
            {
                return;
            }

            runtime.HardwareKey = key;
            runtime.Prepared = false;
            runtime.LastSent = null;
            var applied = await adapter.SetEffectAsync(device.Id, RgbZone.AllZones, ScaleBrightness(effect, profile), token).ConfigureAwait(false);
            if (!applied)
            {
                var still = new RgbColor[device.LedCount];
                Array.Fill(still, effect.PrimaryColor.Scale(effect.Brightness / 100.0 * profile.MasterBrightness / 100.0));
                await SendStaticAsync(adapter, device, still, runtime, token).ConfigureAwait(false);
                DeviceNotice?.Invoke(this, new DeviceNoticeEventArgs(device.Id, device.Name,
                    $"{LightingEffect.DisplayName(effect.Kind)} needs Direct mode; this device shows a static color instead.", isError: false));
            }

            return;
        }

        runtime.HardwareKey = null;
        if (runtime.LastSent is not null && colors.AsSpan().SequenceEqual(runtime.LastSent))
        {
            return;
        }

        await SendStaticAsync(adapter, device, colors, runtime, token).ConfigureAwait(false);
    }

    private static async Task SendStaticAsync(Interfaces.IRgbDeviceAdapter adapter, RgbDevice device, RgbColor[] colors, DeviceRuntime runtime, CancellationToken token)
    {
        if (!runtime.Prepared)
        {
            await adapter.PrepareSoftwareControlAsync(device.Id, token).ConfigureAwait(false);
            runtime.Prepared = true;
        }

        await adapter.SetColorsAsync(device.Id, RgbZone.AllZones, colors, token).ConfigureAwait(false);
        runtime.LastSent = colors;
        runtime.LastSendUtc = DateTime.UtcNow;
    }

    private async Task DriveHardwareAsync(Interfaces.IRgbDeviceAdapter adapter, RgbDevice device, LightingProfile profile, DeviceRuntime runtime, CancellationToken token)
    {
        var effect = profile.ResolveEffect(device.Id, null);
        var key = "hw:" + JsonSerializer.Serialize(effect, ProfileJson.Options) + profile.MasterBrightness;
        if (runtime.HardwareKey == key)
        {
            return;
        }

        runtime.HardwareKey = key;
        var applied = await adapter.SetEffectAsync(device.Id, RgbZone.AllZones, ScaleBrightness(effect, profile), token).ConfigureAwait(false);
        if (!applied)
        {
            DeviceNotice?.Invoke(this, new DeviceNoticeEventArgs(device.Id, device.Name,
                $"No built-in mode matches {LightingEffect.DisplayName(effect.Kind)} on this device.", isError: false));
        }
    }

    private static LightingEffect ScaleBrightness(LightingEffect effect, LightingProfile profile) =>
        effect with { Brightness = effect.Brightness * profile.MasterBrightness / 100.0 };

    private void UpdateSignalSources()
    {
        var profile = Volatile.Read(ref _profile);
        if (profile is null || !IsRunning)
        {
            return;
        }

        var kinds = new HashSet<EffectKind> { profile.GlobalEffect.Kind };
        if (!profile.SyncAll)
        {
            foreach (var setting in profile.Devices.Values)
            {
                if (setting.Effect is not null)
                {
                    kinds.Add(setting.Effect.Kind);
                }

                foreach (var zoneEffect in setting.Zones.Values)
                {
                    kinds.Add(zoneEffect.Kind);
                }
            }
        }

        if (kinds.Contains(EffectKind.Temperature))
        {
            _temperature?.Start();
        }
        else
        {
            _temperature?.Stop();
        }

        if (kinds.Contains(EffectKind.MusicReactive))
        {
            _audio?.Start();
        }
        else
        {
            _audio?.Stop();
        }
    }

    private void PruneRuntime()
    {
        var present = _registry.Devices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _runtime.Keys)
        {
            if (!present.Contains(id))
            {
                _runtime.TryRemove(id, out _);
            }
        }
    }

    private sealed class DeviceRuntime
    {
        private string? _signature;

        public DeviceCapabilities? Capabilities { get; private set; }
        public bool Prepared { get; set; }
        public RgbColor[]? LastSent { get; set; }
        public DateTime LastSendUtc { get; set; }
        public string? HardwareKey { get; set; }
        public int Failures { get; set; }
        public DateTime NextRetryUtc { get; set; }

        /// <summary>Resets cached state when the device layout changes (resize, reconnect, mode list).</summary>
        public void Sync(RgbDevice device)
        {
            var signature = $"{device.LedCount}|{device.Modes.Count}|{string.Join(',', device.Zones.Select(z => z.LedCount))}";
            if (signature == _signature)
            {
                return;
            }

            _signature = signature;
            Capabilities = DeviceCapabilities.From(device);
            Prepared = false;
            LastSent = null;
            HardwareKey = null;
            Failures = 0;
            NextRetryUtc = DateTime.MinValue;
        }
    }
}
