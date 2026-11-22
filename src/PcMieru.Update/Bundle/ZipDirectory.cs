using System.Buffers.Binary;
using System.Text;

namespace PcMieru.Update.Bundle;

/// <summary>One record of a ZIP central directory, as the file says it (nothing is decoded beyond the name).</summary>
internal sealed record ZipCentralEntry(
    int Index,
    string Name,
    ushort VersionMadeBy,
    ushort Flags,
    ushort Method,
    uint CompressedSize,
    uint UncompressedSize,
    uint ExternalAttributes,
    uint LocalHeaderOffset)
{
    /// <summary>A directory entry ends with "/".</summary>
    public bool IsDirectory => Name.EndsWith('/');

    /// <summary>Bit 0 (encrypted), bit 6 (strong encryption) or bit 13 (the local header is masked).</summary>
    public bool IsEncrypted => (Flags & 0x0001) != 0 || (Flags & 0x0040) != 0 || (Flags & 0x2000) != 0;
}

/// <summary>
/// The central directory of a ZIP, read by hand before the zip library is asked anything (DESIGN-B 3.2 item 1: "the central directory first").
/// It exists for what <c>ZipArchive</c> does not tell: the encryption flags, the external attributes (links) and the raw name bytes. The bundle
/// validator then makes the zip library's view agree with this one, or refuses the zip as ambiguous.
///
/// The zip must be the plain thing <c>ZipArchive</c> writes and nothing else:
/// <list type="bullet">
/// <item>The end record is the last 22 bytes of the file and has no comment. (A comment is the one place where a second end record can hide; the zip
///   library takes the LAST end record it finds scanning backwards, a reader that scans forwards takes the first, and a bundle must have only one reading.)
///   Any other end record in the last 64 KiB that could be read as one, that is, a signature with a comment length that fits and a directory that lies
///   before it, makes the zip ambiguous.</item>
/// <item>No multi-disk, no ZIP64 (no ZIP64 locator, no all-ones field, no ZIP64 extra field), and the directory ends exactly where the end record starts.</item>
/// <item>The directory is bounded before it is read: n records cannot take more than n x (46 + 255 + 1 KiB) bytes (a name of at most 255 bytes, an extra
///   field of at most 1 KiB that parses block by block, no per-entry comment).</item>
/// </list>
/// What is not looked at: the flags and the sizes in a local header (the content is judged by its SHA-256 and its length, which the check reads itself),
/// the time stamps and the CRC-32 (the manifest's SHA-256 is the stronger check).
/// </summary>
internal static class ZipDirectory
{
    private const uint EndSignature = 0x06054B50;
    private const uint Zip64LocatorSignature = 0x07064B50;
    private const uint EntrySignature = 0x02014B50;
    private const uint LocalSignature = 0x04034B50;
    private const int EndLength = 22;
    private const int EntryFixedLength = 46;
    private const int LocalFixedLength = 30;

    /// <summary>The longest name a record may have: the limit of a Windows file name, and more than any bundle name.</summary>
    internal const int MaxNameBytes = 255;

    /// <summary>The longest extra field a record may have (ZipArchive writes none).</summary>
    internal const int MaxExtraBytes = 1024;

    private const ushort Zip64ExtraId = 0x0001;

    /// <summary>
    /// Reads the central directory. <paramref name="maxEntries"/> bounds the entry count before anything is allocated.
    /// <paramref name="bodyLength"/> is where the central directory starts: the local headers and the data are below it.
    /// On failure the fault is <see cref="BundleFault.NotZip"/>, <see cref="BundleFault.ZipAmbiguous"/>, <see cref="BundleFault.Zip64"/> or <see cref="BundleFault.TooManyFiles"/>.
    /// </summary>
    public static bool TryRead(Stream zip, int maxEntries, out List<ZipCentralEntry> entries, out long bodyLength, out BundleFault fault, out string detail)
    {
        entries = [];
        bodyLength = 0;
        fault = BundleFault.None;
        detail = string.Empty;
        try
        {
            return Read(zip, maxEntries, entries, out bodyLength, out fault, out detail);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            fault = BundleFault.NotZip;
            detail = "the zip could not be read: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Checks the local header of every entry and returns where its data starts. The header must be where the central record says, must be a local header,
    /// and must name the same file as the central record (a swap of two offsets is read by the zip library without a complaint, through the other's name);
    /// the data must lie below the central directory; a stored entry must have one size.
    /// </summary>
    public static bool TryReadLocalHeaders(Stream zip, IReadOnlyList<ZipCentralEntry> entries, long bodyLength, out long[] dataStarts, out BundleFault fault, out string detail)
    {
        dataStarts = new long[entries.Count];
        fault = BundleFault.None;
        detail = string.Empty;
        try
        {
            return ReadLocalHeaders(zip, entries, bodyLength, dataStarts, out fault, out detail);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            fault = BundleFault.NotZip;
            detail = "the zip could not be read: " + ex.Message;
            return false;
        }
    }

    private static bool IsReadFailure(Exception ex) =>
        ex is IOException or EndOfStreamException or ArgumentException or NotSupportedException or ObjectDisposedException or UnauthorizedAccessException;

    private static bool ReadLocalHeaders(Stream zip, IReadOnlyList<ZipCentralEntry> entries, long bodyLength, long[] dataStarts, out BundleFault fault, out string detail)
    {
        fault = BundleFault.NotZip;
        var header = new byte[LocalFixedLength];
        var name = new byte[MaxNameBytes];
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var shown = BundleNames.Show(entry.Name);
            if (entry.Method == 0 && entry.CompressedSize != entry.UncompressedSize)
            {
                detail = $"'{shown}' is stored with two different sizes";
                return false;
            }

            var offset = (long)entry.LocalHeaderOffset;
            if (offset + LocalFixedLength > bodyLength)
            {
                detail = $"'{shown}': its local header is not inside the zip";
                return false;
            }

            zip.Seek(offset, SeekOrigin.Begin);
            zip.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalSignature)
            {
                detail = $"'{shown}': there is no local header where its central record points";
                return false;
            }

            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            var dataStart = offset + LocalFixedLength + nameLength + extraLength;
            if (nameLength != entry.Name.Length)
            {
                fault = BundleFault.ZipAmbiguous;
                detail = $"the local header of '{shown}' names another file";
                return false;
            }

            if (dataStart + entry.CompressedSize > bodyLength)
            {
                detail = $"'{shown}': its data runs into the central directory";
                return false;
            }

            zip.ReadExactly(name, 0, nameLength);
            var localName = Encoding.Latin1.GetString(name, 0, nameLength);
            if (!string.Equals(entry.Name, localName, StringComparison.Ordinal))
            {
                fault = BundleFault.ZipAmbiguous;
                detail = $"the local header of '{shown}' names '{BundleNames.Show(localName)}'";
                return false;
            }

            dataStarts[i] = dataStart;
        }

        fault = BundleFault.None;
        detail = string.Empty;
        return true;
    }

    private static bool Read(Stream zip, int maxEntries, List<ZipCentralEntry> entries, out long bodyLength, out BundleFault fault, out string detail)
    {
        fault = BundleFault.NotZip;
        bodyLength = 0;
        var length = zip.Length;
        if (length < EndLength)
        {
            detail = "too short to be a zip";
            return false;
        }

        // The last 64 KiB and the end record: the window in which an end record (with a comment of any length) can be, and in which a second one can hide.
        var tailLength = (int)Math.Min(length, EndLength + ushort.MaxValue);
        var tail = new byte[tailLength];
        zip.Seek(length - tailLength, SeekOrigin.Begin);
        zip.ReadExactly(tail);

        var endAt = tailLength - EndLength;
        if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(endAt)) != EndSignature)
        {
            // Told apart for the person reading the log: a proper end record further back, with a comment after it, is the comment's fault.
            var withComment = LastFittingEnd(tail);
            detail = withComment >= 0
                ? $"the zip has a comment of {tailLength - withComment - EndLength} bytes after its end record (a bundle has none; the end record is the last 22 bytes)"
                : "no end-of-central-directory record at the end of the file";
            return false;
        }

        var end = tail.AsSpan(endAt, EndLength);
        var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(end[20..]);
        if (commentLength != 0)
        {
            detail = $"the end record says it has a comment of {commentLength} bytes (a bundle has none)";
            return false;
        }

        var thisDisk = BinaryPrimitives.ReadUInt16LittleEndian(end[4..]);
        var directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(end[6..]);
        var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(end[8..]);
        var totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        var endPosition = length - EndLength;

        if (totalEntries == ushort.MaxValue || directorySize == uint.MaxValue || directoryOffset == uint.MaxValue
            || (endAt >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(endAt - 20)) == Zip64LocatorSignature))
        {
            fault = BundleFault.Zip64;
            detail = "a ZIP64 zip";
            return false;
        }

        if (thisDisk != 0 || directoryDisk != 0 || entriesOnDisk != totalEntries)
        {
            detail = "a multi-disk zip";
            return false;
        }

        if (totalEntries > maxEntries)
        {
            fault = BundleFault.TooManyFiles;
            detail = $"{totalEntries} entries, more than the limit";
            return false;
        }

        if (directoryOffset + (long)directorySize != endPosition)
        {
            detail = "the central directory does not end where the end record starts";
            return false;
        }

        // The size is bounded before it is allocated: what the entry count could fill, and no less than the fixed parts. A lying end record (one entry,
        // a directory of a gigabyte) is refused here, having cost a read of the last 64 KiB.
        var most = (long)totalEntries * (EntryFixedLength + MaxNameBytes + MaxExtraBytes);
        if (directorySize > most || directorySize < (long)totalEntries * EntryFixedLength)
        {
            detail = $"a central directory of {directorySize} bytes cannot hold {totalEntries} entries as a bundle has them";
            return false;
        }

        // Only one end record may be readable in the file. The real one is at the end; if another could be read, the file has two readings.
        var other = OtherEnd(tail, endAt, length - tailLength);
        if (other >= 0)
        {
            fault = BundleFault.ZipAmbiguous;
            detail = $"a second end-of-central-directory record, {tailLength - EndLength - other} bytes before the end record at the end of the file";
            return false;
        }

        var directory = new byte[directorySize];
        zip.Seek(directoryOffset, SeekOrigin.Begin);
        zip.ReadExactly(directory);

        var at = 0;
        for (var index = 0; index < totalEntries; index++)
        {
            if (at + EntryFixedLength > directory.Length || BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(at)) != EntrySignature)
            {
                detail = $"entry {index}: not a central directory record";
                return false;
            }

            var record = directory.AsSpan(at, EntryFixedLength);
            var versionMadeBy = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
            var compressed = BinaryPrimitives.ReadUInt32LittleEndian(record[20..]);
            var uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(record[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(record[30..]);
            var recordComment = BinaryPrimitives.ReadUInt16LittleEndian(record[32..]);
            var diskStart = BinaryPrimitives.ReadUInt16LittleEndian(record[34..]);
            var externalAttributes = BinaryPrimitives.ReadUInt32LittleEndian(record[38..]);
            var localHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[42..]);

            if (nameLength > MaxNameBytes)
            {
                detail = $"entry {index}: a name of {nameLength} bytes (the limit is {MaxNameBytes})";
                return false;
            }

            if (extraLength > MaxExtraBytes)
            {
                detail = $"entry {index}: an extra field of {extraLength} bytes (the limit is {MaxExtraBytes})";
                return false;
            }

            if (recordComment != 0)
            {
                detail = $"entry {index}: a comment of {recordComment} bytes (a bundle has none)";
                return false;
            }

            var next = at + EntryFixedLength + nameLength + extraLength;
            if (next > directory.Length)
            {
                detail = $"entry {index}: the record runs past the central directory";
                return false;
            }

            if (!ExtraFieldsParse(directory.AsSpan(at + EntryFixedLength + nameLength, extraLength), out var zip64Extra))
            {
                detail = $"entry {index}: the extra field does not parse";
                return false;
            }

            if (zip64Extra || compressed == uint.MaxValue || uncompressed == uint.MaxValue || localHeaderOffset == uint.MaxValue)
            {
                fault = BundleFault.Zip64;
                detail = $"entry {index}: " + (zip64Extra ? "a ZIP64 extra field" : "a ZIP64 size or offset");
                return false;
            }

            if (diskStart != 0)
            {
                detail = $"entry {index}: starts on another disk";
                return false;
            }

            // The name as raw bytes, one char per byte: a byte of 0x80 or more is not an ASCII name, and the name rules will refuse it.
            var name = Encoding.Latin1.GetString(directory, at + EntryFixedLength, nameLength);
            entries.Add(new ZipCentralEntry(index, name, versionMadeBy, flags, method, compressed, uncompressed, externalAttributes, localHeaderOffset));
            at = next;
        }

        if (at != directory.Length)
        {
            detail = "bytes after the last central directory record";
            return false;
        }

        bodyLength = directoryOffset;
        fault = BundleFault.None;
        detail = string.Empty;
        return true;
    }

    /// <summary>The last offset in <paramref name="tail"/> that holds an end record whose comment ends exactly at the end of the tail, or -1.</summary>
    private static int LastFittingEnd(byte[] tail)
    {
        for (var i = tail.Length - EndLength; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EndSignature
                && i + EndLength + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) == tail.Length)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The offset of another end record in the tail (before the one at <paramref name="realAt"/>), or -1. Another one is a signature that a reader could take
    /// for an end record: its comment length fits in the rest of the file and the directory it describes lies before it. A random "PK\5\6" in compressed
    /// data does not look like that (it needs a plausible directory offset and size as well: about 3 in 100 million per release).
    /// </summary>
    private static int OtherEnd(byte[] tail, int realAt, long tailStart)
    {
        for (var i = realAt - 1; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != EndSignature || i + EndLength > tail.Length)
            {
                continue;
            }

            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 12));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 16));
            if (i + EndLength + commentLength <= tail.Length && offset + (long)size <= tailStart + i)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The extra field as blocks of (id, size, data): they must tile the field exactly. <paramref name="zip64"/> says a block has the ZIP64 id.</summary>
    private static bool ExtraFieldsParse(ReadOnlySpan<byte> extra, out bool zip64)
    {
        zip64 = false;
        var at = 0;
        while (at < extra.Length)
        {
            if (at + 4 > extra.Length)
            {
                return false;
            }

            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra[at..]);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(extra[(at + 2)..]);
            if (at + 4 + size > extra.Length)
            {
                return false;
            }

            if (id == Zip64ExtraId)
            {
                zip64 = true;
            }

            at += 4 + size;
        }

        return true;
    }
}
