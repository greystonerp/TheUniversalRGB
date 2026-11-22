using System.Diagnostics;
using System.Globalization;
using UniversalRGB.Core.Diagnostics;
using UniversalRGB.Core.Lighting;

namespace UniversalRGB.Hardware.Sensors;

/// <summary>
/// Reads GPU temperature through nvidia-smi, the monitoring tool installed with NVIDIA's own driver.
/// No kernel driver is loaded or installed by this application. Readings outside 0–125 °C are rejected.
/// </summary>
public sealed class GpuTemperatureSource : ITemperatureSource, IDisposable
{
    private readonly IAppLog _log;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(2);
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private double? _latest;
    private DateTime _latestAt;
    private string? _tool;
    private bool _searched;

    public GpuTemperatureSource(IAppLog? log = null) => _log = log ?? NullLog.Instance;

    public string SourceName => _tool is null ? "GPU temperature (nvidia-smi not found)" : "NVIDIA GPU (nvidia-smi)";

    public bool IsAvailable
    {
        get
        {
            EnsureTool();
            return _tool is not null;
        }
    }

    /// <summary>Latest reading, or null when there is none newer than 10 seconds.</summary>
    public double? CurrentTemperatureC => DateTime.UtcNow - _latestAt < TimeSpan.FromSeconds(10) ? _latest : null;

    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null)
            {
                return;
            }

            EnsureTool();
            if (_tool is null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(() => PollAsync(token), token);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Dispose() => Stop();

    /// <summary>Takes a single reading synchronously (used by diagnostics).</summary>
    public double? ReadOnce()
    {
        EnsureTool();
        return _tool is null ? null : Query(_tool);
    }

    private async Task PollAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var value = Query(_tool!);
            if (value is not null)
            {
                _latest = value;
                _latestAt = DateTime.UtcNow;
            }

            try
            {
                await Task.Delay(_interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private double? Query(string tool)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(tool, "--query-gpu=temperature.gpu --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                process.Kill();
                return null;
            }

            var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var celsius) && celsius is > 0 and < 125
                ? celsius
                : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _log.Warn("nvidia-smi query failed.", ex);
            return null;
        }
    }

    private void EnsureTool()
    {
        if (_searched)
        {
            return;
        }

        _searched = true;
        var candidates = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        };
        _tool = candidates.FirstOrDefault(File.Exists);
    }
}
