using System.Text;
using PcMieru.Update.Bundle;

namespace PcMieru.Update.Tests;

/// <summary>The pcm-bundle/1 manifest: the strict reader (each rejection) and the stable writer.</summary>
public class BundleManifestTests
{
    private static readonly string Sha = new('a', 64);
    private const string Guid1 = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";

    private static string File(string path = "pcmi.exe", string bytes = "3", string? sha = null) =>
        $"{{ \"path\": \"{path}\", \"bytes\": {bytes}, \"sha256\": \"{sha ?? Sha}\" }}";

    /// <summary>A manifest as JSON text. A parameter is the raw JSON of the value; null leaves the key out.</summary>
    private static string Json(
        string? format = "\"pcm-bundle/1\"",
        string? version = "\"0.5.0\"",
        string? rid = "\"win-x64\"",
        string? buildId = "\"" + Guid1 + "\"",
        string? gateProtocol = "1",
        string? files = null,
        string extra = "")
    {
        files ??= "[ " + File() + " ]";
        var parts = new List<string>();
        if (format is not null) parts.Add($"\"format\": {format}");
        if (version is not null) parts.Add($"\"version\": {version}");
        if (rid is not null) parts.Add($"\"rid\": {rid}");
        if (buildId is not null) parts.Add($"\"buildId\": {buildId}");
        if (gateProtocol is not null) parts.Add($"\"gateProtocol\": {gateProtocol}");
        if (files is not null) parts.Add($"\"files\": {files}");
        if (extra.Length > 0) parts.Add(extra);
        return "{ " + string.Join(", ", parts) + " }";
    }

    private static BundleVerdict Read(string json)
    {
        var ok = BundleManifest.TryParse(Encoding.UTF8.GetBytes(json), out var manifest, out var verdict);
        Assert.Equal(verdict.Ok, ok);
        Assert.Equal(ok, manifest is not null);
        return verdict;
    }

    private static void Refused(string json, BundleFault expected)
    {
        var verdict = Read(json);
        Assert.False(verdict.Ok, "the manifest should be refused: " + json);
        Assert.Equal(expected, verdict.Fault);
    }

    // ------------------------------------------------------------------------------------------------------------------------- reading

    [Fact]
    public void Reads_a_valid_manifest()
    {
        var json = Json(files: "[ " + File("pcmi.exe", "3") + ", " + File("ja/x.resources.dll", "0", new string('0', 64)) + " ]");
        Assert.True(BundleManifest.TryParse(Encoding.UTF8.GetBytes(json), out var manifest, out var verdict), verdict.ToString());
        Assert.Equal("pcm-bundle/1", manifest!.Format);
        Assert.Equal("0.5.0", manifest.Version);
        Assert.Equal("win-x64", manifest.Rid);
        Assert.Equal(Guid1, manifest.BuildId);
        Assert.Equal(1, manifest.GateProtocol);
        Assert.Equal(2, manifest.Files.Count);
        Assert.Equal(new BundleFile("pcmi.exe", 3, Sha), manifest.Files[0]);
        Assert.Equal(new BundleFile("ja/x.resources.dll", 0, new string('0', 64)), manifest.Files[1]);
    }

    [Fact]
    public void Reads_an_empty_file_list_and_any_key_order()
    {
        Assert.True(Read(Json(files: "[]")).Ok);
        Assert.True(Read("{ \"files\": [], \"gateProtocol\": 1, \"buildId\": \"" + Guid1 + "\", \"rid\": \"win-x64\", \"version\": \"0.5.0\", \"format\": \"pcm-bundle/1\" }").Ok);
    }

    [Fact]
    public void Skips_keys_it_does_not_know()
    {
        // A later version may add keys; the reader skips them, at the top and inside a file entry.
        var entry = "{ \"path\": \"pcmi.exe\", \"future\": [1, { \"a\": null }], \"bytes\": 3, \"sha256\": \"" + Sha + "\" }";
        var json = Json(files: "[ " + entry + " ]", extra: "\"signature\": { \"x\": [true, false, null] }, \"notes\": \"later\"");
        Assert.True(Read(json).Ok);
    }

    [Fact]
    public void Refuses_a_duplicate_key_anywhere()
    {
        Refused(Json(extra: "\"version\": \"0.5.0\""), BundleFault.DuplicateKey);
        Refused(Json(extra: "\"files\": []"), BundleFault.DuplicateKey);
        Refused(Json(extra: "\"unknown\": 1, \"unknown\": 2"), BundleFault.DuplicateKey);
        Refused(Json(files: "[ { \"path\": \"pcmi.exe\", \"path\": \"pcmi.exe\", \"bytes\": 3, \"sha256\": \"" + Sha + "\" } ]"), BundleFault.DuplicateKey);
        Refused(Json(files: "[ { \"path\": \"pcmi.exe\", \"bytes\": 3, \"bytes\": 3, \"sha256\": \"" + Sha + "\" } ]"), BundleFault.DuplicateKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("{")]
    [InlineData("{ \"format\": \"pcm-bundle/1\", }")]
    [InlineData("{ /* c */ \"format\": \"pcm-bundle/1\" }")]
    public void Refuses_what_is_not_a_json_object(string json) => Refused(json, BundleFault.ManifestSyntax);

    [Fact]
    public void Refuses_a_byte_order_mark()
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Json())).ToArray();
        Assert.False(BundleManifest.TryParse(bytes, out _, out var verdict));
        Assert.Equal(BundleFault.ManifestBom, verdict.Fault);
    }

    [Theory]
    [InlineData("format")]
    [InlineData("version")]
    [InlineData("rid")]
    [InlineData("buildId")]
    [InlineData("gateProtocol")]
    [InlineData("files")]
    public void Refuses_a_missing_key(string key)
    {
        var json = key switch
        {
            "format" => Json(format: null),
            "version" => Json(version: null),
            "rid" => Json(rid: null),
            "buildId" => Json(buildId: null),
            "gateProtocol" => Json(gateProtocol: null),
            _ => Json(files: null, extra: "\"x\": 1").Replace("\"files\": [ " + File() + " ], ", string.Empty).Replace("\"files\": [ " + File() + " ]", string.Empty),
        };
        Assert.DoesNotContain("\"" + key + "\"", json);
        Refused(json, BundleFault.ManifestMissingField);
    }

    [Theory]
    [InlineData("\"pcm-bundle/2\"")]
    [InlineData("\"PCM-BUNDLE/1\"")]
    [InlineData("\"pcm-bundle\"")]
    [InlineData("\"\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void Refuses_an_unknown_format(string format) => Refused(Json(format: format), BundleFault.UnknownFormat);

    [Theory]
    [InlineData("\"v0.5.0\"")]
    [InlineData("\"0.5\"")]
    [InlineData("\"0.5.0-beta\"")]
    [InlineData("\"0.5.0+x\"")]
    [InlineData("\"05.0.0\"")]
    [InlineData("\"0.5.0.0\"")]
    [InlineData("\" 0.5.0\"")]
    [InlineData("\"\"")]
    [InlineData("5")]
    [InlineData("null")]
    public void Refuses_a_version_that_is_not_plain(string version) => Refused(Json(version: version), BundleFault.BadVersion);

    [Theory]
    [InlineData("\"win-arm64\"")]
    [InlineData("\"WIN-X64\"")]
    [InlineData("\"linux-x64\"")]
    [InlineData("\"\"")]
    [InlineData("64")]
    public void Refuses_an_unknown_rid(string rid) => Refused(Json(rid: rid), BundleFault.UnknownRid);

    [Theory]
    [InlineData("\"0A1B2C3D-4E5F-4A6B-8C7D-9E0F1A2B3C4D\"")]
    [InlineData("\"0a1b2c3d4e5f4a6b8c7d9e0f1a2b3c4d\"")]
    [InlineData("\"{0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d}\"")]
    [InlineData("\"not-a-guid\"")]
    [InlineData("\"\"")]
    [InlineData("7")]
    public void Refuses_a_build_id_that_is_not_a_lower_case_guid(string buildId) => Refused(Json(buildId: buildId), BundleFault.BadBuildId);

    [Theory]
    [InlineData("2")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"1\"")]
    [InlineData("true")]
    [InlineData("null")]
    public void Refuses_an_unknown_gate_protocol(string gate) => Refused(Json(gateProtocol: gate), BundleFault.UnknownGateProtocol);

    [Theory]
    [InlineData("{}")]
    [InlineData("\"pcmi.exe\"")]
    [InlineData("null")]
    public void Refuses_files_that_is_not_an_array(string files) => Refused(Json(files: files), BundleFault.ManifestSyntax);

    [Theory]
    [InlineData("\"pcmi.exe\"")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{ \"bytes\": 3, \"sha256\": \"" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" + "\" }")]
    [InlineData("{ \"path\": 5, \"bytes\": 3, \"sha256\": \"" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" + "\" }")]
    [InlineData("{ \"path\": \"pcmi.exe\", \"sha256\": \"" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" + "\" }")]
    [InlineData("{ \"path\": \"pcmi.exe\", \"bytes\": \"3\", \"sha256\": \"" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" + "\" }")]
    [InlineData("{ \"path\": \"pcmi.exe\", \"bytes\": 3.5, \"sha256\": \"" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" + "\" }")]
    [InlineData("{ \"path\": \"pcmi.exe\", \"bytes\": 99999999999999999999, \"sha256\": \"" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" + "\" }")]
    public void Refuses_a_files_entry_of_the_wrong_shape(string entry) => Refused(Json(files: "[ " + entry + " ]"), BundleFault.BadFileEntry);

    [Theory]
    [InlineData("-1")]
    [InlineData("-99999999999999999999")]
    [InlineData("-1e30")]
    public void Refuses_a_negative_size(string bytes) => Refused(Json(files: "[ " + File(bytes: bytes) + " ]"), BundleFault.NegativeSize);

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // upper case
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 63
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65
    [InlineData("gaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // not hex
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Refuses_a_hash_that_is_not_64_lower_case_hex(string sha) =>
        Refused(Json(files: "[ " + File(sha: sha) + " ]"), BundleFault.BadHash);

    [Fact]
    public void Refuses_a_missing_or_wrongly_typed_hash()
    {
        Refused(Json(files: "[ { \"path\": \"pcmi.exe\", \"bytes\": 3 } ]"), BundleFault.BadHash);
        Refused(Json(files: "[ { \"path\": \"pcmi.exe\", \"bytes\": 3, \"sha256\": 5 } ]"), BundleFault.BadHash);
        Refused(Json(files: "[ { \"path\": \"pcmi.exe\", \"bytes\": 3, \"sha256\": null } ]"), BundleFault.BadHash);
    }

    [Theory]
    [InlineData("a\\\\b.dll", BundleFault.Backslash)]
    [InlineData("/pcmi.exe", BundleFault.LeadingSlash)]
    [InlineData("../x.dll", BundleFault.DotSegment)]
    [InlineData("a.dll:x", BundleFault.Colon)]
    [InlineData("NUL.dll", BundleFault.DeviceName)]
    [InlineData("a.", BundleFault.TrailingDotOrSpace)]
    [InlineData("sub/x.dll", BundleFault.NotAllowed)]
    [InlineData("x.bin", BundleFault.NotAllowed)]
    [InlineData("fr/x.resources.dll", BundleFault.NotAllowed)]
    public void Refuses_a_path_the_name_rules_refuse(string path, BundleFault expected) =>
        Refused(Json(files: "[ " + File(path) + " ]"), expected);

    [Fact]
    public void Refuses_a_duplicate_path_ignoring_case()
    {
        Refused(Json(files: "[ " + File("a.dll") + ", " + File("a.dll") + " ]"), BundleFault.DuplicatePath);
        Refused(Json(files: "[ " + File("A.dll") + ", " + File("a.dll") + " ]"), BundleFault.DuplicatePath);
        Refused(Json(files: "[ " + File("ja/X.resources.dll") + ", " + File("ja/x.resources.dll") + " ]"), BundleFault.DuplicatePath);
    }

    [Fact]
    public void Refuses_a_manifest_that_is_too_long()
    {
        var bytes = Encoding.UTF8.GetBytes(Json());
        using var stream = new MemoryStream(bytes);
        Assert.False(BundleManifest.TryRead(stream, bytes.Length - 1, out _, out var verdict));
        Assert.Equal(BundleFault.ManifestTooLarge, verdict.Fault);

        stream.Position = 0;
        Assert.True(BundleManifest.TryRead(stream, bytes.Length, out _, out _));

        var huge = new byte[BundleManifest.MaxBytes + 1];
        Assert.False(BundleManifest.TryParse(huge, out _, out var huge1));
        Assert.Equal(BundleFault.ManifestTooLarge, huge1.Fault);
    }

    // ------------------------------------------------------------------------------------------------------------------------- writing

    private static BundleManifest Sample(params BundleFile[] files) => new("0.5.0", Guid1, files);

    [Fact]
    public void Writes_a_fixed_shape()
    {
        var manifest = Sample(new BundleFile("pcmi.exe", 3, Sha), new BundleFile("PcMieru.Core.dll", 12, new string('b', 64)));
        var text = Encoding.UTF8.GetString(manifest.ToUtf8Bytes());
        var expected =
            "{\n" +
            "  \"format\": \"pcm-bundle/1\",\n" +
            "  \"version\": \"0.5.0\",\n" +
            "  \"rid\": \"win-x64\",\n" +
            "  \"buildId\": \"" + Guid1 + "\",\n" +
            "  \"gateProtocol\": 1,\n" +
            "  \"files\": [\n" +
            "    { \"path\": \"PcMieru.Core.dll\", \"bytes\": 12, \"sha256\": \"" + new string('b', 64) + "\" },\n" +
            "    { \"path\": \"pcmi.exe\", \"bytes\": 3, \"sha256\": \"" + Sha + "\" }\n" +
            "  ]\n" +
            "}\n";
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Writes_utf8_without_a_byte_order_mark_and_with_lf_only()
    {
        var bytes = Sample(new BundleFile("pcmi.exe", 3, Sha)).ToUtf8Bytes();
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal((byte)'{', bytes[0]);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.Equal((byte)'}', bytes[^2]);
    }

    [Fact]
    public void The_same_content_gives_the_same_bytes_whatever_the_order_it_came_in()
    {
        var a = new BundleFile("a.dll", 1, Sha);
        var b = new BundleFile("B.dll", 2, Sha);
        var c = new BundleFile("ja/x.resources.dll", 3, Sha);
        var first = Sample(a, b, c).ToUtf8Bytes();
        Assert.Equal(first, Sample(c, b, a).ToUtf8Bytes());
        Assert.Equal(first, Sample(b, c, a).ToUtf8Bytes());

        // Ordinal, not culture: "B.dll" (0x42) sorts before "a.dll" (0x61), and "ja/..." after both.
        var text = Encoding.UTF8.GetString(first);
        Assert.True(text.IndexOf("B.dll", StringComparison.Ordinal) < text.IndexOf("a.dll", StringComparison.Ordinal));
        Assert.True(text.IndexOf("a.dll", StringComparison.Ordinal) < text.IndexOf("ja/x.resources.dll", StringComparison.Ordinal));
    }

    [Fact]
    public void Writes_an_empty_list_and_reads_it_back()
    {
        var bytes = Sample().ToUtf8Bytes();
        Assert.Contains("\"files\": []", Encoding.UTF8.GetString(bytes));
        Assert.True(BundleManifest.TryParse(bytes, out var back, out _));
        Assert.Empty(back!.Files);
    }

    [Fact]
    public void What_it_writes_it_reads_back_and_writes_again_unchanged()
    {
        var manifest = Sample(new BundleFile("pcmi.exe", 3, Sha), new BundleFile("ja/x.resources.dll", 0, new string('0', 64)), new BundleFile("LICENSE", 1234567890123, new string('f', 64)));
        var bytes = manifest.ToUtf8Bytes();
        Assert.True(BundleManifest.TryParse(bytes, out var back, out var verdict), verdict.ToString());
        Assert.Equal(manifest.Version, back!.Version);
        Assert.Equal(manifest.BuildId, back.BuildId);
        Assert.Equal(manifest.Files.OrderBy(f => f.Path, StringComparer.Ordinal), back.Files.OrderBy(f => f.Path, StringComparer.Ordinal));
        Assert.Equal(bytes, back.ToUtf8Bytes());
    }

    [Fact]
    public void Escapes_what_json_needs_escaped()
    {
        // The reader refuses such a path, but the writer must still produce valid JSON for whatever it is given.
        var text = Encoding.UTF8.GetString(Sample(new BundleFile("a\"b\\c\u0001", 1, Sha)).ToUtf8Bytes());
        Assert.Contains("\"a\\\"b\\\\c\\u0001\"", text);
        using var document = System.Text.Json.JsonDocument.Parse(text);
        Assert.Equal("a\"b\\c\u0001", document.RootElement.GetProperty("files")[0].GetProperty("path").GetString());
    }

    [Fact]
    public void Reading_does_not_look_at_more_than_it_is_given_to_read()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Json()));
        Assert.True(BundleManifest.TryRead(stream, 4096, out var manifest, out _));
        Assert.Equal("0.5.0", manifest!.Version);
    }
}
