using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PcMieru.Checks.Storage;

/// <summary>
/// The short cross-process lock of the baseline (DESIGN-A §14.5): it is taken at the start of a run to hand out the next
/// <c>acq</c>, and again at record time. It is never held while sampling. An interface so that a test can run something at the
/// moment a lock is taken and so simulate the interleaving of two runs.
/// </summary>
internal interface IBaselineLock
{
    /// <summary>The lock, held until the returned object is disposed, or null when it could not be taken within <paramref name="timeout"/>.</summary>
    IDisposable? TryAcquire(TimeSpan timeout);
}

/// <summary>
/// A named mutex of the session that identifies the user and the resolved root folder, so two runs of the same user on the same
/// folder exclude each other and any other folder (or another user's) is independent. The owner thread must also be the one that
/// releases it, so a lock is taken and released inside one synchronous call.
/// </summary>
internal sealed class NamedMutexBaselineLock : IBaselineLock
{
    private readonly string _name;

    public NamedMutexBaselineLock(string userId, string root)
    {
        _name = MutexName(userId, root);
    }

    /// <summary>"Local\PCMieru.Baseline." and 32 hex digits of SHA-256 over the user and the full root path (case folded, no trailing separator).</summary>
    public static string MutexName(string userId, string root)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(userId + "|" + full));
        return "Local\\PCMieru.Baseline." + Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    /// <summary>The user's SID when Windows says it, else the user name: never empty.</summary>
    public static string CurrentUserId()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or InvalidOperationException or UnauthorizedAccessException)
        {
            return Environment.UserName;
        }
    }

    public IDisposable? TryAcquire(TimeSpan timeout)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, _name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException or ArgumentException)
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
            // The last owner died without releasing it. The mutex is ours now; what it guarded is files that are replaced whole.
            owned = true;
        }

        if (!owned)
        {
            mutex.Dispose();
            return null;
        }

        return new Held(mutex);
    }

    private sealed class Held(Mutex mutex) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Released from another thread than the one that took it: there is nothing left to release.
            }

            mutex.Dispose();
        }
    }
}
