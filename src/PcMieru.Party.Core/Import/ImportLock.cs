using System.Security.AccessControl;
using System.Security.Principal;

namespace PcMieru.Party.Import;

/// <summary>
/// The per-user lock of the import (DESIGN-C 4.1 item 4): a named mutex <c>Global\PCMieru.Party.Import.v1.&lt;hash&gt;</c> (the same SID
/// hash as the resident mutex, see <see cref="PartyMutexNames"/>) that serializes the GUI's hand-over, the first start of Party and the
/// first creation of the destination. The ACL lets the user and SYSTEM in and nobody else.
/// Taking and giving back happen on ONE thread (a mutex belongs to the thread that took it): every engine call takes it and gives it
/// back inside the call. The name is a parameter so that tests use their own.
/// </summary>
public sealed class ImportLock : IDisposable
{
    private Mutex? _mutex;

    private ImportLock(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>The lock name of the user this process runs as.</summary>
    public static string CurrentName() => PartyMutexNames.CurrentImportName();

    /// <summary>
    /// Takes the lock, waiting up to <paramref name="timeout"/>. Null when somebody else keeps it (or the name belongs to an object this
    /// user may not open). A lock whose owner ended without giving it back is ours: the owner is gone.
    /// </summary>
    public static ImportLock? TryAcquire(string name, TimeSpan timeout)
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

        return new ImportLock(mutex);
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
