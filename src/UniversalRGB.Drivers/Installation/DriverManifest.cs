using System.Text.Json;

namespace UniversalRGB.Drivers.Installation;

/// <summary>A curated, official package that Universal RGB knows how to verify and install with consent.</summary>
public sealed record DriverPackage
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Publisher { get; init; }
    public required string Purpose { get; init; }

    /// <summary>The only place users are sent to download the package.</summary>
    public required string OfficialPage { get; init; }

    /// <summary>application | kernel-driver | service</summary>
    public string Kind { get; init; } = "application";
    public bool RequiresAdministrator { get; init; }

    /// <summary>"no", "yes" or "maybe" — shown to the user before installing.</summary>
    public string Restart { get; init; } = "maybe";

    /// <summary>Requirement this package satisfies (matches DependencyChecker ids).</summary>
    public string? RequirementId { get; init; }

    /// <summary>
    /// Allowed Authenticode signer subject fragments. Empty = any signature that Windows trusts, and the
    /// signer is shown to the user for confirmation.
    /// </summary>
    public IReadOnlyList<string> ExpectedSigners { get; init; } = [];

    /// <summary>Pinned SHA-256 hashes of known-good releases. Empty = hash shown to the user, not enforced.</summary>
    public IReadOnlyList<string> Sha256 { get; init; } = [];

    /// <summary>False for packages that are distributed as archives (e.g. portable OpenRGB): open the page only.</summary>
    public bool Installable { get; init; } = true;

    /// <summary>File name fragments used to recognize a downloaded installer for this package.</summary>
    public IReadOnlyList<string> FileNameHints { get; init; } = [];

    public string Notes { get; init; } = "";

    /// <summary>Release this entry pins (shown to the user).</summary>
    public string? Version { get; init; }

    /// <summary>Official https download. Only used together with a pinned <see cref="Sha256"/>.</summary>
    public string? DownloadUrl { get; init; }

    public long? SizeBytes { get; init; }

    /// <summary>zip (portable, unpacked per user) | exe | msi</summary>
    public string Format { get; init; } = "exe";

    /// <summary>For zip packages: path of the main executable inside the archive.</summary>
    public string? ZipExecutable { get; init; }

    /// <summary>For zip packages: where to unpack (environment variables allowed).</summary>
    public string? InstallDirectory { get; init; }

    /// <summary>
    /// May be installed without a prompt when the user enabled automatic setup. Only portable, per-user
    /// packages qualify; drivers and anything needing administrator rights always ask first.
    /// </summary>
    public bool AutoInstall { get; init; }

    public string DisplayName => Version is null ? Name : $"{Name} {Version}";

    public bool CanDownload => Installable && DownloadUrl is not null && Sha256.Count > 0;
}

public sealed class DriverManifest
{
    public string Format { get; set; } = "UniversalRGB.Drivers";
    public int Version { get; set; } = 1;
    public List<DriverPackage> Packages { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static DriverManifest Load(string path) => Parse(File.ReadAllText(path));

    public static DriverManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<DriverManifest>(json, Options)
            ?? throw new InvalidDataException("Driver manifest is empty.");
        if (manifest.Format != "UniversalRGB.Drivers")
        {
            throw new InvalidDataException("Not a Universal RGB driver manifest.");
        }

        foreach (var package in manifest.Packages)
        {
            if (!Uri.TryCreate(package.OfficialPage, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException($"Package '{package.Id}' must have an https official page.");
            }

            if (package.DownloadUrl is not null)
            {
                if (!Uri.TryCreate(package.DownloadUrl, UriKind.Absolute, out var download) || download.Scheme != Uri.UriSchemeHttps)
                {
                    throw new InvalidDataException($"Package '{package.Id}' download must use https.");
                }

                if (package.Sha256.Count == 0)
                {
                    throw new InvalidDataException($"Package '{package.Id}' has a download but no pinned SHA-256.");
                }
            }

            if (package.Format == "zip" && (package.ZipExecutable is null || package.InstallDirectory is null))
            {
                throw new InvalidDataException($"Zip package '{package.Id}' needs zipExecutable and installDirectory.");
            }

            if (package.AutoInstall && (package.RequiresAdministrator || package.Kind == "kernel-driver" || package.Format != "zip"))
            {
                throw new InvalidDataException($"Package '{package.Id}' cannot be auto-installed: only portable per-user packages may install without a prompt.");
            }
        }

        return manifest;
    }

    public DriverPackage? Find(string id) => Packages.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Guesses which package a downloaded file belongs to from its name.</summary>
    public DriverPackage? GuessFromFileName(string path)
    {
        var name = Path.GetFileName(path);
        return Packages.FirstOrDefault(p => p.FileNameHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)));
    }
}
