using Serilog;
using UniversalRGB.Core.Diagnostics;

namespace UniversalRGB.Desktop.Services;

/// <summary>Serilog-backed log written to %LOCALAPPDATA%\UniversalRGB\logs (14 days retained).</summary>
public sealed class AppLog : IAppLog, IDisposable
{
    private readonly ILogger _logger;

    public AppLog(string directory)
    {
        Directory.CreateDirectory(directory);
        _logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(directory, "universalrgb-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true, // setup, uninstaller and the app may log at the same time
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    public void Info(string message) => _logger.Information("{Message}", message);

    public void Warn(string message, Exception? exception = null) => _logger.Warning(exception, "{Message}", message);

    public void Error(string message, Exception? exception = null) => _logger.Error(exception, "{Message}", message);

    public void Dispose() => (_logger as IDisposable)?.Dispose();
}
