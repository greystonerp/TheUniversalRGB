using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace UniversalRGB.Drivers.Verification;

public enum SignatureState
{
    Valid,
    NotSigned,
    Untrusted,
    Invalid,
    Error,
}

internal sealed record AuthenticodeResult(SignatureState State, string Detail, string? SignerSubject, string? SignerIssuer, DateTime? SignerValidUntil);

/// <summary>
/// Authenticode verification through WinVerifyTrust (the same check Windows uses before running a file).
/// The signer certificate is read from the verification state, so it is exactly the certificate Windows checked.
/// </summary>
internal static class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

    public static AuthenticodeResult Verify(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };

        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = filePtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
            };

            var result = unchecked((uint)WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data));
            var (state, detail) = Interpret(result);

            string? subject = null, issuer = null;
            DateTime? notAfter = null;
            if (state != SignatureState.NotSigned)
            {
                (subject, issuer, notAfter) = ReadSigner(data.hWVTStateData);
            }

            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
            return new AuthenticodeResult(state, detail, subject, issuer, notAfter);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new AuthenticodeResult(SignatureState.Error, ex.Message, null, null, null);
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }

    private static (string? Subject, string? Issuer, DateTime? NotAfter) ReadSigner(IntPtr stateData)
    {
        if (stateData == IntPtr.Zero)
        {
            return (null, null, null);
        }

        var provider = WTHelperProvDataFromStateData(stateData);
        if (provider == IntPtr.Zero)
        {
            return (null, null, null);
        }

        var signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signer == IntPtr.Zero)
        {
            return (null, null, null);
        }

        var providerCert = WTHelperGetProvCertFromChain(signer, 0);
        if (providerCert == IntPtr.Zero)
        {
            return (null, null, null);
        }

        // CRYPT_PROVIDER_CERT { DWORD cbStruct; PCCERT_CONTEXT pCert; ... } — pCert follows the aligned DWORD.
        var certContext = Marshal.ReadIntPtr(providerCert, IntPtr.Size);
        if (certContext == IntPtr.Zero)
        {
            return (null, null, null);
        }

        using var cert = new X509Certificate2(certContext);
        return (cert.GetNameInfo(X509NameType.SimpleName, false), cert.GetNameInfo(X509NameType.SimpleName, true), cert.NotAfter);
    }

    internal static (SignatureState, string) Interpret(uint hresult) => hresult switch
    {
        0 => (SignatureState.Valid, "Valid Authenticode signature chaining to a trusted root."),
        0x800B0100 => (SignatureState.NotSigned, "The file is not digitally signed."),
        0x800B0109 => (SignatureState.Untrusted, "Signed, but the certificate chain ends in an untrusted root."),
        0x800B0004 => (SignatureState.Untrusted, "The publisher is not trusted on this PC."),
        0x800B0111 => (SignatureState.Untrusted, "The publisher was explicitly marked untrusted."),
        0x800B0101 => (SignatureState.Invalid, "The signing certificate has expired."),
        0x80096010 => (SignatureState.Invalid, "The file was modified after it was signed (digest mismatch)."),
        0x80092026 => (SignatureState.Untrusted, "Local security policy blocked the signature check."),
        _ => (SignatureState.Error, $"Signature check failed (0x{hresult:X8})."),
    };

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, ref WINTRUST_DATA data);

    [DllImport("wintrust.dll")]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll")]
    private static extern IntPtr WTHelperGetProvSignerFromChain(
        IntPtr providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);

    [DllImport("wintrust.dll")]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certIndex);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
