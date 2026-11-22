using PcMieru.Update.Bundle;
using PcMieru.Update.Feed;

namespace PcMieru.BundleCheck;

/// <summary>
/// The real bundle validator as a command line (DESIGN-B 11 step 1). tools/publish.ps1 -Verify runs it, so a release that publish makes is judged by the
/// code the client will use, not by a second implementation in PowerShell.
///
///   BundleCheck zip    &lt;zip&gt; &lt;outer manifest&gt; &lt;version&gt; --floor full|this-build [--sha256 &lt;SHA256.txt&gt;]
///   BundleCheck folder &lt;folder&gt; &lt;version&gt; --floor full|this-build [--outer &lt;outer manifest&gt;]
///   BundleCheck --list &lt;zip&gt;
///
/// --floor is required and has no default: <c>full</c> asks for BundleFloor.Full (the whole designed floor, what a release needs and what the client
/// always asks), <c>this-build</c> asks for BundleFloor.ForThisBuild (Full without the files of the later steps). A default would be a silent choice
/// between "every release passes without its Swap.exe" and "the trial fails for a reason nobody can see", so the caller says which, and a missing or
/// misspelled value is a mistake of the command line.
/// --sha256 also checks that SHA256.txt has one line each for the zip and the outer manifest, and that both match the real files.
/// --list prints the names of the central directory, one per line (LF, ordinal order), and judges nothing but that the zip can be read the way the
/// validator reads it: CI compares the lists of the zips of two PowerShells.
/// Prints "OK: N files, M bytes" and exits 0, or "FAIL &lt;Fault&gt;: &lt;detail&gt;" and exits 1. A mistake in the command line exits 2.
/// It is a class (and not top-level statements) so the tests can run it in-process with their own writers.
/// </summary>
public static class Cli
{
    private const string Usage1 = "usage: BundleCheck zip <zip> <outer manifest> <version> --floor full|this-build [--sha256 <SHA256.txt>]";
    private const string Usage2 = "       BundleCheck folder <folder> <version> --floor full|this-build [--outer <outer manifest>]";
    private const string Usage3 = "       BundleCheck --list <zip>";

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            if (args.Length >= 4 && args[0] == "zip")
            {
                return RunZip(args, output, error);
            }

            if (args.Length >= 3 && args[0] == "folder")
            {
                return RunFolder(args, output, error);
            }

            if (args.Length == 2 && args[0] == "--list")
            {
                return RunList(args[1], output, error);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error.WriteLine("FAIL ReadFailed: " + ex.Message);
            return 1;
        }

        error.WriteLine(Usage1);
        error.WriteLine(Usage2);
        error.WriteLine(Usage3);
        return 2;
    }

    private static int RunList(string zipPath, TextWriter output, TextWriter error)
    {
        using var zip = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        if (!BundleZip.TryListNames(zip, Limits.Default, out var names, out var fault, out var detail))
        {
            return Fail(error, fault, detail);
        }

        // Ordinal order so that the list does not depend on the order the entries were written in, and LF so that it is the same on every platform.
        var sorted = names.ToArray();
        Array.Sort(sorted, StringComparer.Ordinal);
        foreach (var name in sorted)
        {
            output.Write(name);
            output.Write('\n');
        }

        return 0;
    }

    private static int RunZip(string[] args, TextWriter output, TextWriter error)
    {
        string zipPath = args[1], manifestPath = args[2], version = args[3];
        if (!TryOptions(args[4..], allowOuter: false, error, out var floor, out _, out var sha256Path))
        {
            return 2;
        }

        var limits = Limits.Default;
        using var manifestStream = File.OpenRead(manifestPath);
        if (!BundleManifest.TryRead(manifestStream, limits.ManifestBytes, out var outer, out var verdict))
        {
            return Fail(error, verdict.Fault, $"{Path.GetFileName(manifestPath)}: {verdict.Detail}");
        }

        if (sha256Path is not null)
        {
            var listFault = CheckHashList(sha256Path, zipPath, manifestPath, version);
            if (listFault is not null)
            {
                return Fail(error, listFault.Value.Fault, listFault.Value.Detail);
            }
        }

        using var zip = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return Report(output, error, BundleZip.Validate(zip, version, outer, limits, floor));
    }

    private static int RunFolder(string[] args, TextWriter output, TextWriter error)
    {
        string folder = args[1], version = args[2];
        if (!TryOptions(args[3..], allowOuter: true, error, out var floor, out var outerPath, out _))
        {
            return 2;
        }

        var limits = Limits.Default;
        BundleManifest? outer = null;
        if (outerPath is not null)
        {
            using var manifestStream = File.OpenRead(outerPath);
            if (!BundleManifest.TryRead(manifestStream, limits.ManifestBytes, out outer, out var verdict))
            {
                return Fail(error, verdict.Fault, $"{Path.GetFileName(outerPath)}: {verdict.Detail}");
            }
        }

        return Report(output, error, BundleFolder.Validate(folder, version, outer, limits, floor));
    }

    private static bool TryOptions(string[] options, bool allowOuter, TextWriter error, out IReadOnlyCollection<string> floor, out string? outerPath, out string? sha256Path)
    {
        floor = BundleFloor.Full; // never used: a missing --floor returns false below
        outerPath = null;
        sha256Path = null;
        var floorGiven = false;
        for (var i = 0; i < options.Length; i++)
        {
            var name = options[i];
            if (i + 1 >= options.Length)
            {
                error.WriteLine($"the option {name} needs a value");
                return false;
            }

            var value = options[++i];
            switch (name)
            {
                case "--floor" when value == "this-build":
                    floor = BundleFloor.ForThisBuild;
                    floorGiven = true;
                    break;
                case "--floor" when value == "full":
                    floor = BundleFloor.Full;
                    floorGiven = true;
                    break;
                case "--floor":
                    error.WriteLine($"--floor must be 'full' or 'this-build', not '{value}'");
                    return false;
                case "--outer" when allowOuter:
                    outerPath = value;
                    break;
                case "--sha256" when !allowOuter:
                    sha256Path = value;
                    break;
                default:
                    error.WriteLine($"unknown option {name} {value}");
                    return false;
            }
        }

        if (!floorGiven)
        {
            error.WriteLine("--floor full|this-build is required (there is no default)");
            return false;
        }

        return true;
    }

    // SHA256.txt must say, once each, the hash of the zip and of the outer manifest, and the hashes must be the real ones.
    private static (BundleFault Fault, string Detail)? CheckHashList(string listPath, string zipPath, string manifestPath, string version)
    {
        var info = new FileInfo(listPath);
        if (info.Length > Sha256List.MaxBytes)
        {
            return (BundleFault.ReadFailed, $"{BundleFormat.HashListName} is longer than {Sha256List.MaxBytes} bytes");
        }

        var list = Sha256List.Parse(File.ReadAllText(listPath));
        var wanted = new[] { (BundleFormat.ZipName(version), zipPath), (BundleFormat.OuterManifestName(version), manifestPath) };
        foreach (var (name, path) in wanted)
        {
            var listed = list.For(name);
            if (listed is null)
            {
                return (BundleFault.HashMismatch, $"{BundleFormat.HashListName} has no single line for '{name}'");
            }

            if (!Sha256List.Same(listed, Sha256List.HashFile(path)))
            {
                return (BundleFault.HashMismatch, $"{BundleFormat.HashListName}: the hash of '{name}' is not the real one");
            }
        }

        if (list.Names.Count != wanted.Length)
        {
            return (BundleFault.HashMismatch, $"{BundleFormat.HashListName} has {list.Names.Count} names, not {wanted.Length}");
        }

        return null;
    }

    private static int Report(TextWriter output, TextWriter error, BundleResult result)
    {
        if (result.Ok)
        {
            output.WriteLine($"OK: {result.FileCount} files, {result.TotalBytes} bytes");
            return 0;
        }

        return Fail(error, result.Fault, result.Detail);
    }

    private static int Fail(TextWriter error, BundleFault fault, string detail)
    {
        error.WriteLine($"FAIL {fault}: {detail}");
        return 1;
    }
}
