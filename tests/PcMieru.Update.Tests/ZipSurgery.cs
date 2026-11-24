using System.Buffers.Binary;

namespace PcMieru.Update.Tests;

/// <summary>
/// Cuts a finished test zip apart and puts it together again with something extra in the central directory or after it: ZipArchive never writes an
/// extra field, a per-entry comment, a zip comment or a second end record, and a test that hands the validator one of these has to write it by hand.
/// Every function expects a zip that ends with a plain 22-byte end record (what <see cref="TestBundle.Write"/> makes).
/// </summary>
internal static class ZipSurgery
{
    public const int EndLength = 22;

    private const uint EndSignature = 0x06054B50;

    /// <summary>Everything before the central directory, and the central records as whole byte arrays (fixed part, name, extra, comment).</summary>
    public static (byte[] Body, List<byte[]> Records) Split(byte[] zip)
    {
        var end = zip.Length - EndLength;
        Assert.Equal(EndSignature, BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end)));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(end + 10));
        var directoryAt = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end + 16));

        var records = new List<byte[]>();
        var at = directoryAt;
        for (var i = 0; i < count; i++)
        {
            var length = 46
                + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(at + 28))
                + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(at + 30))
                + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(at + 32));
            records.Add(zip[at..(at + length)]);
            at += length;
        }

        Assert.Equal(end, at);
        return (zip[..directoryAt], records);
    }

    public static byte[] Directory(IEnumerable<byte[]> records) => records.SelectMany(r => r).ToArray();

    /// <summary>An end record. <paramref name="commentLengthField"/> is what the record says about its comment (the real length when null).</summary>
    public static byte[] End(int count, int directorySize, int directoryOffset, byte[]? comment = null, ushort? commentLengthField = null)
    {
        comment ??= [];
        var end = new byte[EndLength + comment.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(end, EndSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), (ushort)count);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), (uint)directorySize);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), (uint)directoryOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(20), commentLengthField ?? (ushort)comment.Length);
        comment.CopyTo(end, EndLength);
        return end;
    }

    /// <summary>The body, the records as the central directory, and an end record that matches (with the given zip comment, if any).</summary>
    public static byte[] Join(byte[] body, IReadOnlyList<byte[]> records, byte[]? zipComment = null)
    {
        var directory = Directory(records);
        return [.. body, .. directory, .. End(records.Count, directory.Length, body.Length, zipComment)];
    }

    /// <summary>The record with an extra field (inserted after the name).</summary>
    public static byte[] WithExtra(byte[] record, byte[] extra)
    {
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(28));
        var oldExtra = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(30));
        var at = 46 + nameLength;
        var result = new byte[record.Length + extra.Length];
        record.AsSpan(0, at).CopyTo(result);
        extra.CopyTo(result, at);
        record.AsSpan(at).CopyTo(result.AsSpan(at + extra.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(30), (ushort)(oldExtra + extra.Length));
        return result;
    }

    /// <summary>The record with a comment (appended).</summary>
    public static byte[] WithComment(byte[] record, byte[] comment)
    {
        var result = new byte[record.Length + comment.Length];
        record.CopyTo(result, 0);
        comment.CopyTo(result, record.Length);
        var old = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(32));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(32), (ushort)(old + comment.Length));
        return result;
    }

    /// <summary>An extra field block: a header id, its size and that many bytes.</summary>
    public static byte[] ExtraBlock(ushort id, int dataLength, byte fill = 0x55)
    {
        var block = new byte[4 + dataLength];
        BinaryPrimitives.WriteUInt16LittleEndian(block, id);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2), (ushort)dataLength);
        Array.Fill(block, fill, 4, dataLength);
        return block;
    }

    /// <summary>The name of a record, as ASCII.</summary>
    public static string NameOf(byte[] record) =>
        System.Text.Encoding.ASCII.GetString(record, 46, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(28)));

    public static uint LocalOffsetOf(byte[] record) => BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(42));

    public static void SetLocalOffset(byte[] record, uint offset) => BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(42), offset);

    public static void SetExternalAttributes(byte[] record, uint attributes) => BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(38), attributes);

    /// <summary>
    /// A zip with a second, fake end record planted in the first one's comment, after a second central directory (<paramref name="fakeRecords"/>) that the
    /// fake record points to. The file ends with the fake end record, which says <paramref name="fakeCommentLengthField"/> bytes of comment follow it
    /// (0 makes it a perfectly proper last end record; anything else is a lie). The first end record, and the directory before it, are those of the real zip.
    /// </summary>
    public static byte[] WithPlantedEnd(byte[] zip, IReadOnlyList<byte[]> fakeRecords, ushort fakeCommentLengthField)
    {
        var (body, realRecords) = Split(zip);
        var realDirectory = Directory(realRecords);
        var fakeDirectory = Directory(fakeRecords);
        var firstEndAt = body.Length + realDirectory.Length;
        var fake = End(fakeRecords.Count, fakeDirectory.Length, firstEndAt + EndLength, comment: null, commentLengthField: fakeCommentLengthField);
        byte[] comment = [.. fakeDirectory, .. fake];
        return [.. body, .. realDirectory, .. End(realRecords.Count, realDirectory.Length, body.Length, comment)];
    }
}
