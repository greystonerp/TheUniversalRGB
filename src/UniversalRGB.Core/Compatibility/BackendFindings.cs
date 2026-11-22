namespace UniversalRGB.Core.Compatibility;

/// <summary>Why the backend skipped a motherboard lighting controller (from its detection log).</summary>
public enum BoardControllerIssue
{
    None,

    /// <summary>The controller's firmware is older than the backend supports.</summary>
    FirmwareTooOld,

    /// <summary>The controller did not answer the detection probe (often another RGB app holds it).</summary>
    NoResponse,

    /// <summary>The controller answered, but the backend has no lighting layout for this board.</summary>
    NoBoardConfig,
}

/// <summary>Facts read from the RGB backend's own detection log.</summary>
public sealed record BackendFindings
{
    public string? Version { get; init; }

    public DateTime? StartedAt { get; init; }

    /// <summary>SMBus/I2C access failed (OpenRGB could not open PawnIO: not elevated, or the driver is missing).</summary>
    public bool SmbusUnavailable { get; init; }

    /// <summary>Outcome of MSI Mystic Light detection, when the log mentions it.</summary>
    public BoardControllerIssue MsiMysticLight { get; init; }

    public IReadOnlyList<string> RegisteredControllers { get; init; } = [];

    public static BackendFindings Empty { get; } = new();
}
