using System.Diagnostics;
using System.Text;
using PcMieru.Update.Bundle;

namespace PcMieru.Update.Tests;

/// <summary>
/// The folder validator (the one tools/publish.ps1 -SkipZip -Verify runs): the same rules as the zip, applied to real files and judged against the
/// inner PCMieru.bundle.json. The folders are made under the temp folder and deleted afterwards.
/// </summary>
public class BundleFolderTests
{
    /// <summary>A folder on disk with the files of a <see cref="TestBundle"/> and its inner manifest.</summary>
    private sealed class Disk : IDisposable
    {
        public Disk(TestBundle bundle, bool withInner = true)
        {
            Bundle = bundle;
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcmieru-update-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            foreach (var (name, data) in bundle.Files)
            {
                Write(name, data);
            }

            if (withInner)
            {
                Write(BundleFormat.InnerManifestName, bundle.InnerBytes());
            }
        }

        public TestBundle Bundle { get; }

        public string Path { get; }

        public void Write(string relative, byte[] data)
        {
            var full = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, data);
        }

        public void Delete(string relative) => File.Delete(System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));

        public BundleResult Validate(BundleManifest? outer = null, Limits? limits = null, string? version = null, IReadOnlyCollection<string>? floor = null) =>
            BundleFolder.Validate(Path, version ?? Bundle.Version, outer, limits ?? Limits.Default, floor ?? BundleFloor.ForThisBuild);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A scratch folder in the temp folder: a leftover is harmless.
            }
        }
    }

    private static BundleResult Refused(BundleResult result, BundleFault expected)
    {
        Assert.False(result.Ok, "the folder should be refused");
        Assert.Equal(expected, result.Fault);
        Assert.False(string.IsNullOrEmpty(result.Detail));
        return result;
    }

    [Fact]
    public void A_good_folder_passes_against_its_own_inner_manifest()
    {
        var bundle = new TestBundle();
        using var disk = new Disk(bundle);
        var result = disk.Validate();
        Assert.True(result.Ok, result.ToString());
        Assert.Equal(bundle.Files.Count + 1, result.FileCount);
        Assert.Equal(bundle.Files.Values.Sum(v => (long)v.Length) + bundle.InnerBytes().Length, result.TotalBytes);
    }

    [Fact]
    public void A_good_folder_passes_against_the_outer_manifest_too()
    {
        var bundle = new TestBundle();
        using var disk = new Disk(bundle);
        var result = disk.Validate(bundle.Outer());
        Assert.True(result.Ok, result.ToString());
    }

    [Fact]
    public void The_default_floor_is_the_full_floor()
    {
        var bundle = new TestBundle();
        using var disk = new Disk(bundle);
        var result = BundleFolder.Validate(disk.Path, bundle.Version, null, Limits.Default);
        Assert.Equal(BundleFault.FloorMissing, result.Fault);
    }

    [Fact]
    public void A_folder_that_does_not_exist_cannot_be_read()
    {
        var path = Path.Combine(Path.GetTempPath(), "pcmieru-update-tests-missing-" + Guid.NewGuid().ToString("N"));
        Refused(BundleFolder.Validate(path, "0.5.0", null, Limits.Default), BundleFault.ReadFailed);
        Assert.Throws<ArgumentException>(() => BundleFolder.Validate(string.Empty, "0.5.0", null, Limits.Default));
    }

    [Fact]
    public void The_version_asked_for_must_be_plain_and_must_be_the_manifests()
    {
        var bundle = new TestBundle();
        using var disk = new Disk(bundle);
        Refused(disk.Validate(version: "v0.5.0"), BundleFault.BadVersion);
        Refused(disk.Validate(version: "0.5.1"), BundleFault.VersionMismatch);
    }

    [Fact]
    public void A_file_with_other_content_is_refused_on_its_hash()
    {
        var bundle = new TestBundle();
        using var disk = new Disk(bundle);
        var changed = (byte[])bundle.Files["System.Runtime.dll"].Clone();
        changed[7] ^= 0x01;
        disk.Write("System.Runtime.dll", changed);
        var result = Refused(disk.Validate(), BundleFault.HashMismatch);
        Assert.Contains("System.Runtime.dll", result.Detail);
    }

    [Fact]
    public void A_file_of_another_length_is_refused_on_its_length()
    {
        var bundle = new TestBundle();
        using var disk = new Disk(bundle);
        disk.Write("System.Runtime.dll", bundle.Files["System.Runtime.dll"][..^1]);
        Refused(disk.Validate(), BundleFault.LengthMismatch);
    }

    [Fact]
    public void A_file_the_manifest_does_not_list_is_refused()
    {
        var bundle = new TestBundle();
        using var disk = new Disk(bundle);
        disk.Write("extra.dll", [1, 2, 3]);
        var result = Refused(disk.Validate(), BundleFault.FileSetMismatch);
        Assert.Contains("extra.dll", result.Detail);
    }

    [Fact]
    public void A_file_the_manifest_lists_and_the_folder_lacks_is_refused()
    {
        var bundle = new TestBundle();
        using var disk = new Disk(bundle);
        disk.Delete("System.Runtime.dll");
        var result = Refused(disk.Validate(), BundleFault.FileSetMismatch);
        Assert.Contains("System.Runtime.dll", result.Detail);
    }

    [Fact]
    public void A_missing_floor_file_is_refused_before_anything_else()
    {
        var bundle = new TestBundle();
        using var disk = new Disk(bundle);
        disk.Delete("PcMieru.Core.dll");
        var result = Refused(disk.Validate(), BundleFault.FloorMissing);
        Assert.Contains("PcMieru.Core.dll", result.Detail);
    }

    [Fact]
    public void The_inner_manifest_is_required()
    {
        var bundle = new TestBundle();
        using var disk = new Disk(bundle, withInner: false);
        Refused(disk.Validate(), BundleFault.InnerManifestMissing);
    }

    [Fact]
    public void The_inner_manifest_of_another_version_is_refused()
    {
        var bundle = new TestBundle("0.5.1");
        using var disk = new Disk(bundle);

        // The same files, the manifest of another version.
        var files = bundle.Files.Select(f => TestBundle.Line(f.Key, f.Value));
        disk.Write(BundleFormat.InnerManifestName, new BundleManifest("0.5.0", bundle.BuildId, files).ToUtf8Bytes());
        var result = Refused(disk.Validate(), BundleFault.VersionMismatch);
        Assert.Contains("0.5.0", result.Detail);
    }

    [Fact]
    public void An_inner_manifest_of_other_files_is_refused()
    {
        var bundle = new TestBundle();
        using var disk = new Disk(bundle);
        var other = new TestBundle();
        other.Files["more.dll"] = [1, 2, 3];
        disk.Write(BundleFormat.InnerManifestName, other.InnerBytes());
        Refused(disk.Validate(), BundleFault.FileSetMismatch);
    }

    [Fact]
    public void An_inner_manifest_the_strict_reader_refuses_is_refused()
    {
        var bundle = new TestBundle();
        using var disk = new Disk(bundle);
        disk.Write(BundleFormat.InnerManifestName, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble().Concat(bundle.InnerBytes()).ToArray());
        Refused(disk.Validate(), BundleFault.ManifestBom);

        disk.Write(BundleFormat.InnerManifestName, Encoding.ASCII.GetBytes("nope"));
        Refused(disk.Validate(), BundleFault.ManifestSyntax);
    }

    [Fact]
    public void An_outer_manifest_must_agree_with_the_folder()
    {
        var bundle = new TestBundle();
        using var disk = new Disk(bundle);
        var otherBuild = new BundleManifest(bundle.Version, "ffffffff-ffff-4fff-8fff-ffffffffffff", bundle.Outer().Files);
        Refused(disk.Validate(otherBuild), BundleFault.OuterInnerMismatch);

        var otherVersion = new BundleManifest("0.5.1", bundle.BuildId, bundle.Outer().Files);
        Refused(disk.Validate(otherVersion), BundleFault.VersionMismatch);

        var fewer = new BundleManifest(bundle.Version, bundle.BuildId, bundle.Outer().Files.Where(f => f.Path != "NOTICE.txt"));
        Refused(disk.Validate(fewer), BundleFault.FileSetMismatch);
    }

    // (The names that Windows itself cannot create, a device name or a trailing dot, cannot be in a folder; the zip tests cover those.)
    [Theory]
    [InlineData("x.bin", BundleFault.NotAllowed)]
    [InlineData("Setup.exe", BundleFault.NotAllowed)]
    [InlineData("PCMIERU.DLL", BundleFault.NotAllowed)]
    [InlineData("ja/x.dll", BundleFault.NotAllowed)]
    [InlineData("en/PcMieru.Core.txt", BundleFault.NotAllowed)]
    public void A_file_name_that_breaks_a_rule_is_refused(string name, BundleFault expected)
    {
        var bundle = new TestBundle();
        using var disk = new Disk(bundle);
        disk.Write(name, [1]);
        var result = Refused(disk.Validate(), expected);
        Assert.Contains(name.Split('/')[^1], result.Detail);
    }

    [Fact]
    public void A_folder_other_than_ja_and_en_is_refused()
    {
        var bundle = new TestBundle();
        using var disk = new Disk(bundle);
        disk.Write("fr/PcMieru.Core.resources.dll", [1]);
        Refused(disk.Validate(), BundleFault.DirectoryNotAllowed);

        disk.Write("Sub/x.dll", [1]);
        Refused(disk.Validate(), BundleFault.DirectoryNotAllowed);
    }

    [Fact]
    public void A_satellite_folder_spelled_with_other_letters_is_refused()
    {
        var bundle = new TestBundle();
        bundle.Files.Remove("ja/PcMieru.Core.resources.dll");
        using var disk = new Disk(bundle);
        disk.Write("JA/PcMieru.Core.resources.dll", [1]); // there is no "ja" folder, so Windows keeps this spelling
        Refused(disk.Validate(), BundleFault.DirectoryNotAllowed);
    }

    [Fact]
    public void A_folder_below_ja_is_refused()
    {
        var bundle = new TestBundle();
        using var disk = new Disk(bundle);
        disk.Write("ja/sub/x.resources.dll", [1]);
        Refused(disk.Validate(), BundleFault.DirectoryNotAllowed);
    }

    [Fact]
    public void An_empty_satellite_folder_is_refused()
    {
        var bundle = new TestBundle();
        bundle.Files.Remove("ja/PcMieru.Core.resources.dll");
        using var disk = new Disk(bundle);
        Directory.CreateDirectory(Path.Combine(disk.Path, "ja"));
        Refused(disk.Validate(), BundleFault.EmptyDirectory);
    }

    [Fact]
    public void A_junction_is_a_link_and_is_never_followed()
    {
        var bundle = new TestBundle();
        bundle.Files.Remove("ja/PcMieru.Core.resources.dll");
        using var disk = new Disk(bundle);
        using var target = new Disk(new TestBundle());
        var junction = Path.Combine(disk.Path, "ja");

        var info = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target.Path}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using (var process = Process.Start(info)!)
        {
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }

        try
        {
            var result = Refused(disk.Validate(), BundleFault.Link);
            Assert.Contains("ja", result.Detail);
        }
        finally
        {
            // Remove the junction itself first, so that deleting the folder never goes through it into the target.
            Directory.Delete(junction, recursive: false);
        }
    }

    [Fact]
    public void The_file_count_and_total_limits_hold_for_a_folder_too()
    {
        var bundle = new TestBundle();
        using var disk = new Disk(bundle);
        var count = bundle.Files.Count + 1;
        var total = bundle.Files.Values.Sum(v => (long)v.Length) + bundle.InnerBytes().Length;
        Assert.True(disk.Validate(limits: new Limits { Files = count, TotalBytes = total }).Ok);
        Refused(disk.Validate(limits: new Limits { Files = count - 1 }), BundleFault.TooManyFiles);
        Refused(disk.Validate(limits: new Limits { TotalBytes = total - 1 }), BundleFault.TotalTooLarge);
        Refused(disk.Validate(limits: new Limits { ManifestBytes = bundle.InnerBytes().Length - 1 }), BundleFault.ManifestTooLarge);
    }
}
