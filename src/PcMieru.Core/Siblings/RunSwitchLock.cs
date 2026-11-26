using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PcMieru.Core.Siblings;

/// <summary>
/// The per-user lock of every change to the sign-in entries (DESIGN-C 4.3): the switch from the old Parts Life value (<see cref="RunSwitch"/>, its recovery) and the ordinary setting
/// (<see cref="PartyLogon.Enable"/> and <see cref="PartyLogon.Disable"/>) all run under it, so two programs never change the four values at the same time and a recovery never rolls back a switch
/// that another program is still making. A named mutex <c>Global\PCMieru.Party.RunSwitch.v1.&lt;hash&gt;</c>; the hash is the first 16 hex digits (lower case) of the SHA-256 of the SID string of
/// the user (the same rule as the resident and import mutexes of Parts Life). The ACL lets the user and SYSTEM in and nobody else. A mutex belongs to the thread that took it: take it and give it
/// back on one thread (every user of this class does both inside one synchronous call). The name is a parameter so that tests use their own.
/// </summary>
public sealed class RunSwitchLock : IDisposable
{
    public const string Prefix = @"Global\PCMieru.Party.RunSwitch.v1.";

    /// <summary>How long a change waits for the lock before it says "busy" (a change takes a few registry calls).</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(5);

    private Mutex? _mutex;

    private RunSwitchLock(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>The first 16 hex digits (lower case) of the SHA-256 of the SID string (UTF-8).</summary>
    public static string SidHash(string sid) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..16].ToLowerInvariant();

    public static string NameFor(string sid) => Prefix + SidHash(sid);

    /// <summary>The lock name of the user this process runs as.</summary>
    public static string CurrentName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return NameFor(identity.User?.Value ?? throw new InvalidOperationException("the current user has no SID"));
    }

    /// <summary>
    /// Takes the lock, waiting up to <paramref name="timeout"/>. Null when somebody else keeps it (or the name belongs to an object this user may not open). A lock whose owner ended without giving
    /// it back is ours: the owner is gone.
    /// </summary>
    public static RunSwitchLock? TryAcquire(string name, TimeSpan timeout)
    {
        var security = new MutexSecurity();
        using (var identity = WindowsIdentity.GetCurrent())
        {
            security.AddAccessRule(new MutexAccessRule(identity.User!, MutexRights.FullControl, AccessControlType.Allow));
        }

        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), MutexRights.FullControl, AccessControlType.Allow));

        Mutex mutex;
        try
        {
            mutex = MutexAcl.Create(initiallyOwned: false, name, out _, security);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        bool owned;
        try
        {
            owned = mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        if (!owned)
        {
            mutex.Dispose();
            return null;
        }

        return new RunSwitchLock(mutex);
    }

    /// <summary>Gives the lock back. Must be called on the thread that took it.</summary>
    public void Dispose()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        finally
        {
            mutex.Dispose();
        }
    }
}
