using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniversalRGB.Core.Compatibility;

/// <summary>
/// A compatibility database entry. Entries never claim support on their own: a device is only
/// "Ready" once a backend actually exposes a controllable controller for it.
/// </summary>
public sealed record CompatibilityRecord
{
    public required string Id { get; init; }
    public required string Manufacturer { get; init; }

    /// <summary>Model, or a family description for vendor-wide entries.</summary>
    public string Model { get; init; } = "";
    public string Category { get; init; } = "Unknown";

    /// <summary>USB, HID or PCI.</summary>
    public string Bus { get; init; } = "USB";

    /// <summary>4-digit upper-case hex vendor ID.</summary>
    public required string VendorId { get; init; }

    /// <summary>Optional 4-digit product ID; null matches the whole vendor family.</summary>
    public string? ProductId { get; init; }
    public string? Revision { get; init; }
    public string Backend { get; init; } = "OpenRGB";

    /// <summary>Requirement IDs, e.g. "openrgb-sdk-server", "pawnio", "administrator".</summary>
    public IReadOnlyList<string> Requirements { get; init; } = [];

    /// <summary>tested | experimental | vendor-family | unsupported | example-only</summary>
    public string Status { get; init; } = "vendor-family";
    public bool Verified { get; init; }
    public DateOnly? VerifiedOn { get; init; }
    public string Notes { get; init; } = "";

    [JsonIgnore]
    public bool IsVendorFamily => ProductId is null;
}

public sealed class CompatibilityManifest
{
    public string Format { get; set; } = "UniversalRGB.Compatibility";
    public int Version { get; set; } = 1;
    public string? Updated { get; set; }
    public List<CompatibilityRecord> Records { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static CompatibilityManifest Load(string path) => Parse(File.ReadAllText(path));

    public static CompatibilityManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<CompatibilityManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("Compatibility manifest is empty.");
        if (manifest.Format != "UniversalRGB.Compatibility")
        {
            throw new InvalidDataException("Not a Universal RGB compatibility manifest.");
        }

        return manifest;
    }
}
