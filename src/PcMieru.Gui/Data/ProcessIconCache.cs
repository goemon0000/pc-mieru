namespace PcMieru.Gui;

/// <summary>
/// Program icons for the process list and the detail pane. The icon of a process is the icon of its image file, so the
/// cache is keyed by path: fifteen browser processes share one entry. <see cref="TryGet"/> never blocks: it returns what is
/// known and starts the lookup for the rest, off the UI thread; <see cref="Loaded"/> fires on the UI thread when an answer
/// arrives. A file without an icon (or one that cannot be read) stays null: the list draws nothing in the slot, never a guess.
/// </summary>
internal sealed class ProcessIconCache : IDisposable
{
    /// <summary>Requested from the source; drawn smaller (down-scaling stays sharp at every DPI).</summary>
    public const int RequestedSize = 32;

    private const int Capacity = 512;

    /// <summary>A transient failure (timeout, a locked file) is asked again after this long, not on every repaint.</summary>
    private const long RetryAfterMs = 30_000;

    private readonly IProcessIconSource _source;
    private readonly Control _owner;
    private readonly Func<long> _nowMs;
    private readonly Dictionary<string, Entry> _done = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private readonly record struct Entry(Image? Image, long RetryAtMs);

    public ProcessIconCache(IProcessIconSource source, Control owner, Func<long>? nowMs = null)
    {
        _source = source;
        _owner = owner;
        _nowMs = nowMs ?? (static () => Environment.TickCount64);
    }

    public event Action? Loaded;

    /// <summary>Entries held (icons and remembered "no icon" answers); bounded.</summary>
    internal int Count => _done.Count;

    public Image? TryGet(string path)
    {
        if (_disposed || string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (_done.TryGetValue(path, out var found))
        {
            if (found.Image is not null || found.RetryAtMs == 0 || _nowMs() < found.RetryAtMs)
            {
                return found.Image;
            }
        }

        if (_pending.Add(path))
        {
            _ = Fetch(path);
        }

        return null;
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var entry in _done.Values)
        {
            entry.Image?.Dispose();
        }

        _done.Clear();
    }

    private static Image? Decode(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            using var raw = Image.FromStream(stream);
            return new Bitmap(raw); // a copy, so it does not depend on the stream
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            return null; // not a PNG the system can read: the same as no icon
        }
    }

    private async Task Fetch(string path)
    {
        Image? image = null;
        var transient = false;
        try
        {
            var png = await _source.GetPngAsync(path, RequestedSize).ConfigureAwait(false);
            if (png.TryGet(out var bytes))
            {
                image = Decode(bytes);
            }
            else
            {
                transient = png.Status is ValueStatus.Timeout or ValueStatus.Error;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            transient = true;
        }

        void Done()
        {
            _pending.Remove(path);
            if (_disposed)
            {
                image?.Dispose();
                return;
            }

            if (_done.Count >= Capacity)
            {
                // Not disposed: a row being painted may still hold one; the finalizer frees them.
                _done.Clear();
            }

            _done[path] = new Entry(image, transient && image is null ? _nowMs() + RetryAfterMs : 0);
            Loaded?.Invoke();
        }

        // Answers come back on the owner's UI thread. Without a window yet (a test), they are applied where they arrive.
        if (_owner.IsDisposed)
        {
            image?.Dispose();
            return;
        }

        if (_owner.IsHandleCreated)
        {
            _owner.BeginInvoke(Done);
        }
        else
        {
            Done();
        }
    }
}
