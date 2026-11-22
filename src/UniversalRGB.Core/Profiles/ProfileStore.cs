using System.Text.Json;
using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Lighting;

namespace UniversalRGB.Core.Profiles;

/// <summary>Stores lighting profiles as one JSON file per profile.</summary>
public sealed class ProfileStore
{
    private readonly string _directory;
    private readonly Lock _gate = new();

    public ProfileStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public string StorageDirectory => _directory;

    public IReadOnlyList<LightingProfile> LoadAll()
    {
        lock (_gate)
        {
            var list = new List<LightingProfile>();
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                try
                {
                    var profile = JsonSerializer.Deserialize<LightingProfile>(File.ReadAllText(file), ProfileJson.Options);
                    if (profile is not null && !string.IsNullOrWhiteSpace(profile.Id))
                    {
                        list.Add(Normalize(profile));
                    }
                }
                catch (JsonException)
                {
                    // A damaged file must not prevent the others from loading; it is left on disk untouched.
                }
            }

            return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    public void Save(LightingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            profile.ModifiedUtc = DateTimeOffset.UtcNow;
            var path = PathFor(profile.Id);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(profile, ProfileJson.Options));
            File.Move(temp, path, overwrite: true);
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var path = PathFor(id);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
    }

    public LightingProfile Duplicate(LightingProfile source, string? newName = null)
    {
        var copy = source.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = newName ?? $"{source.Name} (copy)";
        copy.Schedule.Enabled = false;
        Save(copy);
        return copy;
    }

    public void Export(IEnumerable<LightingProfile> profiles, string path)
    {
        var export = new ProfileExport { Profiles = profiles.ToList() };
        File.WriteAllText(path, JsonSerializer.Serialize(export, ProfileJson.Options));
    }

    /// <summary>
    /// Imports profiles from an export file or a single profile file. Imported profiles always get new IDs
    /// so they never overwrite existing ones.
    /// </summary>
    public IReadOnlyList<LightingProfile> Import(string path)
    {
        var json = File.ReadAllText(path);
        List<LightingProfile> incoming;

        using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("profiles", out _))
            {
                var export = JsonSerializer.Deserialize<ProfileExport>(json, ProfileJson.Options)
                    ?? throw new InvalidDataException("The file is empty.");
                if (export.Format != "UniversalRGB.Profiles")
                {
                    throw new InvalidDataException("This is not a Universal RGB profile export.");
                }

                incoming = export.Profiles;
            }
            else
            {
                var single = JsonSerializer.Deserialize<LightingProfile>(json, ProfileJson.Options)
                    ?? throw new InvalidDataException("The file is empty.");
                incoming = [single];
            }
        }

        var imported = new List<LightingProfile>();
        foreach (var profile in incoming)
        {
            var p = Normalize(profile);
            p.Id = Guid.NewGuid().ToString("N");
            p.Schedule.Enabled = false;
            Save(p);
            imported.Add(p);
        }

        return imported;
    }

    /// <summary>Creates the Gaming / Desktop / Nighttime profiles when no profiles exist.</summary>
    public IReadOnlyList<LightingProfile> EnsureDefaults()
    {
        var existing = LoadAll();
        if (existing.Count > 0)
        {
            return existing;
        }

        Save(new LightingProfile
        {
            Name = "Gaming",
            Description = "Animated rainbow wave across every device.",
            GlobalEffect = new LightingEffect { Kind = EffectKind.RainbowWave, Speed = 45, Colors = LightingEffect.DefaultColorsFor(EffectKind.RainbowWave) },
        });
        Save(new LightingProfile
        {
            Name = "Desktop",
            Description = "Calm static cyan.",
            GlobalEffect = LightingEffect.StaticColor(LightingEffect.DefaultAccent, 80),
        });
        Save(new LightingProfile
        {
            Name = "Nighttime",
            Description = "Slow, dim amber breathing for late hours.",
            MasterBrightness = 35,
            GlobalEffect = new LightingEffect { Kind = EffectKind.Breathing, Speed = 15, Colors = [new RgbColor(251, 146, 60)] },
            Schedule = new ProfileSchedule { Enabled = false, Start = new TimeOnly(23, 0), End = new TimeOnly(7, 0) },
        });
        return LoadAll();
    }

    private string PathFor(string id)
    {
        var safe = string.Concat(id.Where(char.IsLetterOrDigit));
        if (safe.Length == 0)
        {
            throw new ArgumentException("Profile id is empty.", nameof(id));
        }

        return Path.Combine(_directory, safe + ".json");
    }

    private static LightingProfile Normalize(LightingProfile p)
    {
        p.Name = string.IsNullOrWhiteSpace(p.Name) ? "Unnamed profile" : p.Name.Trim();
        p.Devices ??= new Dictionary<string, DeviceLightingSetting>(StringComparer.Ordinal);
        p.Schedule ??= new ProfileSchedule();
        p.GlobalEffect ??= new LightingEffect();
        p.MasterBrightness = Math.Clamp(p.MasterBrightness, 0, 100);
        if (p.GlobalEffect.Colors is null || p.GlobalEffect.Colors.Count == 0)
        {
            p.GlobalEffect = p.GlobalEffect with { Colors = LightingEffect.DefaultColorsFor(p.GlobalEffect.Kind) };
        }

        return p;
    }
}
