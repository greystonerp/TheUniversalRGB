using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UniversalRGB.Drivers.Installation;

namespace UniversalRGB.Drivers.Verification;

public sealed record PackageVerification
{
    public required string FilePath { get; init; }
    public long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public SignatureState Signature { get; init; }
    public string SignatureDetail { get; init; } = "";
    public string? SignerSubject { get; init; }
    public string? SignerIssuer { get; init; }
    public DateTime? SignerValidUntil { get; init; }
    public DriverPackage? Package { get; init; }

    /// <summary>True when the manifest pins hashes and this file matches one of them.</summary>
    public bool HashPinnedMatch { get; init; }

    /// <summary>Problems that block installation. Empty means the file may be installed after user consent.</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];

    /// <summary>Things the user should know but that do not block installation.</summary>
    public IReadOnlyList<string> Notices { get; init; } = [];

    public bool CanInstall => Blockers.Count == 0;
}

/// <summary>
/// Checks a downloaded installer before anything runs: SHA-256, Authenticode signature, signer identity and,
/// when the manifest pins them, known-good hashes. Unsigned or tampered files are never installable.
/// </summary>
public static class PackageVerifier
{
    private static readonly string[] AllowedExtensions = [".exe", ".msi"];

    public static async Task<PackageVerification> VerifyAsync(string path, DriverPackage? package, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Installer not found.", full);
        }

        string sha;
        await using (var stream = File.OpenRead(full))
        {
            sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        var authenticode = await Task.Run(() => AuthenticodeVerifier.Verify(full), cancellationToken).ConfigureAwait(false);
        var (state, detail) = (authenticode.State, authenticode.Detail);
        var (subject, issuer, notAfter) = (authenticode.SignerSubject, authenticode.SignerIssuer, authenticode.SignerValidUntil);

        var blockers = new List<string>();
        var notices = new List<string>();

        if (!AllowedExtensions.Contains(info.Extension, StringComparer.OrdinalIgnoreCase))
        {
            blockers.Add("Only .exe and .msi installers can be run. Archives must be extracted and checked manually.");
        }

        if (state != SignatureState.Valid)
        {
            blockers.Add($"Signature check failed: {detail}");
        }

        if (package is not null)
        {
            if (!package.Installable)
            {
                blockers.Add($"{package.Name} is not installed through Universal RGB. Use its official page: {package.OfficialPage}");
            }

            if (package.ExpectedSigners.Count > 0 &&
                (subject is null || !package.ExpectedSigners.Any(s => subject.Contains(s, StringComparison.OrdinalIgnoreCase))))
            {
                blockers.Add($"Signer \"{subject ?? "none"}\" is not an expected publisher for {package.Name}.");
            }

            if (package.Sha256.Count > 0)
            {
                if (!package.Sha256.Contains(sha, StringComparer.OrdinalIgnoreCase))
                {
                    blockers.Add($"SHA-256 does not match any known-good {package.Name} release.");
                }
            }
            else
            {
                notices.Add($"No pinned hash for {package.Name}: confirm you downloaded it from {package.OfficialPage}.");
            }
        }
        else
        {
            notices.Add("This file does not match a curated package. Only continue if you trust its publisher.");
        }

        if (subject is not null && state == SignatureState.Valid)
        {
            notices.Add($"Signed by: {subject}");
        }

        return new PackageVerification
        {
            FilePath = full,
            SizeBytes = info.Length,
            Sha256 = sha,
            Signature = state,
            SignatureDetail = detail,
            SignerSubject = subject,
            SignerIssuer = issuer,
            SignerValidUntil = notAfter,
            Package = package,
            HashPinnedMatch = package?.Sha256.Contains(sha, StringComparer.OrdinalIgnoreCase) == true,
            Blockers = blockers,
            Notices = notices,
        };
    }
}
