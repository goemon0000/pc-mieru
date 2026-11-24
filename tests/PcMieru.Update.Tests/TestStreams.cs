namespace PcMieru.Update.Tests;

/// <summary>A readable stream that cannot seek (a network or pipe stream): the zip validator needs to seek and must say so.</summary>
internal sealed class NonSeekableStream(Stream inner) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A stream with no end: it fills every read with the given byte and counts how many bytes were taken. It stands for a decompressor that lies about its length.</summary>
internal sealed class EndlessStream(byte fill = 0) : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Array.Fill(buffer, fill, offset, count);
        BytesRead += count;
        return count;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// A seekable, readable stream that throws on the given read and on every read after it (a disk or a share that goes away while the zip is read).
/// <see cref="Triggered"/> says whether the failure was reached at all, so a test can sweep the failure over every read of a validation.
/// </summary>
internal sealed class FailingReadStream(Stream inner, int failOnRead, Func<Exception> failure) : Stream
{
    private int _reads;

    public bool Triggered { get; private set; }

    /// <summary>How many bytes were really handed out (a test of an allocation bound looks at this).</summary>
    public long BytesRead { get; private set; }

    /// <summary>A stream that only counts: it never fails.</summary>
    public static FailingReadStream Counting(Stream inner) => new(inner, int.MaxValue, () => new IOException("never"));

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Count();
        var read = inner.Read(buffer, offset, count);
        BytesRead += read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        Count();
        var read = inner.Read(buffer);
        BytesRead += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Count()
    {
        if (++_reads >= failOnRead)
        {
            Triggered = true;
            throw failure();
        }
    }
}
