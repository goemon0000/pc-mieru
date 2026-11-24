using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PcMieru.Update.Launch;

/// <summary>
/// A conversation of single text lines between two programs of one install (the GUI and the runner for the hand-off, the runner and the program it verifies for the launch check). Every line is UTF-8 up to a
/// line feed and at most <see cref="PipeChannel.MaxLine"/> characters; a line that is longer, or bytes that are not text, close the channel. Tests make an in-memory one.
/// </summary>
public interface IMessageChannel : IDisposable
{
    /// <summary>Writes one line. False when the other end is gone (or the channel was closed).</summary>
    bool Send(string line);

    /// <summary>The next line that arrived, waiting at most <paramref name="timeout"/>. False on a timeout, or when the channel is closed and nothing is left to read.</summary>
    bool TryReceive(TimeSpan timeout, out string line);

    /// <summary>True once the other end closed, or this end was disposed, or a line broke the rules.</summary>
    bool IsClosed { get; }
}

/// <summary>
/// <see cref="IMessageChannel"/> over one end of the pipe made by <c>LaunchNative.TryCreatePipe</c> (overlapped, both directions). A thread of its own reads, so that a read that is waiting never holds up a write
/// from another thread (the pong of a launch check is written from the UI thread while the reader waits). Only kernel32 is used (overlapped ReadFile and WriteFile through LaunchNative; RandomAccess refuses an unseekable overlapped handle): the runner is NativeAOT and may not load System.IO.Pipes.
/// </summary>
public sealed class PipeChannel : IMessageChannel
{
    /// <summary>The longest line, in characters.</summary>
    public const int MaxLine = 4096;

    /// <summary>How long a write may wait for the other end to read before it is given up (the pipe's buffer is full only when the other end does not read at all).</summary>
    private static readonly TimeSpan WriteLimit = TimeSpan.FromSeconds(10);

    private readonly SafeFileHandle _handle;
    private readonly object _lock = new();
    private readonly object _writeLock = new();
    private readonly Queue<string> _lines = new();
    private readonly Thread _reader;
    private bool _closed;
    private bool _disposed;

    public PipeChannel(SafeFileHandle handle)
    {
        _handle = handle;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "pcm-pipe-reader" };
        _reader.Start();
    }

    /// <summary>The channel over a handle that was inherited, by its value. Null when it is not a pipe (nothing is closed then: the value is not ours to close).</summary>
    public static PipeChannel? FromInheritedHandle(nint value) =>
        LaunchNative.IsPipe(value) ? new PipeChannel(new SafeFileHandle(value, ownsHandle: true)) : null;

    public bool IsClosed
    {
        get
        {
            lock (_lock)
            {
                return _closed && _lines.Count == 0;
            }
        }
    }

    public bool Send(string line) => Send(line, WriteLimit);

    internal bool Send(string line, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || line.Length > MaxLine || line.Contains('\n') || line.Contains('\r'))
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (_writeLock)
        {
            lock (_lock)
            {
                if (_closed || _disposed)
                {
                    return false;
                }
            }

            if (LaunchNative.WriteAll(_handle, bytes, timeout < WriteLimit ? timeout : WriteLimit))
            {
                return true;
            }

            Close();
            return false;
        }
    }

    public bool TryReceive(TimeSpan timeout, out string line)
    {
        var deadline = Environment.TickCount64 + (long)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
        lock (_lock)
        {
            while (_lines.Count == 0)
            {
                var left = deadline - Environment.TickCount64;
                if (_closed || left <= 0)
                {
                    line = string.Empty;
                    return false;
                }

                Monitor.Wait(_lock, (int)Math.Min(left, 250));
            }

            line = _lines.Dequeue();
            return true;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _closed = true;
            Monitor.PulseAll(_lock);
        }

        // A read that is waiting holds the handle open: cancel it until the reader has left, then the handle closes.
        for (var i = 0; i < 40 && _reader.IsAlive; i++)
        {
            LaunchNative.CancelPending(_handle);
            _reader.Join(25);
        }

        _handle.Dispose();
    }

    private void Close()
    {
        lock (_lock)
        {
            _closed = true;
            Monitor.PulseAll(_lock);
        }
    }

    private void ReadLoop()
    {
        var chunk = new byte[1024];
        var pending = new List<byte>();
        try
        {
            while (true)
            {
                var count = LaunchNative.ReadSome(_handle, chunk);
                if (count <= 0)
                {
                    break; // the other end closed, or the read failed or was cancelled (Dispose)
                }

                for (var i = 0; i < count; i++)
                {
                    if (chunk[i] != (byte)'\n')
                    {
                        pending.Add(chunk[i]);
                        if (pending.Count > MaxLine * 4)
                        {
                            return;
                        }

                        continue;
                    }

                    string text;
                    try
                    {
                        text = new UTF8Encoding(false, true).GetString(pending.ToArray());
                    }
                    catch (ArgumentException)
                    {
                        return; // not text
                    }

                    pending.Clear();
                    if (text.Length > MaxLine)
                    {
                        return;
                    }

                    lock (_lock)
                    {
                        _lines.Enqueue(text.TrimEnd('\r'));
                        Monitor.PulseAll(_lock);
                    }
                }
            }
        }
        finally
        {
            Close();

            // The conversation is over, for whatever reason (the other end closed, a line broke the rules): let go of the handle, so that the other end sees a broken pipe and not a reader that no longer reads.
            // A write in progress on another thread holds its own reference, and Dispose() of the channel takes the same path again.
            _handle.Dispose();
        }
    }
}
