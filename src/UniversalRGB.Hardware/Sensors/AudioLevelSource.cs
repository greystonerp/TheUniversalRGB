using NAudio.Wave;
using UniversalRGB.Core.Diagnostics;
using UniversalRGB.Core.Lighting;

namespace UniversalRGB.Hardware.Sensors;

/// <summary>
/// Measures the level of what the PC is playing (WASAPI loopback of the default output device).
/// No microphone is opened and no audio is stored; only a smoothed peak level is kept.
/// </summary>
public sealed class AudioLevelSource : IAudioLevelSource, IDisposable
{
    private readonly IAppLog _log;
    private readonly Lock _gate = new();
    private WasapiLoopbackCapture? _capture;
    private double _level;
    private double _peakHold = 0.2;
    private bool _available = true;

    public AudioLevelSource(IAppLog? log = null) => _log = log ?? NullLog.Instance;

    public bool IsAvailable => _available;

    public double Level => Volatile.Read(ref _level);

    public void Start()
    {
        lock (_gate)
        {
            if (_capture is not null)
            {
                return;
            }

            try
            {
                var capture = new WasapiLoopbackCapture();
                capture.DataAvailable += OnData;
                capture.RecordingStopped += (_, e) =>
                {
                    if (e.Exception is not null)
                    {
                        _log.Warn("Audio loopback capture stopped.", e.Exception);
                    }
                };
                capture.StartRecording();
                _capture = capture;
                _available = true;
                _log.Info("Audio level capture started (loopback).");
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                _available = false;
                _log.Warn("Audio loopback capture is unavailable.", ex);
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_capture is null)
            {
                return;
            }

            try
            {
                _capture.DataAvailable -= OnData;
                _capture.StopRecording();
                _capture.Dispose();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                _log.Warn("Error while stopping audio capture.", ex);
            }

            _capture = null;
            Volatile.Write(ref _level, 0);
        }
    }

    public void Dispose() => Stop();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var format = (sender as WasapiLoopbackCapture)?.WaveFormat;
        if (format is null || e.BytesRecorded == 0)
        {
            return;
        }

        double peak = 0;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32))
        {
            for (var i = 0; i + 4 <= e.BytesRecorded; i += 4)
            {
                var sample = Math.Abs(BitConverter.ToSingle(e.Buffer, i));
                if (sample > peak)
                {
                    peak = sample;
                }
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (var i = 0; i + 2 <= e.BytesRecorded; i += 2)
            {
                var sample = Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768.0);
                if (sample > peak)
                {
                    peak = sample;
                }
            }
        }

        // Auto-gain: normalize against a slowly decaying recent peak so quiet and loud content both move the lights.
        _peakHold = Math.Max(peak, _peakHold * 0.995);
        var normalized = _peakHold > 0.01 ? Math.Clamp(peak / _peakHold, 0, 1) * Math.Min(1, _peakHold * 4) : 0;

        // Fast attack, slower release.
        var current = Volatile.Read(ref _level);
        var next = normalized > current ? (current * 0.3) + (normalized * 0.7) : (current * 0.85) + (normalized * 0.15);
        Volatile.Write(ref _level, next);
    }
}
