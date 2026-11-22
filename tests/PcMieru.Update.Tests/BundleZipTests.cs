using System.Buffers.Binary;
using System.Text;
using PcMieru.Update.Bundle;

namespace PcMieru.Update.Tests;

/// <summary>
/// The zip validator, one rule at a time: DESIGN-B 3.2 list items 1 to 4 and the checks of section 11 (the zip and the publish). Each test builds a small
/// valid release in memory (<see cref="TestBundle"/>), breaks one thing and says which <see cref="BundleFault"/> must follow.
/// </summary>
public class BundleZipTests
{
    private static RawEntry Named(TestBundle bundle, string name, byte[]? data = null) =>
        new(name.Replace("{root}", bundle.Root), data ?? new byte[] { 1 });

    private static RawEntry Find(List<RawEntry> entries, string endsWith) => entries.Single(e => e.Name.EndsWith(endsWith, StringComparison.Ordinal));

    private static BundleResult Refused(TestBundle bundle, byte[] zip, BundleFault expected, BundleManifest? outer = null, Limits? limits = null)
    {
        var result = bundle.Validate(zip, outer, limits);
        Assert.False(result.Ok, "the zip should be refused");
        Assert.Equal(expected, result.Fault);
        Assert.False(string.IsNullOrEmpty(result.Detail));
        return result;
    }

    // ----------------------------------------------------------------------------------------------------------------------------- good

    [Fact]
    public void A_good_bundle_passes_and_says_what_it_read()
    {
        var bundle = new TestBundle();
        var result = bundle.Validate(bundle.Zip());
        Assert.True(result.Ok, result.ToString());
        Assert.Equal(bundle.Files.Count + 1, result.FileCount);
        Assert.Equal(bundle.Files.Values.Sum(v => (long)v.Length) + bundle.InnerBytes().Length, result.TotalBytes);
    }

    [Fact]
    public void The_order_of_the_entries_does_not_matter()
    {
        var bundle = new TestBundle();
        Assert.True(bundle.Validate(bundle.Zip(e => e.Reverse())).Ok);
    }

    [Fact]
    public void Entries_that_are_stored_and_not_deflated_are_read_too()
    {
        var bundle = new TestBundle();
        var zip = TestBundle.WriteStored(bundle.Entries());
        var result = bundle.Validate(zip);
        Assert.True(result.Ok, result.ToString());
        Assert.Equal(bundle.Files.Count + 1, result.FileCount);
    }

    [Fact]
    public void The_root_folder_and_the_satellite_folders_may_have_entries_of_their_own()
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip(e => e.InsertRange(0, [Named(bundle, "{root}/", []), Named(bundle, "{root}/en/", []), Named(bundle, "{root}/ja/", [])]));
        var result = bundle.Validate(zip);
        Assert.True(result.Ok, result.ToString());
        Assert.Equal(bundle.Files.Count + 1, result.FileCount);
    }

    [Fact]
    public void A_zip_made_on_unix_with_plain_file_and_directory_modes_passes()
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip(e =>
        {
            foreach (var entry in e)
            {
                entry.VersionMadeBy = 0x031E; // host 3 (Unix), version 3.0
                entry.ExternalAttributes = 0x81A4u << 16; // -rw-r--r--
            }

            e.Insert(0, new RawEntry(bundle.Root + "/ja/", []) { VersionMadeBy = 0x031E, ExternalAttributes = (0x41EDu << 16) | 0x10 });
        });
        var result = bundle.Validate(zip);
        Assert.True(result.Ok, result.ToString());
    }

    [Fact]
    public void A_zip_comment_is_refused()
    {
        // A comment is the one place where a second end record can hide, and publish never writes one: the end record must be the last 22 bytes of the file.
        var bundle = new TestBundle();
        var zip = bundle.Zip();
        var withComment = zip.Concat(Encoding.ASCII.GetBytes("hello")).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(withComment.AsSpan(zip.Length - 2), 5);
        var result = Refused(bundle, withComment, BundleFault.NotZip);
        Assert.Contains("comment", result.Detail);
    }

    [Fact]
    public void An_end_record_that_says_it_has_a_comment_it_does_not_have_is_refused()
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip();
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(zip.Length - 2), 5);
        Refused(bundle, zip, BundleFault.NotZip);
    }

    // ------------------------------------------------------------------------------------------------------------------------ the floor

    [Fact]
    public void The_client_default_is_the_full_floor_and_a_publish_of_this_build_does_not_meet_it()
    {
        var bundle = new TestBundle();
        using var stream = new MemoryStream(bundle.Zip());
        var result = BundleZip.Validate(stream, bundle.Version, bundle.Outer(), Limits.Default);
        Assert.Equal(BundleFault.FloorMissing, result.Fault);
        Assert.Contains("PCMieru.Swap.exe", result.Detail);
    }

    // ------------------------------------------------------------------------------------------------------------------------ the gate file (DESIGN-B 4)

    [Fact]
    public void A_changed_gate_file_is_refused_even_when_the_manifests_agree_with_it()
    {
        // The gate file's bytes are fixed in the client (GateFile.Sha256Hex): a bundle whose manifests list a different file is not a bundle of this client.
        var bundle = new TestBundle();
        bundle.Files[BundleFormat.GateFileName] = Encoding.UTF8.GetBytes(PcMieru.Wire.Gate.GateFile.Text + "x");
        var result = Refused(bundle, bundle.Zip(), BundleFault.GateFileChanged);
        Assert.Contains(BundleFormat.GateFileName, result.Detail);
    }

    [Fact]
    public void A_gate_file_with_the_same_text_but_other_line_endings_is_refused()
    {
        var bundle = new TestBundle();
        bundle.Files[BundleFormat.GateFileName] = Encoding.UTF8.GetBytes(PcMieru.Wire.Gate.GateFile.Text.Replace("\n", "\r\n"));
        Refused(bundle, bundle.Zip(), BundleFault.GateFileChanged);
    }

    [Fact]
    public void The_gate_file_with_its_fixed_bytes_passes()
    {
        var bundle = new TestBundle();
        Assert.Equal(PcMieru.Wire.Gate.GateFile.Sha256Hex, TestBundle.Sha(bundle.Files[BundleFormat.GateFileName]));
        var result = bundle.Validate(bundle.Zip());
        Assert.True(result.Ok, result.ToString());
    }

    [Fact]
    public void A_bundle_with_the_whole_full_floor_passes_the_default_floor()
    {
        var bundle = new TestBundle();
        foreach (var name in BundleFloor.NotYetBuilt)
        {
            bundle.Files[name] = Encoding.ASCII.GetBytes("later " + name);
        }

        using var stream = new MemoryStream(bundle.Zip());
        var result = BundleZip.Validate(stream, bundle.Version, bundle.Outer(), Limits.Default);
        Assert.True(result.Ok, result.ToString());
    }

    [Theory]
    [InlineData("PcMieru.Core.dll")]
    [InlineData("pcmi.exe")]
    [InlineData("PCMieru.deps.json")]
    [InlineData("PcMieru.Checks.dll")]
    public void A_missing_floor_file_is_named(string name)
    {
        var bundle = new TestBundle();
        bundle.Files.Remove(name); // the manifests agree with the zip, so only the floor is at fault
        var result = Refused(bundle, bundle.Zip(), BundleFault.FloorMissing);
        Assert.Contains(name, result.Detail);
    }

    [Fact]
    public void The_floor_is_compared_without_regard_to_case()
    {
        var bundle = new TestBundle();
        var result = bundle.Validate(bundle.Zip(), floor: ["PCMIERU.EXE", "pcmieru.core.DLL"]);
        Assert.True(result.Ok, result.ToString());
    }

    [Fact]
    public void Another_floor_can_be_asked_for()
    {
        var bundle = new TestBundle();
        var result = bundle.Validate(bundle.Zip(), floor: ["NOTICE.txt", "not-there.dll"]);
        Assert.Equal(BundleFault.FloorMissing, result.Fault);
        Assert.Contains("not-there.dll", result.Detail);
    }

    [Fact]
    public void The_inner_manifest_is_judged_apart_from_the_floor()
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip(e => e.RemoveAll(x => x.Name.EndsWith("/" + BundleFormat.InnerManifestName, StringComparison.Ordinal)));
        Refused(bundle, zip, BundleFault.InnerManifestMissing);

        // The same name in another case is another name: the inner manifest is still missing.
        var lower = bundle.Zip(e => Find(e, "/" + BundleFormat.InnerManifestName).Name = bundle.Root + "/pcmieru.bundle.json");
        Refused(bundle, lower, BundleFault.InnerManifestMissing);
    }

    // ------------------------------------------------------------------------------------------------------------------------- the names

    [Theory]
    [InlineData("{root}/a\\b.dll", BundleFault.Backslash)]
    [InlineData("{root}\\x.dll", BundleFault.Backslash)]
    [InlineData("/{root}/x.dll", BundleFault.LeadingSlash)]
    [InlineData("C:/x.dll", BundleFault.DriveName)]
    [InlineData("{root}/c:x.dll", BundleFault.Colon)]
    [InlineData("{root}/x.dll:stream", BundleFault.Colon)]
    [InlineData("{root}/x.dll::$DATA", BundleFault.Colon)]
    [InlineData("{root}/../x.dll", BundleFault.DotSegment)]
    [InlineData("{root}/./x.dll", BundleFault.DotSegment)]
    [InlineData("../x.dll", BundleFault.DotSegment)]
    [InlineData("{root}//x.dll", BundleFault.EmptySegment)]
    [InlineData("{root}/x\u0001.dll", BundleFault.ControlCharacter)]
    [InlineData("{root}/x.dll.", BundleFault.TrailingDotOrSpace)]
    [InlineData("{root}/x.dll ", BundleFault.TrailingDotOrSpace)]
    [InlineData("{root}/NUL.dll", BundleFault.DeviceName)]
    [InlineData("{root}/con.txt", BundleFault.DeviceName)]
    [InlineData("{root}/ja/COM1.resources.dll", BundleFault.DeviceName)]
    [InlineData("{root}/x.bin", BundleFault.NotAllowed)]
    [InlineData("{root}/x.exe", BundleFault.NotAllowed)]
    [InlineData("{root}/sub/x.dll", BundleFault.NotAllowed)]
    [InlineData("{root}/fr/x.resources.dll", BundleFault.NotAllowed)]
    [InlineData("{root}/ja/x.dll", BundleFault.NotAllowed)]
    [InlineData("{root}/\u00e9.dll", BundleFault.NotAllowed)]
    public void An_entry_name_that_breaks_a_rule_is_refused_with_that_rule(string name, BundleFault expected)
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip(e => e.Add(Named(bundle, name)));
        Refused(bundle, zip, expected);
    }

    [Fact]
    public void A_name_that_is_not_ascii_is_refused()
    {
        var bundle = new TestBundle();
        var result = bundle.Validate(bundle.Zip(e => e.Add(Named(bundle, "{root}/\u65e5\u672c.dll"))));
        Assert.False(result.Ok);
    }

    [Fact]
    public void A_refusal_does_not_print_a_control_character()
    {
        var bundle = new TestBundle();
        var result = bundle.Validate(bundle.Zip(e => e.Add(Named(bundle, "{root}/x\u0001\u001b[2J.dll"))));
        Assert.False(result.Ok);
        Assert.DoesNotContain(result.Detail, c => char.IsControl(c));
    }

    [Fact]
    public void The_zip_that_net_framework_made_for_v020_is_refused_for_its_backslashes()
    {
        // CreateFromDirectory of .NET Framework (Windows PowerShell 5.1) wrote "PCMieru-X.Y.Z-win-x64\name" and "...\ja\name".
        var bundle = new TestBundle();
        var result = Refused(bundle, bundle.FrameworkStyleZip(), BundleFault.Backslash);
        Assert.Contains("PCMieru-0.5.0-win-x64", result.Detail);

        // The same entries with "/" are the good bundle.
        Assert.True(bundle.Validate(bundle.Zip()).Ok);
    }

    [Fact]
    public void Only_the_backslash_separates_the_good_zip_from_the_framework_zip()
    {
        // A zip with a single entry whose name has the backslash in the middle of the name is refused even when the rest is a perfect bundle.
        var bundle = new TestBundle();
        var zip = bundle.Zip(e => Find(e, "/NOTICE.txt").Name = bundle.Root + "\\NOTICE.txt");
        Refused(bundle, zip, BundleFault.Backslash);
    }

    // ---------------------------------------------------------------------------------------------------------------------- the place

    [Fact]
    public void A_file_at_the_top_of_the_zip_is_outside_the_root()
    {
        var bundle = new TestBundle();
        Refused(bundle, bundle.Zip(e => e.Add(new RawEntry("stray.dll", [1]))), BundleFault.OutsideRoot);
    }

    [Theory]
    [InlineData("PCMieru-0.4.0-win-x64/pcmi.exe")]
    [InlineData("PCMieru-0.5.0-win-x64-extra/x.dll")]
    [InlineData("pcmieru-0.5.0-win-x64/x.dll")]
    [InlineData("other/x.dll")]
    [InlineData("PCMieru-0.5.0-win-x64-extra/")]
    public void An_entry_in_another_folder_is_not_in_the_root_folder(string name)
    {
        var bundle = new TestBundle();
        Refused(bundle, bundle.Zip(e => e.Add(new RawEntry(name, name.EndsWith('/') ? Array.Empty<byte>() : new byte[] { 1 }))), BundleFault.WrongRoot);
    }

    [Fact]
    public void A_zip_for_another_version_is_not_the_zip_asked_for()
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip();
        var result = bundle.Validate(zip, version: "0.5.1");
        Assert.Equal(BundleFault.WrongRoot, result.Fault);
    }

    [Theory]
    [InlineData("v0.5.0")]
    [InlineData("0.5")]
    [InlineData("0.5.0-beta")]
    [InlineData("0.5.0+x")]
    [InlineData("05.0.0")]
    [InlineData("")]
    [InlineData("0.5.0 ")]
    public void The_version_asked_for_must_be_a_plain_x_y_z(string version)
    {
        var bundle = new TestBundle();
        var result = bundle.Validate(bundle.Zip(), version: version);
        Assert.Equal(BundleFault.BadVersion, result.Fault);
    }

    [Fact]
    public void The_outer_manifest_must_be_for_the_version_asked_for()
    {
        var bundle = new TestBundle();
        var other = new BundleManifest("0.5.1", bundle.BuildId, bundle.Outer().Files);
        Refused(bundle, bundle.Zip(), BundleFault.VersionMismatch, other);
    }

    // ------------------------------------------------------------------------------------------------------------------ directories

    [Theory]
    [InlineData("{root}/fr/")]
    [InlineData("{root}/sub/")]
    [InlineData("{root}/ja/x/")]
    [InlineData("{root}/x.dll/")]
    [InlineData("{root}/JA/")]
    public void A_directory_entry_other_than_the_root_ja_and_en_is_refused(string name)
    {
        var bundle = new TestBundle();
        Refused(bundle, bundle.Zip(e => e.Insert(0, Named(bundle, name, []))), BundleFault.DirectoryNotAllowed);
    }

    [Theory]
    [InlineData("{root}/")]
    [InlineData("{root}/ja/")]
    public void A_directory_entry_that_carries_data_is_refused(string name)
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip(e => e.Insert(0, new RawEntry(name.Replace("{root}", bundle.Root), []) { UncompressedSize = 5 }));
        Refused(bundle, zip, BundleFault.DirectoryNotAllowed);
    }

    [Fact]
    public void The_root_folder_entry_may_come_only_once()
    {
        var bundle = new TestBundle();
        Refused(bundle, bundle.Zip(e => e.InsertRange(0, [Named(bundle, "{root}/", []), Named(bundle, "{root}/", [])])), BundleFault.DuplicateName);
    }

    [Fact]
    public void A_satellite_folder_entry_with_no_files_under_it_is_refused()
    {
        var bundle = new TestBundle();
        bundle.Files.Remove("ja/PcMieru.Core.resources.dll");
        var zip = bundle.Zip(e => e.Insert(0, Named(bundle, "{root}/ja/", [])));
        Refused(bundle, zip, BundleFault.EmptyDirectory);
    }

    [Fact]
    public void A_file_and_a_directory_with_one_name_collide()
    {
        var bundle = new TestBundle();

        // The folder entry first, the file after it.
        var first = bundle.Zip(e =>
        {
            e.Insert(0, Named(bundle, "{root}/ja/", []));
            e.Add(Named(bundle, "{root}/ja"));
        });
        Refused(bundle, first, BundleFault.FileDirectoryCollision);

        // The files of ja/ first (the folder is implied), a file named "ja" after them.
        var implied = bundle.Zip(e => e.Add(Named(bundle, "{root}/JA")));
        Refused(bundle, implied, BundleFault.FileDirectoryCollision);

        // A file named "ja" before the folder's files is not on the table at all.
        var late = bundle.Zip(e => e.Insert(0, Named(bundle, "{root}/ja")));
        Refused(bundle, late, BundleFault.NotAllowed);
    }

    // --------------------------------------------------------------------------------------------------------------------- duplicates

    [Theory]
    [InlineData("{root}/pcmi.exe")]
    [InlineData("{root}/PCMI.EXE")]
    [InlineData("{root}/PcMieru.CORE.dll")]
    [InlineData("{root}/pcmieru.core.dll")]
    [InlineData("{root}/ja/PCMIERU.Core.resources.dll")]
    public void A_name_twice_is_refused_ignoring_case(string name)
    {
        var bundle = new TestBundle();
        Refused(bundle, bundle.Zip(e => e.Add(Named(bundle, name))), BundleFault.DuplicateName);
    }

    // ------------------------------------------------------------------------------------------------------------------------- links

    [Theory]
    [InlineData((ushort)0x031E, 0xA1FF0000u)] // Unix: a symbolic link (lrwxrwxrwx)
    [InlineData((ushort)0x031E, 0x11A40000u)] // Unix: a pipe
    [InlineData((ushort)0x031E, 0x21A40000u)] // Unix: a character device
    [InlineData((ushort)0x031E, 0x61A40000u)] // Unix: a block device
    [InlineData((ushort)0x031E, 0xC1A40000u)] // Unix: a socket
    [InlineData((ushort)0x031E, 0x41ED0010u)] // Unix: a directory mode on a file name
    [InlineData((ushort)0x0014, 0x00000400u)] // Windows: a reparse point (junction, symbolic link)
    [InlineData((ushort)0x0014, 0x00000040u)] // Windows: a device
    [InlineData((ushort)0x0014, 0x00000010u)] // Windows: the directory bit on a file name
    // The made-by host does not say which half of the attributes is true: a zip that claims MS-DOS or NTFS may carry a Unix mode, and one that claims Unix may carry DOS bits.
    [InlineData((ushort)0x0014, 0xA1FF0000u)] // host 0 (MS-DOS): a symbolic link in the Unix mode
    [InlineData((ushort)0x0014, 0x21A40000u)] // host 0: a character device
    [InlineData((ushort)0x0014, 0x61A40000u)] // host 0: a block device
    [InlineData((ushort)0x0014, 0x11A40000u)] // host 0: a pipe
    [InlineData((ushort)0x0014, 0xC1A40000u)] // host 0: a socket
    [InlineData((ushort)0x0014, 0x41ED0000u)] // host 0: a directory mode on a file name
    [InlineData((ushort)0x0A14, 0xA1FF0000u)] // host 10 (NTFS): a symbolic link in the Unix mode
    [InlineData((ushort)0x0A14, 0x21A40000u)] // host 10: a character device
    [InlineData((ushort)0x0A14, 0x00000400u)] // host 10: a reparse point
    [InlineData((ushort)0x0A14, 0x00000040u)] // host 10: a device
    [InlineData((ushort)0x0714, 0xA1FF0000u)] // some other host (7, Macintosh)
    [InlineData((ushort)0x031E, 0x00000400u)] // host 3 (Unix): a reparse point in the DOS bits
    [InlineData((ushort)0x031E, 0x81A40400u)] // host 3: a regular file mode and a reparse point
    [InlineData((ushort)0x031E, 0x81A40040u)] // host 3: a regular file mode and a device
    public void A_link_or_a_special_file_is_refused(ushort madeBy, uint attributes)
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip(e =>
        {
            var entry = Find(e, "/NOTICE.txt");
            entry.VersionMadeBy = madeBy;
            entry.ExternalAttributes = attributes;
        });
        Refused(bundle, zip, BundleFault.Link);
    }

    [Theory]
    [InlineData((ushort)0x0014, 0x00000000u)] // what .NET writes on Windows: nothing
    [InlineData((ushort)0x0014, 0x00000020u)] // the DOS archive bit
    [InlineData((ushort)0x0014, 0x00000001u)] // read only
    [InlineData((ushort)0x0014, 0x81A40000u)] // host 0 with a regular file mode
    [InlineData((ushort)0x0A14, 0x00000000u)]
    [InlineData((ushort)0x0A14, 0x81A40020u)]
    [InlineData((ushort)0x031E, 0x81A40000u)]
    [InlineData((ushort)0x031E, 0x81ED0000u)] // an executable
    public void A_plain_file_is_not_refused_whatever_host_made_the_zip(ushort madeBy, uint attributes)
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip(e =>
        {
            var entry = Find(e, "/NOTICE.txt");
            entry.VersionMadeBy = madeBy;
            entry.ExternalAttributes = attributes;
        });
        var result = bundle.Validate(zip);
        Assert.True(result.Ok, result.ToString());
    }

    [Theory]
    [InlineData((ushort)0x0014, 0x81A40000u)] // host 0: a regular file mode on a directory name
    [InlineData((ushort)0x0A14, 0x81A40000u)] // host 10
    [InlineData((ushort)0x031E, 0x81A40000u)] // host 3
    [InlineData((ushort)0x0014, 0xA1FF0010u)] // host 0: a symbolic link that calls itself a directory
    [InlineData((ushort)0x0A14, 0x00000410u)] // host 10: a reparse point that calls itself a directory
    public void A_directory_entry_that_is_something_else_is_refused_on_every_host(ushort madeBy, uint attributes)
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip(e => e.Insert(0, new RawEntry(bundle.Root + "/ja/", []) { VersionMadeBy = madeBy, ExternalAttributes = attributes }));
        Refused(bundle, zip, BundleFault.Link);
    }

    [Fact]
    public void A_regular_file_mode_on_a_directory_name_is_refused()
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip(e => e.Insert(0, new RawEntry(bundle.Root + "/ja/", []) { VersionMadeBy = 0x031E, ExternalAttributes = 0x81A4u << 16 }));
        Refused(bundle, zip, BundleFault.Link);
    }

    [Theory]
    [InlineData((ushort)0x0001)]
    [InlineData((ushort)0x0041)]
    [InlineData((ushort)0x0040)]
    [InlineData((ushort)0x2000)]
    public void An_encrypted_entry_is_refused(ushort flags)
    {
        var bundle = new TestBundle();
        Refused(bundle, bundle.Zip(e => Find(e, "/NOTICE.txt").Flags = flags), BundleFault.Encrypted);
    }

    [Theory]
    [InlineData((ushort)1)] // shrunk
    [InlineData((ushort)6)] // imploded
    [InlineData((ushort)9)] // deflate64
    [InlineData((ushort)12)] // bzip2
    [InlineData((ushort)14)] // lzma
    [InlineData((ushort)93)] // zstd
    [InlineData((ushort)99)] // aes
    public void A_compression_method_that_is_not_stored_or_deflate_is_refused(ushort method)
    {
        var bundle = new TestBundle();
        Refused(bundle, bundle.Zip(e => Find(e, "/NOTICE.txt").Method = method), BundleFault.UnsupportedMethod);
    }

    // ------------------------------------------------------------------------------------------------------------------ not a zip, zip64

    [Fact]
    public void Something_that_is_not_a_zip_is_refused()
    {
        var bundle = new TestBundle();
        Refused(bundle, [], BundleFault.NotZip);
        Refused(bundle, new byte[100], BundleFault.NotZip);
        Refused(bundle, Encoding.ASCII.GetBytes(new string('x', 200)), BundleFault.NotZip);

        var zip = bundle.Zip();
        Refused(bundle, zip[..^10], BundleFault.NotZip); // the end record cut
        Refused(bundle, zip.Concat(new byte[5]).ToArray(), BundleFault.NotZip); // bytes after the end record
        Refused(bundle, zip[..(zip.Length / 2)], BundleFault.NotZip); // the middle of the file
    }

    [Fact]
    public void A_damaged_central_directory_is_refused()
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip();
        var end = zip.Length - 22;
        var directoryAt = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end + 16));

        var badSignature = (byte[])zip.Clone();
        badSignature[directoryAt] ^= 0xFF;
        Refused(bundle, badSignature, BundleFault.NotZip);

        var tooMany = (byte[])zip.Clone();
        var count = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(end + 10));
        BinaryPrimitives.WriteUInt16LittleEndian(tooMany.AsSpan(end + 8), (ushort)(count + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(tooMany.AsSpan(end + 10), (ushort)(count + 1));
        Refused(bundle, tooMany, BundleFault.NotZip);

        var tooFew = (byte[])zip.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(tooFew.AsSpan(end + 8), (ushort)(count - 1));
        BinaryPrimitives.WriteUInt16LittleEndian(tooFew.AsSpan(end + 10), (ushort)(count - 1));
        Refused(bundle, tooFew, BundleFault.NotZip);

        var otherDisk = (byte[])zip.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(otherDisk.AsSpan(end + 4), 1);
        Refused(bundle, otherDisk, BundleFault.NotZip);

        var shifted = (byte[])zip.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(shifted.AsSpan(end + 16), (uint)(directoryAt + 1));
        Refused(bundle, shifted, BundleFault.NotZip);
    }

    [Fact]
    public void A_stream_that_cannot_seek_is_refused_and_not_read()
    {
        var bundle = new TestBundle();
        using var stream = new NonSeekableStream(new MemoryStream(bundle.Zip()));
        var result = BundleZip.Validate(stream, bundle.Version, bundle.Outer(), Limits.Default, BundleFloor.ForThisBuild);
        Assert.Equal(BundleFault.NotZip, result.Fault);
    }

    [Fact]
    public void A_zip64_zip_is_refused()
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip();
        var end = zip.Length - 22;

        var entries = (byte[])zip.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(entries.AsSpan(end + 8), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(entries.AsSpan(end + 10), ushort.MaxValue);
        Refused(bundle, entries, BundleFault.Zip64);

        var size = (byte[])zip.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(size.AsSpan(end + 12), uint.MaxValue);
        Refused(bundle, size, BundleFault.Zip64);

        var offset = (byte[])zip.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(offset.AsSpan(end + 16), uint.MaxValue);
        Refused(bundle, offset, BundleFault.Zip64);

        var locator = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064B50);
        Refused(bundle, zip[..end].Concat(locator).Concat(zip[end..]).ToArray(), BundleFault.Zip64);

        Refused(bundle, bundle.Zip(e => Find(e, "/NOTICE.txt").UncompressedSize = uint.MaxValue), BundleFault.Zip64);
        Refused(bundle, bundle.Zip(e => Find(e, "/NOTICE.txt").CompressedSize = uint.MaxValue), BundleFault.Zip64);
    }

    // ----------------------------------------------------------------------------------------- the end record and the central directory

    /// <summary>A bundle whose NOTICE.txt the real directory calls a symbolic link, and the same zip as it is without that (the fake directory).</summary>
    private static (byte[] RealWithLink, List<byte[]> CleanRecords) LinkInTheRealDirectoryOnly(TestBundle bundle)
    {
        var real = bundle.Zip(e =>
        {
            var entry = Find(e, "/NOTICE.txt");
            entry.VersionMadeBy = 0x031E;
            entry.ExternalAttributes = 0xA1FF0000u; // lrwxrwxrwx
        });
        var (_, clean) = ZipSurgery.Split(bundle.Zip());
        return (real, clean);
    }

    [Fact]
    public void A_second_end_record_planted_in_the_comment_is_refused()
    {
        // The first end record (and the directory before it) is the real zip's, in which NOTICE.txt is a symbolic link. A second, proper-looking end record
        // at the very end of the file points to a second directory in which NOTICE.txt is a plain file. A reader that takes the last end record (ZipArchive,
        // and the hand reader of before) sees the clean zip; a reader that takes the first one sees the link. Neither is the zip we mean: refuse it.
        var bundle = new TestBundle();
        var (real, clean) = LinkInTheRealDirectoryOnly(bundle);
        var planted = ZipSurgery.WithPlantedEnd(real, clean, fakeCommentLengthField: 0);

        var result = Refused(bundle, planted, BundleFault.ZipAmbiguous);
        Assert.Contains("end", result.Detail);
    }

    [Fact]
    public void A_fake_end_record_that_lies_about_its_comment_is_refused()
    {
        // The same plant, but the last end record claims 7 bytes of comment after it that are not there. A reader that insists on the comment fitting skips it
        // and takes the real one; ZipArchive takes it. Here the real directory is the clean one and the fake one has the link.
        var bundle = new TestBundle();
        var (withLink, clean) = LinkInTheRealDirectoryOnly(bundle);
        var (_, linked) = ZipSurgery.Split(withLink);
        var planted = ZipSurgery.WithPlantedEnd(bundle.Zip(), linked, fakeCommentLengthField: 7);

        Refused(bundle, planted, BundleFault.NotZip);
        Assert.NotEmpty(clean);
    }

    [Fact]
    public void A_zip_whose_end_record_is_not_the_last_22_bytes_is_refused_whatever_follows_it()
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip();
        Refused(bundle, zip.Concat(new byte[1]).ToArray(), BundleFault.NotZip);
        Refused(bundle, zip.Concat(new byte[22]).ToArray(), BundleFault.NotZip);
        Refused(bundle, zip.Concat(zip[^22..]).ToArray(), BundleFault.NotZip); // the end record twice in a row
    }

    [Fact]
    public void A_central_record_with_a_comment_is_refused()
    {
        var bundle = new TestBundle();
        var (body, records) = ZipSurgery.Split(bundle.Zip());
        records[3] = ZipSurgery.WithComment(records[3], Encoding.ASCII.GetBytes("x"));
        var result = Refused(bundle, ZipSurgery.Join(body, records), BundleFault.NotZip);
        Assert.Contains("comment", result.Detail);
    }

    [Fact]
    public void An_extra_field_of_a_kilobyte_is_read_and_one_byte_more_is_refused()
    {
        var bundle = new TestBundle();
        var (body, records) = ZipSurgery.Split(bundle.Zip());

        var edge = records.ToList();
        edge[2] = ZipSurgery.WithExtra(edge[2], ZipSurgery.ExtraBlock(0xCAFE, 1020)); // 4 + 1020 = 1024 bytes
        var result = bundle.Validate(ZipSurgery.Join(body, edge));
        Assert.True(result.Ok, result.ToString());

        var over = records.ToList();
        over[2] = ZipSurgery.WithExtra(over[2], ZipSurgery.ExtraBlock(0xCAFE, 1021));
        Refused(bundle, ZipSurgery.Join(body, over), BundleFault.NotZip);
    }

    [Fact]
    public void An_extra_field_that_does_not_parse_is_refused()
    {
        var bundle = new TestBundle();
        var (body, records) = ZipSurgery.Split(bundle.Zip());
        var block = ZipSurgery.ExtraBlock(0xCAFE, 10)[..7]; // the block says 10 bytes and 3 are there
        records[2] = ZipSurgery.WithExtra(records[2], block);
        Refused(bundle, ZipSurgery.Join(body, records), BundleFault.NotZip);

        var single = bundle.Zip();
        var (body2, records2) = ZipSurgery.Split(single);
        records2[2] = ZipSurgery.WithExtra(records2[2], [0x01, 0x02, 0x03]); // not even a block header
        Refused(bundle, ZipSurgery.Join(body2, records2), BundleFault.NotZip);
    }

    [Theory]
    [InlineData(0)] // the only block
    [InlineData(1)] // after another block
    public void A_zip64_extra_field_in_a_central_record_is_refused(int position)
    {
        var bundle = new TestBundle();
        var (body, records) = ZipSurgery.Split(bundle.Zip());
        byte[] extra = position == 0
            ? ZipSurgery.ExtraBlock(0x0001, 0)
            : [.. ZipSurgery.ExtraBlock(0xCAFE, 4), .. ZipSurgery.ExtraBlock(0x0001, 8)];
        records[2] = ZipSurgery.WithExtra(records[2], extra);
        Refused(bundle, ZipSurgery.Join(body, records), BundleFault.Zip64);
    }

    [Fact]
    public void A_name_longer_than_255_bytes_is_refused_as_a_central_record()
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip(e => e.Add(Named(bundle, "{root}/" + new string('a', 300) + ".dll")));
        Refused(bundle, zip, BundleFault.NotZip);
    }

    [Fact]
    public void A_central_directory_that_no_allowed_entry_count_could_fill_is_refused_before_it_is_read()
    {
        // 8 MiB of nothing and an end record that says "one entry, the central directory is all of the 8 MiB". The most that n records can take is
        // n x (46 + 255 + 1 KiB) (a name of 255 bytes, an extra field of 1 KiB, no comment); asking for more than that must not allocate and read it.
        var limits = Limits.Default;
        var size = 8 * 1024 * 1024;
        Assert.True(size > 1L * (46 + 255 + 1024));
        byte[] zip = [.. new byte[size], .. ZipSurgery.End(1, size, 0)];

        var bundle = new TestBundle();
        using var stream = FailingReadStream.Counting(new MemoryStream(zip));
        var result = BundleZip.Validate(stream, bundle.Version, bundle.Outer(), limits, BundleFloor.ForThisBuild);
        Assert.Equal(BundleFault.NotZip, result.Fault);
        Assert.True(stream.BytesRead < size / 2, $"the directory was read: {stream.BytesRead} bytes");
    }

    [Fact]
    public void A_central_record_that_points_at_no_local_header_is_refused()
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip(e => Find(e, "/NOTICE.txt").LocalHeaderOffset = 7); // the middle of the first local header
        Refused(bundle, zip, BundleFault.NotZip);

        // Or into the central directory itself.
        var plain = bundle.Zip();
        var (body, records) = ZipSurgery.Split(plain);
        ZipSurgery.SetLocalOffset(records[1], (uint)body.Length);
        Refused(bundle, ZipSurgery.Join(body, records), BundleFault.NotZip);
    }

    [Fact]
    public void A_central_record_whose_local_header_belongs_to_another_file_is_refused()
    {
        // Two files with the same content (so the same compressed bytes): the records swap their local header offsets. ZipArchive opens each name through the
        // other's header and reads the right bytes, the hashes agree, and nothing else notices; the local header's own name does.
        var bundle = new TestBundle();
        bundle.Files["a.dll"] = Encoding.ASCII.GetBytes("the same bytes");
        bundle.Files["b.dll"] = Encoding.ASCII.GetBytes("the same bytes");
        var (body, records) = ZipSurgery.Split(bundle.Zip());
        var a = records.Single(r => ZipSurgery.NameOf(r).EndsWith("/a.dll", StringComparison.Ordinal));
        var b = records.Single(r => ZipSurgery.NameOf(r).EndsWith("/b.dll", StringComparison.Ordinal));
        var offsetOfA = ZipSurgery.LocalOffsetOf(a);
        ZipSurgery.SetLocalOffset(a, ZipSurgery.LocalOffsetOf(b));
        ZipSurgery.SetLocalOffset(b, offsetOfA);

        var result = Refused(bundle, ZipSurgery.Join(body, records), BundleFault.ZipAmbiguous);
        Assert.Contains("local header", result.Detail);
    }

    [Fact]
    public void The_unchanged_pieces_put_together_again_are_the_good_zip()
    {
        // The surgery itself must not be what breaks the zips above.
        var bundle = new TestBundle();
        var zip = bundle.Zip();
        var (body, records) = ZipSurgery.Split(zip);
        var again = ZipSurgery.Join(body, records);
        Assert.Equal(zip, again);
        Assert.True(bundle.Validate(again).Ok);
    }

    // --------------------------------------------------------------------------------------------------------------- a stream that fails

    [Theory]
    [InlineData("io")]
    [InlineData("not supported")]
    [InlineData("disposed")]
    [InlineData("unauthorized")]
    public void A_stream_that_fails_on_any_read_gives_a_refusal_and_never_an_exception(string kind)
    {
        Func<Exception> failure = kind switch
        {
            "io" => () => new IOException("the disk went away"),
            "not supported" => () => new NotSupportedException("no"),
            "disposed" => () => new ObjectDisposedException("zip"),
            _ => () => new UnauthorizedAccessException("denied"),
        };

        var bundle = new TestBundle();
        var zip = bundle.Zip();
        var reached = 0;
        for (var failOn = 1; failOn <= 20_000; failOn++)
        {
            using var stream = new FailingReadStream(new MemoryStream(zip), failOn, failure);
            BundleResult result;
            try
            {
                result = BundleZip.Validate(stream, bundle.Version, bundle.Outer(), Limits.Default, BundleFloor.ForThisBuild);
            }
            catch (Exception ex)
            {
                Assert.Fail($"the failure of read {failOn} ({kind}) escaped as {ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (!stream.Triggered)
            {
                Assert.True(result.Ok, result.ToString()); // fewer reads than failOn: the sweep is over
                Assert.True(reached > 20, $"only {reached} reads were swept");
                return;
            }

            reached++;
            Assert.False(result.Ok, $"the failure of read {failOn} ({kind}) was swallowed");
            Assert.True(result.Fault is BundleFault.NotZip or BundleFault.ReadFailed, $"read {failOn} ({kind}): {result}");
        }

        Assert.Fail("the sweep never ended");
    }

    // ---------------------------------------------------------------------------------------------------------- inflating past the headers

    [Fact]
    public void An_entry_that_is_shorter_than_its_headers_and_the_manifest_say_is_refused_on_its_length()
    {
        // The headers say 3000 bytes (as the manifest does) but the deflate stream holds 2000.
        var bundle = new TestBundle();
        var zip = bundle.Zip(e =>
        {
            var entry = Find(e, "/System.Runtime.dll");
            entry.Data = Encoding.ASCII.GetBytes(new string('r', 2000));
            entry.UncompressedSize = 3000;
        });
        var result = bundle.Validate(zip);
        Assert.False(result.Ok);
        Assert.True(result.Fault is BundleFault.LengthMismatch or BundleFault.NotZip or BundleFault.ReadFailed, result.ToString());
    }

    [Fact]
    public void An_entry_whose_deflate_stream_inflates_past_the_headers_and_the_manifest_is_refused_on_its_length()
    {
        // The headers say 3000 bytes (as the manifest does) and the first 3000 bytes of the stream are exactly the manifest's file, but the stream goes on
        // to 6000. The validator must not call that file good.
        var bundle = new TestBundle();
        var zip = bundle.Zip(e =>
        {
            var entry = Find(e, "/System.Runtime.dll");
            entry.Data = Encoding.ASCII.GetBytes(new string('r', 6000));
            entry.UncompressedSize = 3000;
        });
        var result = Refused(bundle, zip, BundleFault.LengthMismatch);
        Assert.Contains("System.Runtime.dll", result.Detail);
    }

    // -------------------------------------------------------------------------------------------------------------- manifest and files

    [Fact]
    public void A_file_that_the_outer_manifest_does_not_list_is_refused()
    {
        var bundle = new TestBundle();
        var outer = bundle.Outer();
        var result = Refused(bundle, bundle.Zip(e => e.Add(Named(bundle, "{root}/extra.dll"))), BundleFault.FileSetMismatch, outer);
        Assert.Contains("extra.dll", result.Detail);
    }

    [Fact]
    public void A_file_that_the_outer_manifest_lists_and_the_zip_lacks_is_refused()
    {
        var bundle = new TestBundle();
        bundle.Files["extra.dll"] = [1, 2, 3];
        var outer = bundle.Outer();
        var result = Refused(bundle, bundle.Zip(e => e.RemoveAll(x => x.Name.EndsWith("/extra.dll", StringComparison.Ordinal))), BundleFault.FileSetMismatch, outer);
        Assert.Contains("extra.dll", result.Detail);
    }

    [Fact]
    public void A_name_spelled_with_other_letters_in_the_zip_and_in_the_manifest_is_refused()
    {
        var bundle = new TestBundle();
        bundle.Files["System.Runtime.dll"] = [1, 2, 3];
        var zip = bundle.Zip(e => Find(e, "/System.Runtime.dll").Name = bundle.Root + "/system.runtime.dll");
        var result = Refused(bundle, zip, BundleFault.FileSetMismatch);
        Assert.Contains("spelled", result.Detail);
    }

    [Fact]
    public void The_outer_manifest_must_list_the_inner_manifest()
    {
        var bundle = new TestBundle();
        var outer = new BundleManifest(bundle.Version, bundle.BuildId, bundle.Outer().Files.Where(f => f.Path != BundleFormat.InnerManifestName));
        Refused(bundle, bundle.Zip(), BundleFault.FileSetMismatch, outer);
    }

    [Fact]
    public void A_file_with_other_content_of_the_same_length_is_refused_on_its_hash()
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip(e =>
        {
            var entry = Find(e, "/System.Runtime.dll");
            entry.Data = (byte[])entry.Data.Clone();
            entry.Data[100] ^= 0x01;
        });
        var result = Refused(bundle, zip, BundleFault.HashMismatch);
        Assert.Contains("System.Runtime.dll", result.Detail);
    }

    [Fact]
    public void A_file_of_another_length_is_refused_on_its_length()
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip(e =>
        {
            var entry = Find(e, "/System.Runtime.dll");
            entry.Data = entry.Data[..^1];
        });
        var result = Refused(bundle, zip, BundleFault.LengthMismatch);
        Assert.Contains("System.Runtime.dll", result.Detail);
    }

    [Fact]
    public void A_header_that_says_another_length_than_the_manifest_is_refused()
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip(e => Find(e, "/System.Runtime.dll").UncompressedSize = 10);
        Refused(bundle, zip, BundleFault.LengthMismatch);
    }

    [Fact]
    public void The_inner_manifest_must_be_the_one_the_outer_manifest_hashes()
    {
        var bundle = new TestBundle();
        var inner = bundle.InnerBytes();
        var other = (byte[])inner.Clone();
        other[^3] ^= 0x01; // same length, other hash
        var outer = bundle.Outer(other);
        var result = Refused(bundle, bundle.Zip(), BundleFault.HashMismatch, outer);
        Assert.Contains(BundleFormat.InnerManifestName, result.Detail);
    }

    [Fact]
    public void The_outer_and_the_inner_manifest_must_say_the_same_build_and_version()
    {
        var bundle = new TestBundle();
        var files = bundle.Files.Select(f => TestBundle.Line(f.Key, f.Value));

        var otherBuild = new BundleManifest(bundle.Version, "ffffffff-ffff-4fff-8fff-ffffffffffff", files).ToUtf8Bytes();
        var result = Refused(bundle, bundle.Zip(e => Find(e, "/" + BundleFormat.InnerManifestName).Data = otherBuild), BundleFault.OuterInnerMismatch, bundle.Outer(otherBuild));
        Assert.Contains("build id", result.Detail);

        var otherVersion = new BundleManifest("0.5.1", bundle.BuildId, files).ToUtf8Bytes();
        result = Refused(bundle, bundle.Zip(e => Find(e, "/" + BundleFormat.InnerManifestName).Data = otherVersion), BundleFault.OuterInnerMismatch, bundle.Outer(otherVersion));
        Assert.Contains("0.5.1", result.Detail);
    }

    [Fact]
    public void The_inner_manifest_must_list_what_the_outer_one_lists_without_itself()
    {
        var bundle = new TestBundle();

        // One file left out of the inner manifest.
        var fewer = new BundleManifest(bundle.Version, bundle.BuildId, bundle.Files.Where(f => f.Key != "NOTICE.txt").Select(f => TestBundle.Line(f.Key, f.Value))).ToUtf8Bytes();
        Refused(bundle, bundle.Zip(e => Find(e, "/" + BundleFormat.InnerManifestName).Data = fewer), BundleFault.InnerFilesMismatch, bundle.Outer(fewer));

        // One file with another hash in the inner manifest (the file itself is as the outer manifest says).
        var otherHash = new BundleManifest(
            bundle.Version,
            bundle.BuildId,
            bundle.Files.Select(f => f.Key == "NOTICE.txt" ? new BundleFile(f.Key, f.Value.Length, new string('0', 64)) : TestBundle.Line(f.Key, f.Value))).ToUtf8Bytes();
        Refused(bundle, bundle.Zip(e => Find(e, "/" + BundleFormat.InnerManifestName).Data = otherHash), BundleFault.InnerFilesMismatch, bundle.Outer(otherHash));

        // The inner manifest lists itself.
        var itself = new BundleManifest(
            bundle.Version,
            bundle.BuildId,
            bundle.Files.Select(f => TestBundle.Line(f.Key, f.Value)).Append(new BundleFile(BundleFormat.InnerManifestName, 1, new string('0', 64)))).ToUtf8Bytes();
        Refused(bundle, bundle.Zip(e => Find(e, "/" + BundleFormat.InnerManifestName).Data = itself), BundleFault.InnerFilesMismatch, bundle.Outer(itself));
    }

    [Fact]
    public void An_inner_manifest_the_strict_reader_refuses_is_refused_with_the_readers_reason()
    {
        var bundle = new TestBundle();

        var bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble().Concat(bundle.InnerBytes()).ToArray();
        var result = Refused(bundle, bundle.Zip(e => Find(e, "/" + BundleFormat.InnerManifestName).Data = bom), BundleFault.ManifestBom, bundle.Outer(bom));
        Assert.Contains(BundleFormat.InnerManifestName, result.Detail);

        var garbage = Encoding.ASCII.GetBytes("this is not json");
        Refused(bundle, bundle.Zip(e => Find(e, "/" + BundleFormat.InnerManifestName).Data = garbage), BundleFault.ManifestSyntax, bundle.Outer(garbage));

        var duplicate = Encoding.ASCII.GetBytes("{ \"format\": \"pcm-bundle/1\", \"format\": \"pcm-bundle/1\" }");
        Refused(bundle, bundle.Zip(e => Find(e, "/" + BundleFormat.InnerManifestName).Data = duplicate), BundleFault.DuplicateKey, bundle.Outer(duplicate));

        var future = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(bundle.InnerBytes()).Replace("pcm-bundle/1", "pcm-bundle/2", StringComparison.Ordinal));
        Refused(bundle, bundle.Zip(e => Find(e, "/" + BundleFormat.InnerManifestName).Data = future), BundleFault.UnknownFormat, bundle.Outer(future));
    }

    // ------------------------------------------------------------------------------------------------------------------------- limits

    [Fact]
    public void The_zip_size_limit_is_checked_at_its_edge()
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip();
        Assert.True(bundle.Validate(zip, limits: new Limits { ZipBytes = zip.Length }).Ok);
        Refused(bundle, zip, BundleFault.ZipTooLarge, limits: new Limits { ZipBytes = zip.Length - 1 });
    }

    [Fact]
    public void The_file_count_limit_is_checked_at_its_edge()
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip();
        var count = bundle.Files.Count + 1;
        Assert.True(bundle.Validate(zip, limits: new Limits { Files = count }).Ok);
        Refused(bundle, zip, BundleFault.TooManyFiles, limits: new Limits { Files = count - 1 });
        Refused(bundle, zip, BundleFault.TooManyFiles, limits: new Limits { Files = 3 });
    }

    [Fact]
    public void The_total_size_limit_is_checked_at_its_edge()
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip();
        var total = bundle.Files.Values.Sum(v => (long)v.Length) + bundle.InnerBytes().Length;
        Assert.True(bundle.Validate(zip, limits: new Limits { TotalBytes = total }).Ok);
        Refused(bundle, zip, BundleFault.TotalTooLarge, limits: new Limits { TotalBytes = total - 1 });
    }

    [Fact]
    public void The_manifest_size_limit_is_checked_at_its_edge()
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip();
        var length = bundle.InnerBytes().Length;
        Assert.True(bundle.Validate(zip, limits: new Limits { ManifestBytes = length }).Ok);
        Refused(bundle, zip, BundleFault.ManifestTooLarge, limits: new Limits { ManifestBytes = length - 1 });
    }

    [Fact]
    public void The_default_limits_are_the_proposed_ones()
    {
        var limits = Limits.Default;
        Assert.Equal(256L * 1024 * 1024, limits.ZipBytes);
        Assert.Equal(768L * 1024 * 1024, limits.TotalBytes);
        Assert.Equal(4096, limits.Files);
        Assert.Equal(2 * 1024 * 1024, limits.ManifestBytes);
    }

    // ------------------------------------------------------------------------------------------------------------------------- misuse

    [Fact]
    public void The_arguments_are_checked()
    {
        var bundle = new TestBundle();
        using var stream = new MemoryStream(bundle.Zip());
        Assert.Throws<ArgumentNullException>(() => BundleZip.Validate(null!, "0.5.0", bundle.Outer(), Limits.Default));
        Assert.Throws<ArgumentNullException>(() => BundleZip.Validate(stream, "0.5.0", null!, Limits.Default));
        Assert.Throws<ArgumentNullException>(() => BundleZip.Validate(stream, "0.5.0", bundle.Outer(), null!));
    }
}
