using System.ComponentModel;
using System.Diagnostics;
using UniversalRGB.Core.Diagnostics;
using UniversalRGB.Core.Storage;
using UniversalRGB.Drivers.Verification;

namespace UniversalRGB.Drivers.Installation;

public enum InstallResult
{
    Succeeded,
    RestartRequired,
    Failed,
    Cancelled,
    Blocked,
}

public sealed record InstallOutcome(InstallResult Result, int? ExitCode, string Message);

/// <summary>
/// Runs a verified installer after the user has approved it. The file is copied into a private cache,
/// re-hashed (so it cannot be swapped between verification and launch), started with the minimum
/// required elevation, and the result is written to the install history.
/// </summary>
public sealed class PackageInstaller
{
    private readonly AppDatabase _database;
    private readonly string _cacheDirectory;
    private readonly IAppLog _log;

    public PackageInstaller(AppDatabase database, string cacheDirectory, IAppLog? log = null)
    {
        _database = database;
        _cacheDirectory = cacheDirectory;
        _log = log ?? NullLog.Instance;
    }

    /// <param name="userApproved">Must be true: callers show the consent dialog first.</param>
    public async Task<InstallOutcome> InstallAsync(PackageVerification verification, bool userApproved, CancellationToken cancellationToken)
    {
        var name = verification.Package?.Name ?? Path.GetFileName(verification.FilePath);
        if (!userApproved)
        {
            return Record(verification, new InstallOutcome(InstallResult.Cancelled, null, "Installation was not approved."));
        }

        if (!verification.CanInstall)
        {
            return Record(verification, new InstallOutcome(InstallResult.Blocked, null, string.Join(" ", verification.Blockers)));
        }

        Directory.CreateDirectory(_cacheDirectory);
        var cached = Path.Combine(_cacheDirectory, $"{verification.Sha256[..12]}_{Path.GetFileName(verification.FilePath)}");
        File.Copy(verification.FilePath, cached, overwrite: true);

        var recheck = await PackageVerifier.VerifyAsync(cached, verification.Package, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(recheck.Sha256, verification.Sha256, StringComparison.OrdinalIgnoreCase) || !recheck.CanInstall)
        {
            TryDelete(cached);
            return Record(verification, new InstallOutcome(InstallResult.Blocked, null, "The file changed after it was verified. Nothing was run."));
        }

        var isMsi = cached.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(isMsi ? "msiexec.exe" : cached)
        {
            Arguments = isMsi ? $"/i \"{cached}\"" : "",
            UseShellExecute = true,
            WorkingDirectory = _cacheDirectory,
        };
        if (verification.Package?.RequiresAdministrator == true)
        {
            start.Verb = "runas";
        }

        _log.Info($"Starting approved installer for {name} ({verification.Sha256}).");
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The installer did not start.");
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var code = process.ExitCode;
            var outcome = code switch
            {
                0 => new InstallOutcome(InstallResult.Succeeded, code, $"{name} installed successfully."),
                3010 or 1641 => new InstallOutcome(InstallResult.RestartRequired, code, $"{name} installed. Restart Windows to finish."),
                1602 or 1223 => new InstallOutcome(InstallResult.Cancelled, code, "The installer was cancelled."),
                _ => new InstallOutcome(InstallResult.Failed, code, $"The installer exited with code {code}."),
            };
            return Record(verification, outcome);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return Record(verification, new InstallOutcome(InstallResult.Cancelled, null, "Administrator permission was declined."));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log.Error($"Installer for {name} failed to start.", ex);
            return Record(verification, new InstallOutcome(InstallResult.Failed, null, ex.Message));
        }
    }

    private InstallOutcome Record(PackageVerification v, InstallOutcome outcome)
    {
        _database.AddInstallRecord(new InstallRecord(
            0,
            DateTimeOffset.Now,
            v.Package?.Name ?? Path.GetFileName(v.FilePath),
            v.FilePath,
            v.Sha256,
            v.SignerSubject,
            v.Signature == SignatureState.Valid,
            outcome.ExitCode,
            outcome.Result.ToString(),
            outcome.Message));
        _log.Info($"Install result for {Path.GetFileName(v.FilePath)}: {outcome.Result} ({outcome.Message})");
        return outcome;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
