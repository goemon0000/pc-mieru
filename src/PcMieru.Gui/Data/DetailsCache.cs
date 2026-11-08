namespace PcMieru.Gui;

/// <summary>
/// Details (path, product, signature...) are slower than a sample, so they are asked for one process at a time, off the
/// UI thread, and remembered. <see cref="TryGet"/> never blocks: it returns what is known and starts the lookup for the
/// rest; <see cref="Loaded"/> fires on the UI thread when an answer arrives.
/// </summary>
internal sealed class DetailsCache
{
    private const int Capacity = 256;

    private readonly IProcessDetailsProvider _provider;
    private readonly Control _owner;
    private readonly Dictionary<ProcessKey, ProcessDetails> _done = [];
    private readonly HashSet<ProcessKey> _pending = [];

    public DetailsCache(IProcessDetailsProvider provider, Control owner)
    {
        _provider = provider;
        _owner = owner;
    }

    public event Action<ProcessKey>? Loaded;

    public ProcessDetails? TryGet(ProcessKey key)
    {
        if (_done.TryGetValue(key, out var found))
        {
            return found;
        }

        if (_pending.Add(key))
        {
            _ = Fetch(key);
        }

        return null;
    }

    public void Forget(ProcessKey key) => _done.Remove(key);

    private async Task Fetch(ProcessKey key)
    {
        ProcessDetails? details = null;
        try
        {
            details = await Task.Run(() => _provider.GetAsync(key)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A process that vanished or cannot be opened is a normal answer, not an error to show.
        }

        void Done()
        {
            _pending.Remove(key);
            if (details is null)
            {
                return;
            }

            if (_done.Count >= Capacity)
            {
                _done.Clear();
            }

            _done[key] = details;
            Loaded?.Invoke(key);
        }

        // Answers come back on the owner's UI thread. Without a window yet (a test), they are applied where they arrive.
        if (_owner.IsDisposed)
        {
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
