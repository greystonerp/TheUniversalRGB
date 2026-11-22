namespace UniversalRGB.Core.Diagnostics;

/// <summary>Minimal logging abstraction so the core libraries stay free of a logging framework.</summary>
public interface IAppLog
{
    void Info(string message);

    void Warn(string message, Exception? exception = null);

    void Error(string message, Exception? exception = null);
}

public sealed class NullLog : IAppLog
{
    public static readonly NullLog Instance = new();

    public void Info(string message)
    {
    }

    public void Warn(string message, Exception? exception = null)
    {
    }

    public void Error(string message, Exception? exception = null)
    {
    }
}
