using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcMieru.Core.Collection;

/// <summary>
/// Is this file signed, and by whom? WinVerifyTrust with no UI, no network (cache-only URL retrieval) and no revocation
/// check, so the answer is "signed and the chain was valid offline", never "safe". An embedded signature is looked for
/// first, then the Windows catalogs (most system files are catalog-signed). Results are cached by path, size and write
/// time, so the same svchost.exe is verified once however many processes run it (SPEC 6).
/// </summary>
internal sealed unsafe class SignatureVerifier
{
    public const string Source = "WinVerifyTrust (offline, revocation not checked)";

    private const int MaxCacheEntries = 4096;
    private const int WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdChoiceCatalog = 2;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;
    private const uint WtdRevocationCheckNone = 0x10;
    private const uint CertNameSimpleDisplayType = 4;

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int CryptEFileError = unchecked((int)0x80092003);

    private static Guid _genericVerifyV2 = new(0x00AAC56B, 0xCD44, 0x11d0, 0x8C, 0xC2, 0x00, 0xC0, 0x4F, 0xC2, 0x95, 0xEE);

    private readonly ConcurrentDictionary<FileStamp, Value<SignatureInfo>> _cache = new();

    public int CacheCount => _cache.Count;

    /// <summary>How many files were actually verified (not served from the cache); for tests and the cost report.</summary>
    public int Verifications => Volatile.Read(ref _verifications);

    private int _verifications;

    public Value<SignatureInfo> Verify(string path)
    {
        var read = FileStamp.TryRead(path, out var problem);
        if (read is not { } stamp)
        {
            return Value.Failed<SignatureInfo>(Source, problem ?? "the image file could not be examined");
        }

        if (_cache.TryGetValue(stamp, out var cached))
        {
            return cached;
        }

        Interlocked.Increment(ref _verifications);
        var result = VerifyUncached(stamp.Path);

        // A failure to read the file is not a fact about the file: do not remember it.
        if (result.Status is ValueStatus.Ok)
        {
            if (_cache.Count >= MaxCacheEntries)
            {
                _cache.Clear();
            }

            _cache[stamp] = result;
        }

        return result;
    }

    private static Value<SignatureInfo> VerifyUncached(string path)
    {
        try
        {
            var embedded = CheckFile(path, out var signer, out var hr);
            if (embedded)
            {
                return Value.Ok(new SignatureInfo(SignatureState.SignedEmbedded, signer), Source);
            }

            if (hr == CryptEFileError)
            {
                return Value.Failed<SignatureInfo>(Source, "the file could not be read for verification");
            }

            if (!IsNoSignature(hr, Marshal.GetLastPInvokeError()))
            {
                return new Value<SignatureInfo>(new SignatureInfo(SignatureState.Invalid, signer), ValueStatus.Ok, Source, Describe(hr));
            }

            if (CheckCatalog(path, out var catalogSigner, out var catalogHr, out var catalogFound))
            {
                return Value.Ok(new SignatureInfo(SignatureState.SignedCatalog, catalogSigner), Source);
            }

            if (catalogFound)
            {
                // The file is listed in a catalog, but the catalog itself did not verify.
                return new Value<SignatureInfo>(new SignatureInfo(SignatureState.Invalid, catalogSigner), ValueStatus.Ok, Source, "listed in a catalog that does not verify: " + Describe(catalogHr));
            }

            return Value.Ok(new SignatureInfo(SignatureState.Unsigned, null), Source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Value.Denied<SignatureInfo>(Source, "the file could not be opened for verification");
        }
    }

    private static bool IsNoSignature(int hr, int lastError) =>
        hr is TrustENoSignature or TrustEProviderUnknown or TrustESubjectFormUnknown
        && (hr != TrustENoSignature || lastError is TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown or 0);

    private static string Describe(int hr) => hr switch
    {
        unchecked((int)0x80096010) => "0x80096010: the file changed after it was signed",
        unchecked((int)0x800B0109) => "0x800B0109: the certificate chain ends at a root that is not trusted",
        unchecked((int)0x800B0101) => "0x800B0101: a certificate has expired",
        unchecked((int)0x800B010A) => "0x800B010A: the certificate chain could not be built",
        unchecked((int)0x800B0111) => "0x800B0111: the publisher is explicitly distrusted",
        unchecked((int)0x80092026) => "0x80092026: the signature is not allowed by the security settings",
        unchecked((int)0x800B0100) => "0x800B0100: the signature could not be read",
        _ => $"0x{hr:X8}",
    };

    /// <summary>WinVerifyTrust on the file's embedded signature. Always closes the state it opened.</summary>
    private static bool CheckFile(string path, out string? signer, out int hr)
    {
        signer = null;
        fixed (char* p = path)
        {
            var file = new Win32.WintrustFileInfo { Size = (uint)sizeof(Win32.WintrustFileInfo), FilePath = (nint)p };
            return Run(WtdChoiceFile, &file, out signer, out hr);
        }
    }

    private static bool Run(uint choice, void* payload, out string? signer, out int hr)
    {
        var data = new Win32.WintrustData
        {
            Size = (uint)sizeof(Win32.WintrustData),
            UiChoice = WtdUiNone,
            RevocationChecks = WtdRevokeNone,
            UnionChoice = choice,
            Union = (nint)payload,
            StateAction = WtdStateActionVerify,
            ProvFlags = WtdCacheOnlyUrlRetrieval | WtdRevocationCheckNone,
        };

        try
        {
            hr = Win32.WinVerifyTrust(-1, ref _genericVerifyV2, &data);
            signer = data.StateData != 0 ? ReadSigner(data.StateData) : null;
            return hr == 0;
        }
        finally
        {
            // The state must be released even when the verify failed.
            data.StateAction = WtdStateActionClose;
            _ = Win32.WinVerifyTrust(-1, ref _genericVerifyV2, &data);
        }
    }

    /// <summary>The simple display name of the first signer's certificate (for example "Microsoft Windows").</summary>
    private static string? ReadSigner(nint state)
    {
        var provider = Win32.WTHelperProvDataFromStateData(state);
        if (provider == 0)
        {
            return null;
        }

        var signer = Win32.WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signer == 0)
        {
            return null;
        }

        var chainCert = Win32.WTHelperGetProvCertFromChain(signer, 0);
        if (chainCert == 0)
        {
            return null;
        }

        // CRYPT_PROVIDER_CERT { DWORD cbStruct; PCCERT_CONTEXT pCert; ... }
        var certificate = *(nint*)(chainCert + 8);
        if (certificate == 0)
        {
            return null;
        }

        var name = stackalloc char[256];
        var length = Win32.CertGetNameString(certificate, CertNameSimpleDisplayType, 0, null, name, 256);
        return length > 1 ? new string(name, 0, (int)length - 1) : null;
    }

    private static bool CheckCatalog(string path, out string? signer, out int hr, out bool catalogFound)
    {
        signer = null;
        hr = 0;
        catalogFound = false;
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        foreach (var algorithm in new string?[] { "SHA256", null })
        {
            if (!Win32.CryptCATAdminAcquireContext2(out var admin, 0, algorithm, 0, 0))
            {
                continue;
            }

            try
            {
                if (TryCatalog(admin, file, path, out signer, out hr, out var found))
                {
                    catalogFound = true;
                    return true;
                }

                catalogFound |= found;
                if (found)
                {
                    return false;
                }
            }
            finally
            {
                _ = Win32.CryptCATAdminReleaseContext(admin, 0);
            }
        }

        return false;
    }

    private static bool TryCatalog(nint admin, SafeFileHandle file, string path, out string? signer, out int hr, out bool found)
    {
        signer = null;
        hr = 0;
        found = false;
        var handle = file.DangerousGetHandle();

        uint hashLength = 0;
        _ = Win32.CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref hashLength, null, 0);
        if (hashLength == 0 || hashLength > 64)
        {
            return false;
        }

        var hash = stackalloc byte[(int)hashLength];
        if (!Win32.CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref hashLength, hash, 0))
        {
            return false;
        }

        nint previous = 0;
        var catalog = Win32.CryptCATAdminEnumCatalogFromHash(admin, hash, hashLength, 0, ref previous);
        if (catalog == 0)
        {
            return false;
        }

        try
        {
            var info = new Win32.CatalogInfo { Size = (uint)sizeof(Win32.CatalogInfo) };
            if (!Win32.CryptCATCatalogInfoFromContext(catalog, ref info, 0))
            {
                return false;
            }

            found = true;
            var tag = Convert.ToHexString(new ReadOnlySpan<byte>(hash, (int)hashLength));
            fixed (char* tagPointer = tag)
            fixed (char* pathPointer = path)
            {
                var member = new Win32.WintrustCatalogInfo
                {
                    Size = (uint)sizeof(Win32.WintrustCatalogInfo),
                    CatalogFilePath = (nint)info.CatalogFile,
                    MemberTag = (nint)tagPointer,
                    MemberFilePath = (nint)pathPointer,
                    MemberFile = handle,
                    CalculatedFileHash = (nint)hash,
                    CalculatedFileHashLength = hashLength,
                    CatAdmin = admin,
                };
                return Run(WtdChoiceCatalog, &member, out signer, out hr);
            }
        }
        finally
        {
            _ = Win32.CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
        }
    }
}
