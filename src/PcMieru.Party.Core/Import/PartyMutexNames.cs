using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PcMieru.Party.Import;

/// <summary>
/// The names of the per-user mutexes of Parts Life (DESIGN-C 3.2 and 4.1). One place for the SID hash, so that the resident mutex
/// (shared with the last release of the old Parts Life) and the import lock can never drift apart: the same user gets the same
/// hash in every session, another user a different one.
/// The hash is the first 16 hex digits (lower case) of the SHA-256 of the SID string of the user (UTF-8).
/// </summary>
public static class PartyMutexNames
{
    /// <summary>The single resident (shared with Parts Life v0.8.0). Do not change: the old program uses the same name.</summary>
    public const string ResidentPrefix = @"Global\PCMieru.PartsLife.Resident.v1.";

    /// <summary>The lock that serializes the first import (GUI hand-over, first start of Party, first creation of the destination).</summary>
    public const string ImportPrefix = @"Global\PCMieru.Party.Import.v1.";

    /// <summary>The first 16 hex digits (lower case) of the SHA-256 of the SID string.</summary>
    public static string SidHash(string sid) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..16].ToLowerInvariant();

    public static string ResidentNameFor(string sid) => ResidentPrefix + SidHash(sid);

    public static string ImportNameFor(string sid) => ImportPrefix + SidHash(sid);

    /// <summary>The SID string of the user this process runs as.</summary>
    public static string CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("the current user has no SID");
    }

    public static string CurrentResidentName() => ResidentNameFor(CurrentSid());

    public static string CurrentImportName() => ImportNameFor(CurrentSid());
}
