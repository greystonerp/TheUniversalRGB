namespace UniversalRGB.Core.Lighting;

/// <summary>Live inputs for reactive effects.</summary>
public readonly record struct RenderSignals(double? TemperatureC, double AudioLevel)
{
    public static RenderSignals None => new(null, 0);
}

/// <summary>A trusted CPU/GPU temperature reading. Implementations must never install drivers.</summary>
public interface ITemperatureSource
{
    string SourceName { get; }

    /// <summary>Latest reading in °C, or null when no trustworthy reading is available.</summary>
    double? CurrentTemperatureC { get; }

    void Start();

    void Stop();
}

/// <summary>System audio output level (loopback), 0..1.</summary>
public interface IAudioLevelSource
{
    bool IsAvailable { get; }

    double Level { get; }

    void Start();

    void Stop();
}
