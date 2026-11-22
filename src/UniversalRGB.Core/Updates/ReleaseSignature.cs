using System.Security.Cryptography;

namespace UniversalRGB.Core.Updates;

/// <summary>
/// ECDSA P-256 signatures for release files. Releases are signed with a private key kept offline by the
/// maintainer; installed copies only accept updates whose signature matches the public key built into the app,
/// so a compromised download location cannot push a malicious update.
/// </summary>
public static class ReleaseSignature
{
    public static string Sign(string filePath, string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        using var stream = File.OpenRead(filePath);
        return Convert.ToBase64String(key.SignData(stream, HashAlgorithmName.SHA256));
    }

    public static bool Verify(string filePath, string signatureBase64, string publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64) || string.IsNullOrWhiteSpace(signatureBase64))
        {
            return false;
        }

        byte[] signature;
        byte[] publicKey;
        try
        {
            signature = Convert.FromBase64String(signatureBase64.Trim());
            publicKey = Convert.FromBase64String(publicKeyBase64.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            using var stream = File.OpenRead(filePath);
            return key.VerifyData(stream, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Creates a new key pair: (PKCS#8 private key PEM, base64 SubjectPublicKeyInfo).</summary>
    public static (string PrivateKeyPem, string PublicKeyBase64) CreateKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    public static string PublicKeyFromPrivate(string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }
}
