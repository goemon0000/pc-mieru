// Provenance: the idea (a release made in memory from a small table of files, with knobs to break it) is that of FakeRelease.Zip in the owner's pl-cloud
// tests/PartsLife.Update.Tests/UpdaterTests.cs (Parts Life, MIT, same owner; see NOTICE.txt). The code is new: it writes the central directory fields
// by hand where a test needs a flag or an attribute that ZipArchive would not write.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PcMieru.Update.Bundle;

namespace PcMieru.Update.Tests;

/// <summary>One entry of a test zip, with optional overrides of what the central directory says.</summary>
internal sealed class RawEntry(string name, byte[] data)
{
    public string Name { get; set; } = name;

    public byte[] Data { get; set; } = data;

    public ushort? VersionMadeBy { get; set; }

    public ushort? Flags { get; set; }

    public ushort? Method { get; set; }

    public uint? CompressedSize { get; set; }

    public uint? UncompressedSize { get; set; }

    public uint? ExternalAttributes { get; set; }

    /// <summary>The offset of the local header, as the central directory says it.</summary>
    public uint? LocalHeaderOffset { get; set; }
}

/// <summary>
/// A small, valid pcm-bundle/1 release in memory: the files of <see cref="BundleFloor.ForThisBuild"/> plus a few more, the inner manifest, the outer
/// manifest and a zip. A test changes one thing (a file, a manifest, an entry) and says which fault must follow.
/// </summary>
internal sealed class TestBundle
{
    public const string DefaultBuildId = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";

    public TestBundle(string version = "0.5.0")
    {
        Version = version;
        foreach (var name in BundleFloor.ForThisBuild)
        {
            if (name != BundleFormat.InnerManifestName)
            {
                Files[name] = Encoding.ASCII.GetBytes("content of " + name + " in " + version);
            }
        }

        // The gate file is the one file whose bytes are fixed (DESIGN-B 4): the validator checks its exact hash (BundleFault.GateFileChanged).
        Files[BundleFormat.GateFileName] = PcMieru.Wire.Gate.GateFile.Bytes();

        Files["System.Runtime.dll"] = Encoding.ASCII.GetBytes(new string('r', 3000));
        Files["createdump.exe"] = Encoding.ASCII.GetBytes("createdump");
        Files["NOTICE.txt"] = Encoding.ASCII.GetBytes("notice");
        Files["LICENSE"] = Encoding.ASCII.GetBytes("license");
        Files["ja/PcMieru.Core.resources.dll"] = Encoding.ASCII.GetBytes("ja satellite");
        Files["en/PcMieru.Core.resources.dll"] = Encoding.ASCII.GetBytes("en satellite");
    }

    public string Version { get; }

    public string BuildId { get; set; } = DefaultBuildId;

    /// <summary>The files by name below the root, without the inner manifest.</summary>
    public SortedDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public string Root => BundleFormat.RootFolder(Version);

    public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static BundleFile Line(string path, byte[] data) => new(path, data.Length, Sha(data));

    /// <summary>The inner manifest of the files as they are now.</summary>
    public BundleManifest Inner() => new(Version, BuildId, Files.Select(f => Line(f.Key, f.Value)));

    public byte[] InnerBytes() => Inner().ToUtf8Bytes();

    /// <summary>The outer manifest: the files and the inner manifest's line, for the given inner bytes.</summary>
    public BundleManifest Outer(byte[]? innerBytes = null) =>
        new(Version, BuildId, Files.Select(f => Line(f.Key, f.Value)).Append(Line(BundleFormat.InnerManifestName, innerBytes ?? InnerBytes())));

    /// <summary>The entries of a zip of this bundle (the inner manifest included), under the root folder, in ordinal order.</summary>
    public List<RawEntry> Entries(byte[]? innerBytes = null)
    {
        var all = Files.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        all[BundleFormat.InnerManifestName] = innerBytes ?? InnerBytes();
        return all.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new RawEntry(Root + "/" + f.Key, f.Value)).ToList();
    }

    public byte[] Zip(Action<List<RawEntry>>? edit = null)
    {
        var entries = Entries();
        edit?.Invoke(entries);
        return Write(entries);
    }

    /// <summary>Validates a zip against this bundle's outer manifest (or the given one), with the floor of this build unless another is given.</summary>
    public BundleResult Validate(byte[] zip, BundleManifest? outer = null, Limits? limits = null, string? version = null, IReadOnlyCollection<string>? floor = null)
    {
        using var stream = new MemoryStream(zip);
        return BundleZip.Validate(stream, version ?? Version, outer ?? Outer(), limits ?? Limits.Default, floor ?? BundleFloor.ForThisBuild);
    }

    // ----------------------------------------------------------------------------------------------------------------------- zip writing

    /// <summary>Writes the entries in order with ZipArchive (a name is used as it is, so "a\b" stays "a\b"), then applies the central directory overrides.</summary>
    public static byte[] Write(IReadOnlyList<RawEntry> entries)
    {
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                var created = archive.CreateEntry(entry.Name, CompressionLevel.Optimal);
                if (entry.Data.Length == 0)
                {
                    continue; // a directory entry, or an empty file: nothing to write
                }

                using var stream = created.Open();
                stream.Write(entry.Data);
            }
        }

        var bytes = buffer.ToArray();
        PatchCentral(bytes, entries);
        return bytes;
    }

    /// <summary>
    /// Writes a zip by hand with every entry stored (method 0, no compression), the way a few zip tools do: ZipArchive does not let a test choose the
    /// method, and the validator must read stored entries as well as deflated ones.
    /// </summary>
    public static byte[] WriteStored(IReadOnlyList<RawEntry> entries)
    {
        var output = new MemoryStream();
        var offsets = new List<uint>();
        foreach (var entry in entries)
        {
            offsets.Add((uint)output.Length);
            var name = Encoding.ASCII.GetBytes(entry.Name);
            var header = new byte[30];
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0x04034B50);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 20);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), Crc32(entry.Data));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), (uint)entry.Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22), (uint)entry.Data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), (ushort)name.Length);
            output.Write(header);
            output.Write(name);
            output.Write(entry.Data);
        }

        var directoryStart = (uint)output.Length;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var name = Encoding.ASCII.GetBytes(entry.Name);
            var record = new byte[46];
            BinaryPrimitives.WriteUInt32LittleEndian(record, 0x02014B50);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 20);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 20);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), Crc32(entry.Data));
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(20), (uint)entry.Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), (uint)entry.Data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(28), (ushort)name.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(42), offsets[i]);
            output.Write(record);
            output.Write(name);
        }

        var directoryLength = (uint)output.Length - directoryStart;
        var end = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054B50);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), (ushort)entries.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), (ushort)entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), directoryLength);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), directoryStart);
        output.Write(end);

        var bytes = output.ToArray();
        PatchCentral(bytes, entries);
        return bytes;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static void PatchCentral(byte[] zip, IReadOnlyList<RawEntry> entries)
    {
        var end = zip.Length - 22;
        Assert.Equal(0x06054B50u, BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end)));
        var at = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end + 16));
        foreach (var entry in entries)
        {
            var record = zip.AsSpan(at);
            Assert.Equal(0x02014B50u, BinaryPrimitives.ReadUInt32LittleEndian(record));
            if (entry.VersionMadeBy is { } made) BinaryPrimitives.WriteUInt16LittleEndian(record[4..], made);
            if (entry.Flags is { } flags) BinaryPrimitives.WriteUInt16LittleEndian(record[8..], flags);
            if (entry.Method is { } method) BinaryPrimitives.WriteUInt16LittleEndian(record[10..], method);
            if (entry.CompressedSize is { } compressed) BinaryPrimitives.WriteUInt32LittleEndian(record[20..], compressed);
            if (entry.UncompressedSize is { } uncompressed) BinaryPrimitives.WriteUInt32LittleEndian(record[24..], uncompressed);
            if (entry.ExternalAttributes is { } attributes) BinaryPrimitives.WriteUInt32LittleEndian(record[38..], attributes);
            if (entry.LocalHeaderOffset is { } offset) BinaryPrimitives.WriteUInt32LittleEndian(record[42..], offset);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(record[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(record[32..]);
            at += 46 + nameLength + extraLength + commentLength;
        }
    }

    /// <summary>
    /// A zip made the way .NET Framework's CreateFromDirectory (Windows PowerShell 5.1) made the v0.2.0 release: the same files, but every entry name
    /// is <c>PCMieru-X.Y.Z-win-x64\name</c> with backslashes (a "ja\..." name for a satellite).
    /// </summary>
    public byte[] FrameworkStyleZip() =>
        Write(Entries().Select(e => new RawEntry(e.Name.Replace('/', '\\'), e.Data)).ToList());
}
