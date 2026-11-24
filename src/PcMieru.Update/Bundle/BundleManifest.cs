using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PcMieru.Update.Feed;

namespace PcMieru.Update.Bundle;

/// <summary>One file of a bundle: its relative path (as the manifest spells it), its size and the SHA-256 of its content (64 lower-case hex digits).</summary>
public sealed record BundleFile(string Path, long Bytes, string Sha256);

/// <summary>
/// A <c>pcm-bundle/1</c> manifest: <c>{format, version, rid, buildId, gateProtocol, files:[{path, bytes, sha256}]}</c> (DESIGN-B 3.1).
/// The folder carries one as PCMieru.bundle.json (it lists every file but itself); the release carries an outer one next to the zip (it lists every
/// file including the inner manifest). <c>buildId</c> is a GUID chosen at every publish and lives in the manifest only (DESIGN-B 13 item 4).
///
/// <see cref="TryParse"/> is strict: it skips keys it does not know (so a later version may add some), but refuses a duplicate key, an unknown format,
/// rid or gate protocol, a path the name rules refuse, a duplicate path (ignoring case), a missing or malformed hash and a negative size.
/// <see cref="ToUtf8Bytes"/> is stable: the same content gives the same bytes (UTF-8 without a BOM, LF, files in ordinal order of the path).
/// </summary>
public sealed class BundleManifest
{
    public BundleManifest(string version, string buildId, IEnumerable<BundleFile> files)
    {
        Version = version ?? throw new ArgumentNullException(nameof(version));
        BuildId = buildId ?? throw new ArgumentNullException(nameof(buildId));
        Files = (files ?? throw new ArgumentNullException(nameof(files))).ToArray();
    }

    /// <summary>Always <see cref="BundleFormat.Format"/>.</summary>
    public string Format => BundleFormat.Format;

    /// <summary>"X.Y.Z" (no "v").</summary>
    public string Version { get; }

    /// <summary>Always <see cref="BundleFormat.Rid"/>.</summary>
    public string Rid => BundleFormat.Rid;

    /// <summary>A lower-case GUID, new at every publish.</summary>
    public string BuildId { get; }

    /// <summary>Always <see cref="BundleFormat.GateProtocol"/>.</summary>
    public int GateProtocol => BundleFormat.GateProtocol;

    /// <summary>The files in the order they were read (the writer sorts).</summary>
    public IReadOnlyList<BundleFile> Files { get; }

    /// <summary>The longest manifest <see cref="TryParse"/> will look at (the 2 MiB of <see cref="Limits"/>). A caller with a tighter limit checks first.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    // ---------------------------------------------------------------------------------------------------------------------- write

    /// <summary>
    /// The manifest as bytes: UTF-8 without a byte order mark, LF line ends, a fixed key order, one line per file, files in ordinal order of the path.
    /// tools/publish.ps1 writes the same shape by hand; the real-machine test compares the two.
    /// </summary>
    public byte[] ToUtf8Bytes()
    {
        var text = new StringBuilder();
        text.Append("{\n");
        text.Append("  \"format\": ").Append(Quote(Format)).Append(",\n");
        text.Append("  \"version\": ").Append(Quote(Version)).Append(",\n");
        text.Append("  \"rid\": ").Append(Quote(Rid)).Append(",\n");
        text.Append("  \"buildId\": ").Append(Quote(BuildId)).Append(",\n");
        text.Append("  \"gateProtocol\": ").Append(GateProtocol.ToString(CultureInfo.InvariantCulture)).Append(",\n");

        var ordered = Files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0)
        {
            text.Append("  \"files\": []\n");
        }
        else
        {
            text.Append("  \"files\": [\n");
            for (var i = 0; i < ordered.Length; i++)
            {
                var f = ordered[i];
                text.Append("    { \"path\": ").Append(Quote(f.Path))
                    .Append(", \"bytes\": ").Append(f.Bytes.ToString(CultureInfo.InvariantCulture))
                    .Append(", \"sha256\": ").Append(Quote(f.Sha256))
                    .Append(" }").Append(i + 1 < ordered.Length ? ",\n" : "\n");
            }

            text.Append("  ]\n");
        }

        text.Append("}\n");
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text.ToString());
    }

    private static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case < ' ': sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: sb.Append(c); break;
            }
        }

        return sb.Append('"').ToString();
    }

    // ----------------------------------------------------------------------------------------------------------------------- read

    /// <summary>Reads a manifest from a stream, looking at no more than <paramref name="maxBytes"/> (a longer one is <see cref="BundleFault.ManifestTooLarge"/>).</summary>
    public static bool TryRead(Stream stream, int maxBytes, [NotNullWhen(true)] out BundleManifest? manifest, out BundleVerdict verdict)
    {
        manifest = null;
        var bytes = ReadAtMost(stream, maxBytes, out var tooLong);
        if (tooLong)
        {
            verdict = BundleVerdict.Refuse(BundleFault.ManifestTooLarge, $"the manifest is longer than {maxBytes} bytes");
            return false;
        }

        return TryParse(bytes, out manifest, out verdict);
    }

    /// <summary>Reads at most <paramref name="maxBytes"/> bytes; <paramref name="tooLong"/> says the stream had more.</summary>
    internal static byte[] ReadAtMost(Stream stream, int maxBytes, out bool tooLong)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        long total = 0;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                tooLong = true;
                return [];
            }

            buffer.Write(chunk, 0, read);
        }

        tooLong = false;
        return buffer.ToArray();
    }

    /// <summary>The strict reader. <paramref name="utf8Json"/> is the file as it is (a BOM is refused).</summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8Json, [NotNullWhen(true)] out BundleManifest? manifest, out BundleVerdict verdict)
    {
        manifest = null;
        if (utf8Json.Length > MaxBytes)
        {
            verdict = BundleVerdict.Refuse(BundleFault.ManifestTooLarge, $"the manifest is longer than {MaxBytes} bytes");
            return false;
        }

        if (utf8Json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            verdict = BundleVerdict.Refuse(BundleFault.ManifestBom, "the manifest starts with a byte order mark (UTF-8 without BOM is the form)");
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions
            {
                AllowDuplicateProperties = false,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        }
        catch (JsonException ex)
        {
            // Told apart from plain bad JSON: the same bytes read with duplicates allowed are valid JSON.
            verdict = DuplicatesOnly(utf8Json)
                ? BundleVerdict.Refuse(BundleFault.DuplicateKey, "a JSON key appears twice in one object")
                : BundleVerdict.Refuse(BundleFault.ManifestSyntax, "not valid JSON: " + ex.Message);
            return false;
        }
        catch (Exception ex) when (IsUnreadableText(ex))
        {
            verdict = BundleVerdict.Refuse(BundleFault.ManifestSyntax, "not valid JSON: " + ex.Message);
            return false;
        }

        using (document)
        {
            try
            {
                return Interpret(document.RootElement, out manifest, out verdict);
            }
            catch (Exception ex) when (IsUnreadableText(ex))
            {
                // Well-formed JSON can still hold text that is not text: "\ud800" (half a surrogate pair) in a key or a value parses, and throws only when the
                // name or the string is turned into a .NET string (InvalidOperationException; FormatException for the typed getters). That is a syntax fault.
                manifest = null;
                verdict = BundleVerdict.Refuse(BundleFault.ManifestSyntax, "not valid JSON: a string in the manifest is not valid text (" + ex.Message + ")");
                return false;
            }
        }
    }

    private static bool IsUnreadableText(Exception ex) => ex is InvalidOperationException or FormatException;

    private static bool DuplicatesOnly(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using var _ = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions { AllowDuplicateProperties = true, MaxDepth = 16 });
            return true;
        }
        catch (Exception ex) when (ex is JsonException || IsUnreadableText(ex))
        {
            return false;
        }
    }

    private static bool Interpret(JsonElement root, [NotNullWhen(true)] out BundleManifest? manifest, out BundleVerdict verdict)
    {
        manifest = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            verdict = BundleVerdict.Refuse(BundleFault.ManifestSyntax, "the manifest is not a JSON object");
            return false;
        }

        // Unknown keys are skipped; the six known ones are looked up by name (duplicates are already refused by the parser).
        JsonElement? format = null, version = null, rid = null, buildId = null, gateProtocol = null, files = null;
        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "format": format = property.Value; break;
                case "version": version = property.Value; break;
                case "rid": rid = property.Value; break;
                case "buildId": buildId = property.Value; break;
                case "gateProtocol": gateProtocol = property.Value; break;
                case "files": files = property.Value; break;
            }
        }

        foreach (var (key, value) in new[] { ("format", format), ("version", version), ("rid", rid), ("buildId", buildId), ("gateProtocol", gateProtocol), ("files", files) })
        {
            if (value is null)
            {
                verdict = BundleVerdict.Refuse(BundleFault.ManifestMissingField, $"the key '{key}' is missing");
                return false;
            }
        }

        if (format!.Value.ValueKind != JsonValueKind.String || format.Value.GetString() != BundleFormat.Format)
        {
            verdict = BundleVerdict.Refuse(BundleFault.UnknownFormat, $"format is not '{BundleFormat.Format}'");
            return false;
        }

        if (version!.Value.ValueKind != JsonValueKind.String || !IsPlainVersion(version.Value.GetString(), out var versionText))
        {
            verdict = BundleVerdict.Refuse(BundleFault.BadVersion, "version is not a plain X.Y.Z");
            return false;
        }

        if (rid!.Value.ValueKind != JsonValueKind.String || rid.Value.GetString() != BundleFormat.Rid)
        {
            verdict = BundleVerdict.Refuse(BundleFault.UnknownRid, $"rid is not '{BundleFormat.Rid}'");
            return false;
        }

        if (buildId!.Value.ValueKind != JsonValueKind.String || !IsLowerGuid(buildId.Value.GetString()))
        {
            verdict = BundleVerdict.Refuse(BundleFault.BadBuildId, "buildId is not a lower-case GUID");
            return false;
        }

        if (gateProtocol!.Value.ValueKind != JsonValueKind.Number
            || !gateProtocol.Value.TryGetInt32(out var gate)
            || gate != BundleFormat.GateProtocol)
        {
            verdict = BundleVerdict.Refuse(BundleFault.UnknownGateProtocol, $"gateProtocol is not the integer {BundleFormat.GateProtocol}");
            return false;
        }

        if (files!.Value.ValueKind != JsonValueKind.Array)
        {
            verdict = BundleVerdict.Refuse(BundleFault.ManifestSyntax, "files is not an array");
            return false;
        }

        var list = new List<BundleFile>(files.Value.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in files.Value.EnumerateArray())
        {
            if (!TryReadFile(element, out var file, out verdict))
            {
                return false;
            }

            if (!seen.Add(file.Path))
            {
                verdict = BundleVerdict.Refuse(BundleFault.DuplicatePath, $"'{file.Path}' is listed twice (ignoring case)");
                return false;
            }

            list.Add(file);
        }

        manifest = new BundleManifest(versionText, buildId.Value.GetString()!, list);
        verdict = BundleVerdict.Pass;
        return true;
    }

    private static bool TryReadFile(JsonElement element, [NotNullWhen(true)] out BundleFile? file, out BundleVerdict verdict)
    {
        file = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            verdict = BundleVerdict.Refuse(BundleFault.BadFileEntry, "a files entry is not an object");
            return false;
        }

        string? path = null, sha256 = null;
        long? bytes = null;
        var pathWrongType = false;
        var bytesNegative = false;
        var bytesWrongType = false;
        var shaWrongType = false;
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "path":
                    if (property.Value.ValueKind == JsonValueKind.String) path = property.Value.GetString();
                    else pathWrongType = true;
                    break;
                case "bytes":
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var n))
                    {
                        if (n < 0) bytesNegative = true;
                        else bytes = n;
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Number && IsNegativeNumber(property.Value))
                    {
                        bytesNegative = true;
                    }
                    else bytesWrongType = true;
                    break;
                case "sha256":
                    if (property.Value.ValueKind == JsonValueKind.String) sha256 = property.Value.GetString();
                    else shaWrongType = true;
                    break;
            }
        }

        if (path is null || pathWrongType)
        {
            verdict = BundleVerdict.Refuse(BundleFault.BadFileEntry, "a files entry has no string 'path'");
            return false;
        }

        if (bytesNegative)
        {
            verdict = BundleVerdict.Refuse(BundleFault.NegativeSize, $"'{path}' has a negative size");
            return false;
        }

        if (bytes is null || bytesWrongType)
        {
            verdict = BundleVerdict.Refuse(BundleFault.BadFileEntry, $"'{path}' has no integer 'bytes'");
            return false;
        }

        if (sha256 is null || shaWrongType || !IsLowerHex64(sha256))
        {
            verdict = BundleVerdict.Refuse(BundleFault.BadHash, $"'{path}' has no 'sha256' of 64 lower-case hex digits");
            return false;
        }

        var name = BundleNames.Check(path);
        if (!name.Ok)
        {
            verdict = name;
            return false;
        }

        file = new BundleFile(path, bytes.Value, sha256);
        verdict = BundleVerdict.Pass;
        return true;
    }

    /// <summary>A number the parser cannot give as a long that is still negative ("-1e30", "-99999999999999999999"): a size below zero, not a type error.</summary>
    private static bool IsNegativeNumber(JsonElement number) => number.GetRawText().StartsWith('-');

    /// <summary>"X.Y.Z" and nothing else: the text is read as a version and must print back as the same text (no "v", no leading zero).</summary>
    private static bool IsPlainVersion(string? text, [NotNullWhen(true)] out string? plain)
    {
        plain = null;
        if (text is null || !AppVersion.TryParse(text, out var parsed) || parsed.ToString() != text)
        {
            return false;
        }

        plain = text;
        return true;
    }

    /// <summary>A GUID in its 36-character form with lower-case hex digits.</summary>
    internal static bool IsLowerGuid(string? text) =>
        text is { Length: 36 } && Guid.TryParseExact(text, "D", out _) && text == text.ToLowerInvariant();

    /// <summary>Exactly 64 lower-case hex digits (the manifest's spelling; <see cref="Sha256List.IsHex64"/> also takes upper case).</summary>
    internal static bool IsLowerHex64(string text)
    {
        if (text.Length != 64)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
