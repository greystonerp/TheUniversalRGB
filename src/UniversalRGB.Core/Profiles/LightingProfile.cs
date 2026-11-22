using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Lighting;

namespace UniversalRGB.Core.Profiles;

/// <summary>Per-device lighting inside a profile.</summary>
public sealed class DeviceLightingSetting
{
    /// <summary>When false the device is left alone by this profile.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Device-wide effect. Null means "use the profile's global effect".</summary>
    public LightingEffect? Effect { get; set; }

    /// <summary>Optional per-zone overrides keyed by zone name.</summary>
    public Dictionary<string, LightingEffect> Zones { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Daily time window in which a profile is automatically activated.</summary>
public sealed class ProfileSchedule
{
    public bool Enabled { get; set; }
    public TimeOnly Start { get; set; } = new(19, 0);
    public TimeOnly End { get; set; } = new(23, 0);

    /// <summary>True when <paramref name="now"/> is inside the window (handles windows that cross midnight).</summary>
    public bool IsActiveAt(TimeOnly now) =>
        Enabled && (Start <= End ? now >= Start && now < End : now >= Start || now < End);
}

public sealed class LightingProfile
{
    public const int SchemaVersion = 1;

    public int Version { get; set; } = SchemaVersion;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New profile";
    public string Description { get; set; } = "";

    /// <summary>When true every enabled device runs <see cref="GlobalEffect"/> on a shared clock.</summary>
    public bool SyncAll { get; set; } = true;
    public LightingEffect GlobalEffect { get; set; } = new();

    /// <summary>0..100 multiplier applied on top of every effect's own brightness.</summary>
    public double MasterBrightness { get; set; } = 100;

    /// <summary>Keyed by <see cref="RgbDevice.Id"/>.</summary>
    public Dictionary<string, DeviceLightingSetting> Devices { get; set; } = new(StringComparer.Ordinal);

    public ProfileSchedule Schedule { get; set; } = new();
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;

    public bool IsDeviceEnabled(string deviceId) =>
        !Devices.TryGetValue(deviceId, out var setting) || setting.Enabled;

    /// <summary>Resolves the effect that applies to a device zone.</summary>
    public LightingEffect ResolveEffect(string deviceId, string? zoneName)
    {
        if (SyncAll || !Devices.TryGetValue(deviceId, out var setting))
        {
            return GlobalEffect;
        }

        if (zoneName is not null && setting.Zones.TryGetValue(zoneName, out var zoneEffect))
        {
            return zoneEffect;
        }

        return setting.Effect ?? GlobalEffect;
    }

    public LightingProfile Clone() =>
        JsonSerializer.Deserialize<LightingProfile>(JsonSerializer.Serialize(this, ProfileJson.Options), ProfileJson.Options)!;
}

public static class ProfileJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

/// <summary>Portable export format.</summary>
public sealed class ProfileExport
{
    public string Format { get; set; } = "UniversalRGB.Profiles";
    public int Version { get; set; } = 1;
    public DateTimeOffset ExportedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<LightingProfile> Profiles { get; set; } = [];
}
