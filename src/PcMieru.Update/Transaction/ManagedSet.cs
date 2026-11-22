using System.Security.Cryptography;
using PcMieru.Update.Bundle;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Transaction;

/// <summary>What is at a path.</summary>
internal enum Presence
{
    /// <summary>Nothing.</summary>
    Absent,

    /// <summary>An ordinary file (its length and SHA-256 are given).</summary>
    File,

    /// <summary>A link (a reparse point), never followed.</summary>
    Link,

    /// <summary>A folder, or anything else that is not a file.</summary>
    Other,
}

internal enum FindingKind
{
    Missing,
    Link,
    NotAFile,
    Length,
    Hash,
}

/// <summary>One difference between what a record says and what is on the disk.</summary>
internal readonly record struct SetFinding(string Path, FindingKind Kind)
{
    public override string ToString() => $"{BundleNames.Show(Path)} ({Kind})";
}

/// <summary>
/// "Is the managed set what the record says": every file of an <see cref="InstalledRecord"/> by length and SHA-256 of its REAL bytes. Nothing in the journal (a counter, a state) is looked at: the disk is the
/// witness. A missing file, a folder or a link in place of a file, another length, another hash are all differences. The gate file is read through the HELD handle (<see cref="GateEntry.ReadHeld"/>) because the
/// swapper holds it exclusively and no second open by path can work.
/// </summary>
internal static class ManagedSet
{
    /// <summary>What is at <paramref name="path"/>, without reading it (a file is <see cref="Presence.File"/>; its bytes are not hashed).</summary>
    public static Presence Look(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Presence.Absent;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return Presence.Link;
        }

        return attributes.HasFlag(FileAttributes.Directory) ? Presence.Other : Presence.File;
    }

    /// <summary>Whether anything (a file, a folder, a link, even a broken one) is at <paramref name="path"/>.</summary>
    public static bool Exists(string path) => Look(path) != Presence.Absent;

    /// <summary>What is at <paramref name="path"/>; for a file, its length and SHA-256. Throws <see cref="IOException"/> when the file cannot be read (a lock).</summary>
    public static Presence Probe(string path, out long length, out string? sha256)
    {
        length = 0;
        sha256 = null;
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Presence.Absent;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return Presence.Link;
        }

        if (attributes.HasFlag(FileAttributes.Directory))
        {
            return Presence.Other;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        length = stream.Length;
        sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        return Presence.File;
    }

    /// <summary>The difference between <paramref name="file"/> and the disk, or null when it is as recorded.</summary>
    public static SetFinding? CheckOne(TxPaths paths, InstalledFile file, GateEntry? gate)
    {
        if (gate is { IsOpen: true } && GateFile.IsExcludedFromSwap(file.Path))
        {
            var held = gate.ReadHeld();
            if (held is not null)
            {
                return held.Length == file.Bytes && string.Equals(Convert.ToHexStringLower(SHA256.HashData(held)), file.Sha256, StringComparison.Ordinal)
                    ? null
                    : new SetFinding(file.Path, FindingKind.Hash);
            }
        }

        // A link in ANY folder between the install folder and the file (ja/, en/ and whatever lies below them) is also a difference: the file would be somewhere else. Every segment is looked at, not only the last
        // one; a link at "ja" with a real folder below it is just as much a link.
        var segments = file.Path.Split('/');
        var folder = paths.Install;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            folder = Path.Combine(folder, segments[i]);
            if (TxPaths.IsLink(folder))
            {
                return new SetFinding(file.Path, FindingKind.Link);
            }
        }

        switch (Probe(paths.InInstall(file.Path), out var length, out var sha))
        {
            case Presence.Absent:
                return new SetFinding(file.Path, FindingKind.Missing);
            case Presence.Link:
                return new SetFinding(file.Path, FindingKind.Link);
            case Presence.Other:
                return new SetFinding(file.Path, FindingKind.NotAFile);
        }

        if (length != file.Bytes)
        {
            return new SetFinding(file.Path, FindingKind.Length);
        }

        return string.Equals(sha, file.Sha256, StringComparison.Ordinal) ? null : new SetFinding(file.Path, FindingKind.Hash);
    }

    /// <summary>All differences between the record and the disk (empty when the whole set is as recorded).</summary>
    public static List<SetFinding> Verify(TxPaths paths, InstalledRecord record, GateEntry? gate)
    {
        var findings = new List<SetFinding>();
        foreach (var file in record.Files)
        {
            var finding = CheckOne(paths, file, gate);
            if (finding is not null)
            {
                findings.Add(finding.Value);
            }
        }

        return findings;
    }

    /// <summary>A short text of the first differences (for a result's detail).</summary>
    public static string Describe(IReadOnlyList<SetFinding> findings)
    {
        var shown = string.Join(", ", findings.Take(3).Select(f => f.ToString()));
        return findings.Count > 3 ? $"{shown} and {findings.Count - 3} more" : shown;
    }
}
