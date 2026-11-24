using System.Security.Cryptography;
using PcMieru.Update.Bundle;

namespace PcMieru.Update.Tests;

/// <summary>
/// The checks the zip and the folder validators share, tested on their own so that a stream which lies about its length can be made at will:
/// the check must count what it really reads, whatever a header or a directory declared. (ZipArchive itself truncates a deflate stream at the declared
/// size, so a zip with a lying header cannot show this; a stream can.)
/// </summary>
public class BundleCheckerTests
{
    private static List<BundleEntry> EntriesOf(TestBundle bundle, Func<string, byte[], BundleEntry?>? replace = null)
    {
        var all = bundle.Files.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        all[BundleFormat.InnerManifestName] = bundle.InnerBytes();
        return all
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => replace?.Invoke(f.Key, f.Value) ?? new BundleEntry(f.Key, f.Value.Length, () => new MemoryStream(f.Value)))
            .ToList();
    }

    private static BundleResult Check(IReadOnlyList<BundleEntry> entries, TestBundle bundle, Limits? limits = null, IReadOnlyCollection<string>? floor = null) =>
        BundleChecker.Check(entries, bundle.Version, null, limits ?? Limits.Default, floor ?? BundleFloor.ForThisBuild);

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    // ---------------------------------------------------------------------------------------------------------------------------- Hash

    [Fact]
    public void Hash_of_nothing_is_the_hash_of_the_empty_string()
    {
        var digest = BundleChecker.Hash(new MemoryStream(), 10);
        Assert.Equal(0, digest.Bytes);
        Assert.False(digest.Exceeded);
        Assert.Equal(Sha([]), digest.Sha256);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(80 * 1024)]
    [InlineData(80 * 1024 + 1)]
    [InlineData(300_000)]
    public void Hash_of_a_stream_of_exactly_the_cap_passes_and_one_more_byte_does_not(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);

        var exact = BundleChecker.Hash(new MemoryStream(data), length);
        Assert.False(exact.Exceeded);
        Assert.Equal(length, exact.Bytes);
        Assert.Equal(Sha(data), exact.Sha256);

        var over = BundleChecker.Hash(new MemoryStream(data), length - 1);
        Assert.True(over.Exceeded);
        Assert.Equal(length, over.Bytes);

        var roomy = BundleChecker.Hash(new MemoryStream(data), length + 1);
        Assert.False(roomy.Exceeded);
        Assert.Equal(length, roomy.Bytes);
    }

    [Fact]
    public void Hash_with_a_cap_of_zero_accepts_only_an_empty_stream()
    {
        Assert.False(BundleChecker.Hash(new MemoryStream(), 0).Exceeded);
        Assert.True(BundleChecker.Hash(new MemoryStream([1]), 0).Exceeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(80 * 1024)]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public void Hash_of_a_stream_without_an_end_reads_the_cap_plus_one_byte_and_stops(int cap)
    {
        var endless = new EndlessStream();
        var digest = BundleChecker.Hash(endless, cap);
        Assert.True(digest.Exceeded);
        Assert.Equal(cap + 1L, endless.BytesRead);
        Assert.Equal(cap + 1L, digest.Bytes);
    }

    // ------------------------------------------------------------------------------------------------------------------- FirstSetDifference

    [Fact]
    public void The_same_names_in_any_order_have_no_difference()
    {
        Assert.Null(BundleChecker.FirstSetDifference(["b", "a", "c"], ["c", "a", "b"], "left", "right"));
        Assert.Null(BundleChecker.FirstSetDifference([], [], "left", "right"));
    }

    [Fact]
    public void A_name_on_one_side_only_is_named_with_the_side_it_is_on()
    {
        var left = BundleChecker.FirstSetDifference(["a", "b", "x"], ["a", "b"], "the zip", "the manifest");
        Assert.NotNull(left);
        Assert.Contains("'x'", left);
        Assert.Contains("the zip", left);

        var right = BundleChecker.FirstSetDifference(["a", "b"], ["a", "b", "y"], "the zip", "the manifest");
        Assert.NotNull(right);
        Assert.Contains("'y'", right);
        Assert.Contains("the manifest", right);
    }

    [Fact]
    public void The_same_name_spelled_with_other_letters_is_a_difference_and_says_so()
    {
        var text = BundleChecker.FirstSetDifference(["PcMieru.Core.dll"], ["pcmieru.core.dll"], "the zip", "the manifest");
        Assert.NotNull(text);
        Assert.Contains("spelled", text);
        Assert.Contains("PcMieru.Core.dll", text);
        Assert.Contains("pcmieru.core.dll", text);
    }

    [Fact]
    public void A_control_character_in_a_name_is_not_shown_as_it_is()
    {
        var text = BundleChecker.FirstSetDifference(["a\u0007b"], [], "the zip", "the manifest");
        Assert.NotNull(text);
        Assert.DoesNotContain('\u0007', text);
    }

    // ------------------------------------------------------------------------------------------------------------------------------ Check

    [Fact]
    public void A_good_set_of_entries_passes()
    {
        var bundle = new TestBundle();
        var result = Check(EntriesOf(bundle), bundle);
        Assert.True(result.Ok, result.ToString());
        Assert.Equal(bundle.Files.Count + 1, result.FileCount);
        Assert.Equal(bundle.Files.Values.Sum(v => (long)v.Length) + bundle.InnerBytes().Length, result.TotalBytes);
    }

    [Fact]
    public void A_file_that_never_ends_is_stopped_at_the_length_of_the_manifest()
    {
        var bundle = new TestBundle();
        var length = bundle.Files["System.Runtime.dll"].Length;
        var endless = new EndlessStream((byte)'r');
        var entries = EntriesOf(bundle, (name, data) => name == "System.Runtime.dll" ? new BundleEntry(name, data.Length, () => endless) : null);

        var result = Check(entries, bundle);

        Assert.Equal(BundleFault.LengthMismatch, result.Fault);
        Assert.Contains("System.Runtime.dll", result.Detail);
        Assert.Equal(length + 1L, endless.BytesRead);
    }

    [Fact]
    public void A_declared_total_over_the_limit_is_refused_before_a_byte_is_read()
    {
        var bundle = new TestBundle();
        var inner = bundle.InnerBytes().Length;
        var smallFiles = bundle.Files.Where(f => f.Key != "System.Runtime.dll").Sum(f => (long)f.Value.Length);
        var endless = new EndlessStream((byte)'r');
        var entries = EntriesOf(bundle, (name, data) => name == "System.Runtime.dll" ? new BundleEntry(name, data.Length, () => endless) : null);

        // Declared total (everything) is over the limit, so this is refused before anything is read: the stream is never touched.
        var limits = new Limits { TotalBytes = inner + smallFiles + 100 };
        var result = Check(entries, bundle, limits);

        Assert.Equal(BundleFault.TotalTooLarge, result.Fault);
        Assert.Equal(0, endless.BytesRead);
    }

    [Fact]
    public void An_inner_manifest_that_never_ends_is_stopped_at_the_manifest_limit()
    {
        var bundle = new TestBundle();
        var endless = new EndlessStream((byte)' ');
        var entries = EntriesOf(bundle, (name, data) => name == BundleFormat.InnerManifestName ? new BundleEntry(name, 10, () => endless) : null);

        var result = Check(entries, bundle, new Limits { ManifestBytes = 4096 });

        Assert.Equal(BundleFault.ManifestTooLarge, result.Fault);
        Assert.True(endless.BytesRead <= 4096 + 16 * 1024, $"{endless.BytesRead} bytes were read");
    }

    [Fact]
    public void An_inner_manifest_that_declares_itself_too_large_is_refused_without_a_read()
    {
        var bundle = new TestBundle();
        var opened = false;
        var entries = EntriesOf(bundle, (name, data) => name == BundleFormat.InnerManifestName
            ? new BundleEntry(name, 5000, () =>
            {
                opened = true;
                return new MemoryStream(data);
            })
            : null);

        var result = Check(entries, bundle, new Limits { ManifestBytes = 4096 });

        Assert.Equal(BundleFault.ManifestTooLarge, result.Fault);
        Assert.False(opened);
    }

    [Fact]
    public void An_inner_manifest_that_is_shorter_than_it_declared_is_a_length_mismatch()
    {
        var bundle = new TestBundle();
        var entries = EntriesOf(bundle, (name, data) => name == BundleFormat.InnerManifestName ? new BundleEntry(name, data.Length + 1, () => new MemoryStream(data)) : null);
        Assert.Equal(BundleFault.LengthMismatch, Check(entries, bundle).Fault);
    }

    [Fact]
    public void A_declared_length_that_is_not_the_manifests_is_refused_before_a_byte_is_read()
    {
        var bundle = new TestBundle();
        var opened = new List<string>();
        var entries = EntriesOf(bundle, (name, data) => name == "NOTICE.txt"
            ? new BundleEntry(name, data.Length + 1, () =>
            {
                opened.Add(name);
                return new MemoryStream(data);
            })
            : null);

        var result = Check(entries, bundle);

        Assert.Equal(BundleFault.LengthMismatch, result.Fault);
        Assert.Contains("NOTICE.txt", result.Detail);
        Assert.Empty(opened);
    }

    [Fact]
    public void A_file_that_is_shorter_than_the_manifest_says_is_a_length_mismatch()
    {
        var bundle = new TestBundle();
        var entries = EntriesOf(bundle, (name, data) => name == "NOTICE.txt" ? new BundleEntry(name, data.Length, () => new MemoryStream(data[..^1])) : null);

        var result = Check(entries, bundle);

        Assert.Equal(BundleFault.LengthMismatch, result.Fault);
        Assert.Contains("NOTICE.txt", result.Detail);
    }

    [Fact]
    public void A_file_with_the_right_length_and_other_bytes_is_a_hash_mismatch()
    {
        var bundle = new TestBundle();
        var entries = EntriesOf(bundle, (name, data) =>
        {
            if (name != "NOTICE.txt")
            {
                return null;
            }

            var other = (byte[])data.Clone();
            other[0] ^= 1;
            return new BundleEntry(name, other.Length, () => new MemoryStream(other));
        });

        var result = Check(entries, bundle);

        Assert.Equal(BundleFault.HashMismatch, result.Fault);
        Assert.Contains("NOTICE.txt", result.Detail);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidDataException))]
    public void A_read_error_is_a_refusal_with_the_name_and_not_an_exception(Type exceptionType)
    {
        var bundle = new TestBundle();
        var entries = EntriesOf(bundle, (name, data) => name == "NOTICE.txt"
            ? new BundleEntry(name, data.Length, () => throw (Exception)Activator.CreateInstance(exceptionType, "unreadable")!)
            : null);

        var result = Check(entries, bundle);

        Assert.Equal(BundleFault.ReadFailed, result.Fault);
        Assert.Contains("NOTICE.txt", result.Detail);
    }

    [Fact]
    public void A_read_error_in_the_inner_manifest_is_a_refusal_too()
    {
        var bundle = new TestBundle();
        var entries = EntriesOf(bundle, (name, data) => name == BundleFormat.InnerManifestName
            ? new BundleEntry(name, data.Length, () => throw new IOException("unreadable"))
            : null);

        Assert.Equal(BundleFault.ReadFailed, Check(entries, bundle).Fault);
    }

    [Fact]
    public void More_entries_than_the_limit_are_refused_first()
    {
        var bundle = new TestBundle();
        var entries = EntriesOf(bundle);
        Assert.True(Check(entries, bundle, new Limits { Files = entries.Count }).Ok);
        Assert.Equal(BundleFault.TooManyFiles, Check(entries, bundle, new Limits { Files = entries.Count - 1 }).Fault);
    }

    [Fact]
    public void The_floor_is_judged_before_the_inner_manifest_and_the_inner_manifest_has_its_own_fault()
    {
        var bundle = new TestBundle();
        var withoutInner = EntriesOf(bundle).Where(e => e.Name != BundleFormat.InnerManifestName).ToList();

        // With the real floor a missing file is named first; with an empty floor the manifest itself is what is missing.
        Assert.Equal(BundleFault.InnerManifestMissing, Check(withoutInner, bundle, floor: []).Fault);
        Assert.Equal(BundleFault.FloorMissing, Check(withoutInner.Where(e => e.Name != "pcmi.exe").ToList(), bundle).Fault);
    }

    [Fact]
    public void A_floor_name_is_found_whatever_its_case()
    {
        var bundle = new TestBundle();
        var upper = EntriesOf(bundle);
        Assert.True(Check(upper, bundle, floor: ["PCMIERU.CORE.DLL", "PCMI.EXE"]).Ok);
    }

    [Fact]
    public void An_inner_manifest_that_lists_itself_is_refused()
    {
        var bundle = new TestBundle();
        var lines = bundle.Files.Select(f => TestBundle.Line(f.Key, f.Value)).Append(TestBundle.Line(BundleFormat.InnerManifestName, [1]));
        var selfish = new BundleManifest(bundle.Version, bundle.BuildId, lines).ToUtf8Bytes();
        var entries = EntriesOf(bundle, (name, data) => name == BundleFormat.InnerManifestName ? new BundleEntry(name, selfish.Length, () => new MemoryStream(selfish)) : null);

        Assert.Equal(BundleFault.InnerFilesMismatch, Check(entries, bundle).Fault);
    }
}
