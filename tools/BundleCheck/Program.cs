using PcMieru.Update.Bundle;
using PcMieru.Update.Feed;

// The real bundle validator as a command line (DESIGN-B 11 step 1). tools/publish.ps1 -Verify runs it, so a release that publish makes is judged by the
// code the client will use, not by a second implementation in PowerShell.
//
//   BundleCheck zip    <zip> <outer manifest> <version> [--floor build|full] [--sha256 <SHA256.txt>]
//   BundleCheck folder <folder> <version> [--outer <outer manifest>] [--floor build|full]
//
// --floor build (the default) asks for BundleFloor.ForThisBuild, what this build's publish can meet; --floor full asks for the whole designed floor.
// --sha256 also checks that SHA256.txt has one line each for the zip and the outer manifest, and that both match the real files.
// Prints "OK: N files, M bytes" and exits 0, or "FAIL <Fault>: <detail>" and exits 1. A mistake in the command line exits 2.
return Run(args);

static int Run(string[] args)
{
    try
    {
        if (args.Length >= 4 && args[0] == "zip")
        {
            return RunZip(args);
        }

        if (args.Length >= 3 && args[0] == "folder")
        {
            return RunFolder(args);
        }
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
    {
        Console.Error.WriteLine("FAIL ReadFailed: " + ex.Message);
        return 1;
    }

    Console.Error.WriteLine("usage: BundleCheck zip <zip> <outer manifest> <version> [--floor build|full] [--sha256 <SHA256.txt>]");
    Console.Error.WriteLine("       BundleCheck folder <folder> <version> [--outer <outer manifest>] [--floor build|full]");
    return 2;
}

static int RunZip(string[] args)
{
    string zipPath = args[1], manifestPath = args[2], version = args[3];
    if (!TryOptions(args[4..], allowOuter: false, out var floor, out _, out var sha256Path))
    {
        return 2;
    }

    var limits = Limits.Default;
    using var manifestStream = File.OpenRead(manifestPath);
    if (!BundleManifest.TryRead(manifestStream, limits.ManifestBytes, out var outer, out var verdict))
    {
        return Fail(verdict.Fault, $"{Path.GetFileName(manifestPath)}: {verdict.Detail}");
    }

    if (sha256Path is not null)
    {
        var listFault = CheckHashList(sha256Path, zipPath, manifestPath, version);
        if (listFault is not null)
        {
            return Fail(listFault.Value.Fault, listFault.Value.Detail);
        }
    }

    using var zip = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
    return Report(BundleZip.Validate(zip, version, outer, limits, floor));
}

static int RunFolder(string[] args)
{
    string folder = args[1], version = args[2];
    if (!TryOptions(args[3..], allowOuter: true, out var floor, out var outerPath, out _))
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
            return Fail(verdict.Fault, $"{Path.GetFileName(outerPath)}: {verdict.Detail}");
        }
    }

    return Report(BundleFolder.Validate(folder, version, outer, limits, floor));
}

static bool TryOptions(string[] options, bool allowOuter, out IReadOnlyCollection<string> floor, out string? outerPath, out string? sha256Path)
{
    floor = BundleFloor.ForThisBuild;
    outerPath = null;
    sha256Path = null;
    for (var i = 0; i < options.Length; i++)
    {
        var name = options[i];
        if (i + 1 >= options.Length)
        {
            Console.Error.WriteLine($"the option {name} needs a value");
            return false;
        }

        var value = options[++i];
        switch (name)
        {
            case "--floor" when value == "build":
                floor = BundleFloor.ForThisBuild;
                break;
            case "--floor" when value == "full":
                floor = BundleFloor.Full;
                break;
            case "--outer" when allowOuter:
                outerPath = value;
                break;
            case "--sha256" when !allowOuter:
                sha256Path = value;
                break;
            default:
                Console.Error.WriteLine($"unknown option {name} {value}");
                return false;
        }
    }

    return true;
}

// SHA256.txt must say, once each, the hash of the zip and of the outer manifest, and the hashes must be the real ones.
static (BundleFault Fault, string Detail)? CheckHashList(string listPath, string zipPath, string manifestPath, string version)
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

static int Report(BundleResult result)
{
    if (result.Ok)
    {
        Console.WriteLine($"OK: {result.FileCount} files, {result.TotalBytes} bytes");
        return 0;
    }

    return Fail(result.Fault, result.Detail);
}

static int Fail(BundleFault fault, string detail)
{
    Console.Error.WriteLine($"FAIL {fault}: {detail}");
    return 1;
}
