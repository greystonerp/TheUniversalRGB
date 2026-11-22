using System.IO.Compression;
using System.Security.Cryptography;
using UniversalRGB.Core.Diagnostics;
using UniversalRGB.Core.Storage;

namespace UniversalRGB.Drivers.Installation;

public readonly record struct DownloadProgress(long Received, long? Total)
{
    public double? Fraction => Total is > 0 ? Math.Clamp((double)Received / Total.Value, 0, 1) : null;
}

/// <summary>
/// Downloads curated requirements from their official https source and only accepts files whose SHA-256 matches
/// the hash pinned in drivers.json. Portable zip packages are unpacked into a per-user folder (no admin rights);
/// installers are handed to <see cref="PackageInstaller"/> after signature checks and user consent.
/// </summary>
public sealed class RequirementInstaller : IDisposable
{
    private const long MaxDownloadBytes = 512L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly AppDatabase _database;
    private readonly string _cacheDirectory;
    private readonly IAppLog _log;

    public RequirementInstaller(AppDatabase database, string cacheDirectory, IAppLog? log = null, HttpMessageHandler? handler = null)
    {
        _database = database;
        _cacheDirectory = cacheDirectory;
        _log = log ?? NullLog.Instance;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("UniversalRGB/" + (typeof(RequirementInstaller).Assembly.GetName().Version?.ToString(3) ?? "1"));
    }

    /// <summary>Downloads the package (or reuses a cached copy) and returns the path of a hash-verified file.</summary>
    public async Task<string> DownloadVerifiedAsync(DriverPackage package, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (!package.CanDownload || !Uri.TryCreate(package.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"{package.Name} has no official https download with a pinned SHA-256.");
        }

        Directory.CreateDirectory(_cacheDirectory);
        var target = Path.Combine(_cacheDirectory, Path.GetFileName(uri.LocalPath));
        if (File.Exists(target) && Matches(await HashAsync(target, cancellationToken).ConfigureAwait(false), package))
        {
            _log.Info($"Using cached, verified {package.DisplayName} download.");
            return target;
        }

        _log.Info($"Downloading {package.DisplayName} from {uri}.");
        var partial = target + ".partial";
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme is { } finalScheme && finalScheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("The download was redirected away from https. Nothing was installed.");
            }

            var total = response.Content.Headers.ContentLength ?? package.SizeBytes;
            var limit = Math.Min(MaxDownloadBytes, Math.Max(package.SizeBytes ?? 0, total ?? 0) * 2 + (1024 * 1024));
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                var lastReport = DateTime.MinValue;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > limit)
                    {
                        throw new InvalidDataException("The download is larger than expected. Nothing was installed.");
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(120))
                    {
                        lastReport = DateTime.UtcNow;
                        progress?.Report(new DownloadProgress(received, total));
                    }
                }

                progress?.Report(new DownloadProgress(received, total));
            }

            var sha = await HashAsync(partial, cancellationToken).ConfigureAwait(false);
            if (!Matches(sha, package))
            {
                throw new InvalidDataException(
                    $"The downloaded {package.Name} does not match the pinned SHA-256 (got {sha[..16]}…). It was deleted and nothing was installed.");
            }

            File.Move(partial, target, overwrite: true);
            _log.Info($"{package.DisplayName} download verified (SHA-256 {sha}).");
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

    /// <summary>
    /// Unpacks a verified portable zip into the package's per-user install folder and returns the main executable.
    /// Any previous copy in that folder is replaced.
    /// </summary>
    public async Task<string> InstallPortableAsync(DriverPackage package, string verifiedZip, CancellationToken cancellationToken)
    {
        if (package.Format != "zip" || package.ZipExecutable is null || package.InstallDirectory is null)
        {
            throw new InvalidOperationException($"{package.Name} is not a portable zip package.");
        }

        // Re-check right before unpacking so a cached file cannot be swapped in between.
        var sha = await HashAsync(verifiedZip, cancellationToken).ConfigureAwait(false);
        if (!Matches(sha, package))
        {
            throw new InvalidDataException($"{Path.GetFileName(verifiedZip)} no longer matches the pinned SHA-256. Nothing was installed.");
        }

        var destination = Path.GetFullPath(Environment.ExpandEnvironmentVariables(package.InstallDirectory));
        if (Path.GetPathRoot(destination) == destination || destination.Contains('%'))
        {
            throw new InvalidOperationException($"Refusing to install {package.Name} into '{destination}'.");
        }

        var staging = destination + ".staging";
        await Task.Run(() =>
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            // ZipFile rejects entries that would escape the target folder.
            ZipFile.ExtractToDirectory(verifiedZip, staging, overwriteFiles: true);
            var exeInStaging = Path.GetFullPath(Path.Combine(staging, package.ZipExecutable.Replace('/', Path.DirectorySeparatorChar)));
            if (!exeInStaging.StartsWith(staging, StringComparison.OrdinalIgnoreCase) || !File.Exists(exeInStaging))
            {
                throw new InvalidDataException($"The {package.Name} archive does not contain {package.ZipExecutable}.");
            }

            var contentRoot = Path.GetDirectoryName(exeInStaging)!;
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(contentRoot, destination);
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }, cancellationToken).ConfigureAwait(false);

        var executable = Path.Combine(destination, Path.GetFileName(package.ZipExecutable));
        _database.AddInstallRecord(new InstallRecord(
            0,
            DateTimeOffset.Now,
            package.DisplayName,
            verifiedZip,
            sha,
            null,
            false,
            0,
            nameof(InstallResult.Succeeded),
            $"Verified against the pinned SHA-256 and unpacked to {destination}."));
        _log.Info($"{package.DisplayName} unpacked to {destination}.");
        return executable;
    }

    public void Dispose() => _http.Dispose();

    private static bool Matches(string sha, DriverPackage package) =>
        package.Sha256.Contains(sha, StringComparer.OrdinalIgnoreCase);

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
