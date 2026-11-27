using System.Security.AccessControl;
using System.Security.Principal;
using PcMieru.Party.Import;

namespace PcMieru.Party;

/// <summary>
/// The one resident of a user (DESIGN-C 3.2): a named mutex that Parts Life shares with the old product's last version, so that two programs never write the same save at once. The name is
/// <c>Global\PCMieru.PartsLife.Resident.v1.&lt;hash&gt;</c>, the hash being the first 16 hex digits (lower case) of the SHA-256 of the SID string of the user (UTF-8): the same user gets the same
/// name in every session, another user a different one. The ACL lets the user and SYSTEM in and nobody else.
/// Taking and giving back happen on one thread: the main (STA) thread of Main, which is also the one that runs the window.
/// </summary>
internal sealed class ResidentInstance : IDisposable
{
    internal const string NamePrefix = PartyMutexNames.ResidentPrefix;

    private Mutex? _mutex;

    private ResidentInstance(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>The mutex name of the user whose SID string is <paramref name="sid"/>.</summary>
    internal static string NameFor(string sid) => PartyMutexNames.ResidentNameFor(sid);

    /// <summary>The name for the user this process runs as.</summary>
    internal static string CurrentName() => PartyMutexNames.CurrentResidentName();

    /// <summary>
    /// Becomes the resident; null when someone else is (another session, another start of this program, the old product). A mutex whose owner ended without giving it back (abandoned) is ours:
    /// the owner is gone, so there is nobody to protect. A mutex that exists but may not be opened by this user is somebody else's too: the name is per user and the ACL is ours, so it is
    /// not a mutex of the product.
    /// </summary>
    public static ResidentInstance? TryAcquire(string name)
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
            owned = mutex.WaitOne(0);
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

        return new ResidentInstance(mutex);
    }

    /// <summary>Gives the mutex back. Must be called on the thread that took it.</summary>
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
