using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using UniversalRGB.Core.Diagnostics;
using UniversalRGB.Core.Updates;
using UniversalRGB.Drivers.Installation;

namespace UniversalRGB.Drivers.Updates;

public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string Name,
    string Notes,
    string PageUrl,
    string SetupUrl,
    string SignatureUrl,
    long SetupSize,
    string? SetupSha256);

/// <summary>
/// Checks the project's GitHub Releases for a newer version and downloads it. A download is accepted only when
/// its SHA-256 matches GitHub's digest (when published) AND its ECDSA signature matches the release key built
/// into this app.
/// </summary>
public sealed partial class UpdateClient : IDisposable
{
    public const string SetupAssetName = "UniversalRGB-Setup.exe";
    public const string SignatureAssetName = SetupAssetName + ".sig";
    private const long MaxSetupBytes = 400L * 1024 * 1024;

    private readonly string _repository;
    private readonly string _publicKey;
    private readonly string _downloadDirectory;
    private readonly IAppLog _log;
    private readonly HttpClient _http;

    public UpdateClient(string? repository, string? publicKey, string downloadDirectory, IAppLog? log = null, HttpMessageHandler? handler = null)
    {
        _repository = repository?.Trim() ?? "";
        _publicKey = publicKey?.Trim() ?? "";
        _downloadDirectory = downloadDirectory;
        _log = log ?? NullLog.Instance;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("UniversalRGB-Updater/" + (typeof(UpdateClient).Assembly.GetName().Version?.ToString(3) ?? "1"));
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryPattern();

    public bool IsConfigured => RepositoryPattern().IsMatch(_repository) && _publicKey.Length > 0;

    public string Repository => _repository;

    public string ReleasesPage => $"https://github.com/{_repository}/releases";

    /// <summary>Returns the latest published release, or null when the repository has none yet.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("No update source is configured for this build.");
        }

        using var response = await _http.GetAsync($"https://api.github.com/repos/{_repository}/releases/latest", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Parse(json.RootElement);
    }

    internal static ReleaseInfo? Parse(JsonElement root)
    {
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!TryParseVersion(tag, out var version))
        {
            return null;
        }

        string? setupUrl = null, signatureUrl = null, digest = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name == SetupAssetName)
                {
                    setupUrl = url;
                    size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                    digest = asset.TryGetProperty("digest", out var d) && d.GetString() is { } dv && dv.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? dv["sha256:".Length..].ToUpperInvariant()
                        : null;
                }
                else if (name == SignatureAssetName)
                {
                    signatureUrl = url;
                }
            }
        }

        if (setupUrl is null || signatureUrl is null)
        {
            return null;
        }

        return new ReleaseInfo(
            version,
            tag,
            root.TryGetProperty("name", out var nm) ? nm.GetString() ?? tag : tag,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
            setupUrl,
            signatureUrl,
            size,
            digest);
    }

    public static bool TryParseVersion(string tag, out Version version)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            text = text[..dash];
        }

        if (Version.TryParse(text, out var parsed))
        {
            version = Normalize(parsed);
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    public static bool IsNewer(ReleaseInfo release, Version current) => release.Version > Normalize(current);

    /// <summary>Downloads and fully verifies the release setup. Returns the path of the verified file.</summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        RequireHttps(release.SetupUrl);
        RequireHttps(release.SignatureUrl);
        Directory.CreateDirectory(_downloadDirectory);
        var target = Path.Combine(_downloadDirectory, $"UniversalRGB-Setup-{release.Version.ToString(3)}.exe");
        var partial = target + ".partial";
        try
        {
            using (var response = await _http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? release.SetupSize;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                long received = 0;
                var lastReport = DateTime.MinValue;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > MaxSetupBytes)
                    {
                        throw new InvalidDataException("The update is larger than expected. It was discarded.");
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(150))
                    {
                        lastReport = DateTime.UtcNow;
                        progress?.Report(new DownloadProgress(received, total));
                    }
                }
            }

            if (release.SetupSha256 is { } expected)
            {
                await using var check = File.OpenRead(partial);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellationToken).ConfigureAwait(false));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The update does not match the checksum GitHub published for it. It was discarded.");
                }
            }

            var signature = await _http.GetStringAsync(release.SignatureUrl, cancellationToken).ConfigureAwait(false);
            if (signature.Length > 1024 || !ReleaseSignature.Verify(partial, signature, _publicKey))
            {
                throw new InvalidDataException("The update is not signed with the Universal RGB release key. It was discarded and nothing was installed.");
            }

            File.Move(partial, target, overwrite: true);
            await File.WriteAllTextAsync(target + ".sig", signature.Trim(), cancellationToken).ConfigureAwait(false);
            _log.Info($"Update {release.Tag} downloaded and verified (signature OK).");
            return target;
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    /// <summary>Re-checks a downloaded update's signature right before it is started.</summary>
    public bool VerifyDownloaded(string path) =>
        File.Exists(path) && File.Exists(path + ".sig") && ReleaseSignature.Verify(path, File.ReadAllText(path + ".sig"), _publicKey);

    public void Dispose() => _http.Dispose();

    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    private static void RequireHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Update downloads must use https.");
        }
    }
}
