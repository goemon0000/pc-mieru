// Provenance: ported from the owner's pl-cloud src/PartsLife.Update/Authenticode.cs at 25e4571 (Parts Life, MIT, same owner; see NOTICE.txt).
// What is new here: the namespace; the identity comparison of the old SignaturePolicy (Subject, product name, original file name) is NOT ported (DESIGN-B 12 decides what is pinned and
// the owner has no certificate yet), so the policy is "Valid or Unsigned passes, Invalid refuses" with a seam where the pinning goes; the inspector is a seam for tests; the DLL
// is resolved from System32 per method (the same rule as every P/Invoke of this assembly); the comments are in English.
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcMieru.Update.Staging;

/// <summary>How a file's embedded Authenticode signature came out.</summary>
public enum SignatureKind
{
    /// <summary>No signature at all (TRUST_E_NOSIGNATURE).</summary>
    Unsigned,

    /// <summary>A signature that WinVerifyTrust accepts with what is on this PC (no network).</summary>
    Valid,

    /// <summary>A signature that is there and is not accepted: damaged, a changed file, a certificate chain that cannot be built from what is on this PC.</summary>
    Invalid,
}

/// <summary>A file's signature: the kind, and the signer's subject (only for a Valid one).</summary>
public readonly record struct SignatureInfo(SignatureKind Kind, string? Subject)
{
    public static readonly SignatureInfo Unsigned = new(SignatureKind.Unsigned, null);
}

/// <summary>What looks at a file's signature. The real one is <see cref="Authenticode"/>; a test gives its own answers.</summary>
public interface ISignatureInspector
{
    SignatureInfo Inspect(string path);
}

/// <summary>The real inspector (<see cref="Authenticode.Inspect"/>).</summary>
public sealed class WindowsSignatureInspector : ISignatureInspector
{
    [SupportedOSPlatform("windows")]
    public SignatureInfo Inspect(string path) => Authenticode.Inspect(path);
}

/// <summary>
/// What a staged program file's signature must be for the update to go on (DESIGN-B 3.3 and 12). A program file that is NEW or CHANGED (its hash differs from the installed record) is looked
/// at: Valid and Unsigned pass (the owner has no certificate yet, so every file of this project is Unsigned, and the Microsoft runtime files carry Microsoft's signature); Invalid refuses the
/// whole candidate. A file that did not change is not looked at: it is the one that is installed.
///
/// The hash against SHA256.txt and the manifest is what binds a file to the release; this check is only the second, independent opinion (a signed file that was tampered with fails it).
/// </summary>
public static class SignaturePolicy
{
    /// <summary>The files of a stage that are looked at: a program file (.exe or .dll).</summary>
    public static bool IsProgramFile(string bundleRelativeName) =>
        bundleRelativeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || bundleRelativeName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    /// <returns>Null when the file may go on, otherwise why not (for a log; the person is told by <c>UpdateRefusal.SignatureInvalid</c>).</returns>
    public static string? Check(string name, SignatureInfo candidate)
    {
        if (candidate.Kind == SignatureKind.Invalid)
        {
            return $"the signature of '{name}' is not valid";
        }

        // TODO(DESIGN-B 12): when the owner has a certificate, pin the Subject and the Issuer here: a file of the installed set that was Valid must stay Valid with the same Subject,
        // and an Unsigned file of a set whose installed copy was Valid is refused. Until then Unsigned passes, as the first-run dialog says ("including versions without a Windows digital signature").
        return null;
    }
}

/// <summary>
/// Checks a signature with WinVerifyTrust (P/Invoke through source-generated imports, so it also runs in a trimmed or AOT program).
///
/// <b>It does not talk to the network while it checks.</b> The revocation check (CRL / OCSP) is off and a certificate that has to be fetched is looked for only in what is already on this PC
/// (WTD_CACHE_ONLY_URL_RETRIEVAL): the only host this updater talks to is GitHub. The price: a signature whose chain needs an intermediate certificate that is not on this PC reads as Invalid,
/// which stops the update (the safe side).
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class Authenticode
{
    public static SignatureInfo Inspect(string path)
    {
        var (hr, subject) = Verify(path);
        if (hr == TrustENoSignature)
        {
            return SignatureInfo.Unsigned;
        }

        if (hr != 0 || string.IsNullOrEmpty(subject))
        {
            return new SignatureInfo(SignatureKind.Invalid, null);
        }

        return new SignatureInfo(SignatureKind.Valid, subject);
    }

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x10;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
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

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WintrustData data);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("wintrust.dll")]
    private static partial IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("wintrust.dll")]
    private static partial IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData, uint idxSigner, [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint idxCounterSigner);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("wintrust.dll")]
    private static partial IntPtr WTHelperGetProvCertFromChain(IntPtr pSgnr, uint idxCert);

    /// <summary>
    /// The signer's subject, read from the certificate WinVerifyTrust itself checked (a second reading of the file by another tool could differ from what was checked).
    /// CRYPT_PROVIDER_CERT starts with a DWORD cbStruct, and the next field (aligned to the pointer width) is pCert.
    /// </summary>
    private static string? SignerSubject(IntPtr state)
    {
        var prov = WTHelperProvDataFromStateData(state);
        if (prov == IntPtr.Zero)
        {
            return null;
        }

        var signer = WTHelperGetProvSignerFromChain(prov, 0, false, 0);
        if (signer == IntPtr.Zero)
        {
            return null;
        }

        var provCert = WTHelperGetProvCertFromChain(signer, 0);
        if (provCert == IntPtr.Zero)
        {
            return null;
        }

        var context = Marshal.ReadIntPtr(provCert, IntPtr.Size);
        if (context == IntPtr.Zero)
        {
            return null;
        }

        using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(context); // a copy (the original goes away at CLOSE)
        return certificate.Subject;
    }

    private static unsafe (int Hr, string? Subject) Verify(string path)
    {
        fixed (char* p = path)
        {
            var file = new WintrustFileInfo
            {
                cbStruct = (uint)sizeof(WintrustFileInfo),
                pcwszFilePath = (IntPtr)p,
            };
            var data = new WintrustData
            {
                cbStruct = (uint)sizeof(WintrustData),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = (IntPtr)(&file),
                dwStateAction = WtdStateActionVerify,
                dwProvFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval,
            };
            var action = GenericVerifyV2;

            // INVALID_HANDLE_VALUE as the window: no dialog of any kind.
            var hr = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            try
            {
                return (hr, hr == 0 ? SignerSubject(data.hWVTStateData) : null);
            }
            finally
            {
                data.dwStateAction = WtdStateActionClose;
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
        }
    }
}
