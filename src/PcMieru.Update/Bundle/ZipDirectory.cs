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
    uint ExternalAttributes)
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
/// A multi-disk zip, a ZIP64 zip, trailing bytes after the end record and a directory that does not end where the end record starts are all refused:
/// a bundle made by <c>ZipArchive</c> is none of these.
/// </summary>
internal static class ZipDirectory
{
    private const uint EndSignature = 0x06054B50;
    private const uint Zip64LocatorSignature = 0x07064B50;
    private const uint EntrySignature = 0x02014B50;
    private const int EndLength = 22;
    private const int EntryFixedLength = 46;

    /// <summary>
    /// Reads the central directory. <paramref name="maxEntries"/> bounds the entry count before anything is allocated.
    /// On failure the fault is <see cref="BundleFault.NotZip"/>, <see cref="BundleFault.Zip64"/> or <see cref="BundleFault.TooManyFiles"/>.
    /// </summary>
    public static bool TryRead(Stream zip, int maxEntries, out List<ZipCentralEntry> entries, out BundleFault fault, out string detail)
    {
        entries = [];
        fault = BundleFault.None;
        detail = string.Empty;
        try
        {
            return Read(zip, maxEntries, entries, out fault, out detail);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException or NotSupportedException or ObjectDisposedException)
        {
            fault = BundleFault.NotZip;
            detail = "the zip could not be read: " + ex.Message;
            return false;
        }
    }

    private static bool Read(Stream zip, int maxEntries, List<ZipCentralEntry> entries, out BundleFault fault, out string detail)
    {
        fault = BundleFault.NotZip;
        var length = zip.Length;
        if (length < EndLength)
        {
            detail = "too short to be a zip";
            return false;
        }

        // The end record is the last 22 + comment bytes. Scan back for a signature whose comment length lands exactly on the end of the file.
        var tailLength = (int)Math.Min(length, EndLength + ushort.MaxValue);
        var tail = new byte[tailLength];
        zip.Seek(length - tailLength, SeekOrigin.Begin);
        zip.ReadExactly(tail);

        var endAt = -1;
        for (var i = tailLength - EndLength; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != EndSignature)
            {
                continue;
            }

            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20));
            if (i + EndLength + commentLength == tailLength)
            {
                endAt = i;
                break;
            }
        }

        if (endAt < 0)
        {
            detail = "no end-of-central-directory record";
            return false;
        }

        var end = tail.AsSpan(endAt, EndLength);
        var thisDisk = BinaryPrimitives.ReadUInt16LittleEndian(end[4..]);
        var directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(end[6..]);
        var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(end[8..]);
        var totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        var endPosition = length - tailLength + endAt;

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
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(record[32..]);
            var diskStart = BinaryPrimitives.ReadUInt16LittleEndian(record[34..]);
            var externalAttributes = BinaryPrimitives.ReadUInt32LittleEndian(record[38..]);

            var next = at + EntryFixedLength + nameLength + extraLength + commentLength;
            if (next > directory.Length)
            {
                detail = $"entry {index}: the record runs past the central directory";
                return false;
            }

            if (compressed == uint.MaxValue || uncompressed == uint.MaxValue)
            {
                fault = BundleFault.Zip64;
                detail = $"entry {index}: a ZIP64 size";
                return false;
            }

            if (diskStart != 0)
            {
                detail = $"entry {index}: starts on another disk";
                return false;
            }

            // The name as raw bytes, one char per byte: a byte of 0x80 or more is not an ASCII name, and the name rules will refuse it.
            var name = Encoding.Latin1.GetString(directory, at + EntryFixedLength, nameLength);
            entries.Add(new ZipCentralEntry(index, name, versionMadeBy, flags, method, compressed, uncompressed, externalAttributes));
            at = next;
        }

        if (at != directory.Length)
        {
            detail = "bytes after the last central directory record";
            return false;
        }

        fault = BundleFault.None;
        detail = string.Empty;
        return true;
    }
}
