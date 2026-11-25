using System.Buffers;

namespace PcMieru.Party.Import;

/// <summary>Thrown by <see cref="CappedBuffer"/> when more than the allowed number of bytes would be written.</summary>
internal sealed class OutputTooLargeException() : Exception("output too large");

/// <summary>
/// A growing byte buffer for <see cref="System.Text.Json.Utf8JsonWriter"/> that refuses to hold more than a fixed number of
/// bytes: the converted state is written in one pass and, if it would be larger than the cap, the write stops there.
/// </summary>
internal sealed class CappedBuffer(int limit) : IBufferWriter<byte>
{
    private byte[] _buf = new byte[1024];
    private int _len;

    public ReadOnlySpan<byte> Written => _buf.AsSpan(0, _len);

    public byte[] ToArray() => Written.ToArray();

    public void Advance(int count)
    {
        if (count < 0 || (long)_len + count > limit) throw new OutputTooLargeException();
        _len += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buf.AsMemory(_len);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buf.AsSpan(_len);
    }

    private void Ensure(int hint)
    {
        if (hint <= 0) hint = 256;
        long need = (long)_len + hint;
        if (need <= _buf.Length) return;
        long size = Math.Max(need, Math.Min((long)_buf.Length * 2, (long)limit + 1));
        if (size > Array.MaxLength) throw new OutputTooLargeException();
        Array.Resize(ref _buf, (int)size);
    }
}
