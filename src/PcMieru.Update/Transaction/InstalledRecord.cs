using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PcMieru.Update.Bundle;
using PcMieru.Update.Feed;

namespace PcMieru.Update.Transaction;

/// <summary>One file of an installed generation: its name relative to the install folder (with "/"), its length and its SHA-256 (64 lower-case hex digits).</summary>
public sealed record InstalledFile(string Path, long Bytes, string Sha256);

/// <summary>
/// What is installed: <c>I\update\installed.json</c> (DESIGN-B 4, 5). The version, the buildId and every file of the generation with its length and hash, <b>including the inner manifest
/// PCMieru.bundle.json and the gate file</b> (the folder's manifest does not list itself, so the record adds its line). It is the ONLY basis for deleting or displacing a file: a file that is not in the record
/// belongs to the user and is never touched, and a file that is in the record is touched only while its real hash is the recorded one.
///
/// <code>
/// {
///   "format": "pcm-installed/1",
///   "version": "0.5.0",
///   "buildId": "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d",
///   "files": [
///     { "path": "PCMieru.exe", "bytes": 123, "sha256": "..." }
///   ]
/// }
/// </code>
/// UTF-8 without a byte order mark, LF line ends, files in ordinal order of the path (the same writer shape as the bundle manifest), so the same content is the same bytes and <see cref="Sha256"/> is stable.
/// </summary>
public sealed class InstalledRecord
{
    public const string Format = "pcm-installed/1";

    private readonly Dictionary<string, InstalledFile> _byName;
    private byte[]? _bytes;

    public InstalledRecord(string version, string buildId, IEnumerable<InstalledFile> files)
    {
        Version = version ?? throw new ArgumentNullException(nameof(version));
        BuildId = buildId ?? throw new ArgumentNullException(nameof(buildId));
        _byName = new Dictionary<string, InstalledFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files ?? throw new ArgumentNullException(nameof(files)))
        {
            if (!_byName.TryAdd(file.Path, file))
            {
                throw new ArgumentException($"'{file.Path}' is listed twice (ignoring case)", nameof(files));
            }
        }

        Files = _byName.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
    }

    /// <summary>"X.Y.Z" (no "v").</summary>
    public string Version { get; }

    /// <summary>A lower-case GUID: the buildId of the bundle manifest this generation came from.</summary>
    public string BuildId { get; }

    /// <summary>Every file, in ordinal order of the path.</summary>
    public IReadOnlyList<InstalledFile> Files { get; }

    /// <summary>The line of that name (compared without regard to case, as the file system does), or false.</summary>
    public bool TryGet(string path, [NotNullWhen(true)] out InstalledFile? file) => _byName.TryGetValue(path, out file);

    /// <summary>The record as bytes (canonical, see the type's summary).</summary>
    public byte[] ToUtf8Bytes()
    {
        if (_bytes is not null)
        {
            return _bytes;
        }

        var text = new StringBuilder();
        text.Append("{\n");
        text.Append("  \"format\": ").Append(TxJson.Quote(Format)).Append(",\n");
        text.Append("  \"version\": ").Append(TxJson.Quote(Version)).Append(",\n");
        text.Append("  \"buildId\": ").Append(TxJson.Quote(BuildId)).Append(",\n");
        TxJson.AppendFiles(text, "  ", Files);
        text.Append("\n}\n");
        _bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text.ToString());
        return _bytes;
    }

    /// <summary>The SHA-256 of <see cref="ToUtf8Bytes"/> (lower-case hex): what the real installed.json must hash to.</summary>
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(ToUtf8Bytes()));

    /// <summary>The record of a bundle: the files of its inner manifest and the line of the inner manifest itself (<paramref name="innerManifestBytes"/> is the file as it is).</summary>
    public static InstalledRecord FromBundle(BundleManifest inner, ReadOnlySpan<byte> innerManifestBytes)
    {
        var files = inner.Files.Select(f => new InstalledFile(f.Path, f.Bytes, f.Sha256)).ToList();
        files.Add(new InstalledFile(BundleFormat.InnerManifestName, innerManifestBytes.Length, Convert.ToHexStringLower(SHA256.HashData(innerManifestBytes))));
        return new InstalledRecord(inner.Version, inner.BuildId, files);
    }

    /// <summary>The strict reader: an unknown format, a bad version, buildId, name or hash, a duplicate key or path, a BOM, or anything that is not JSON is refused with a reason (English, for a log).</summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8Json, [NotNullWhen(true)] out InstalledRecord? record, out string error)
    {
        record = null;
        if (!TxJson.TryParseRoot(utf8Json, out var document, out error))
        {
            return false;
        }

        using (document)
        {
            return TryInterpret(document.RootElement, out record, out error);
        }
    }

    internal static bool TryInterpret(JsonElement root, [NotNullWhen(true)] out InstalledRecord? record, out string error)
    {
        record = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "not a JSON object";
            return false;
        }

        if (!TxJson.TryGetString(root, "format", out var format) || format != Format)
        {
            error = $"format is not '{Format}'";
            return false;
        }

        if (!TxJson.TryReadGeneration(root, out var version, out var buildId, out var files, out error))
        {
            return false;
        }

        try
        {
            record = new InstalledRecord(version, buildId, files);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }

        error = string.Empty;
        return true;
    }
}

/// <summary>Reading and writing the small JSON of the transaction without reflection (the swapper is NativeAOT) and with the same strictness as the bundle manifest.</summary>
internal static class TxJson
{
    private static readonly JsonDocumentOptions Strict = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };

    /// <summary>The most a journal or a record may be (a generation of a few hundred files is well under a megabyte).</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    public static bool TryParseRoot(ReadOnlySpan<byte> utf8Json, out JsonDocument document, out string error)
    {
        document = null!;
        if (utf8Json.Length == 0)
        {
            error = "empty";
            return false;
        }

        if (utf8Json.Length > MaxBytes)
        {
            error = $"longer than {MaxBytes} bytes";
            return false;
        }

        if (utf8Json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            error = "starts with a byte order mark";
            return false;
        }

        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray(), Strict);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            error = "not valid JSON: " + ex.Message;
            return false;
        }
    }

    public static bool TryGetString(JsonElement obj, string name, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            try
            {
                value = element.GetString();
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                return false;
            }
        }

        return value is not null;
    }

    public static bool TryGetInt(JsonElement obj, string name, out int value)
    {
        value = 0;
        return obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }

    /// <summary>Reads "version", "buildId" and "files" of an object (the shape of an installed record, and of the from/to of a journal).</summary>
    public static bool TryReadGeneration(JsonElement obj, out string version, out string buildId, out List<InstalledFile> files, out string error)
    {
        version = buildId = string.Empty;
        files = [];
        if (!TryGetString(obj, "version", out var v) || !AppVersion.TryParse(v, out var parsed) || parsed.ToString() != v)
        {
            error = "version is not a plain X.Y.Z";
            return false;
        }

        if (!TryGetString(obj, "buildId", out var b) || !BundleManifest.IsLowerGuid(b))
        {
            error = "buildId is not a lower-case GUID";
            return false;
        }

        if (!obj.TryGetProperty("files", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            error = "files is not an array";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in array.EnumerateArray())
        {
            if (!TryReadFile(element, out var file, out error))
            {
                return false;
            }

            if (!seen.Add(file.Path))
            {
                error = $"'{file.Path}' is listed twice (ignoring case)";
                return false;
            }

            files.Add(file);
        }

        version = v;
        buildId = b;
        error = string.Empty;
        return true;
    }

    private static bool TryReadFile(JsonElement element, [NotNullWhen(true)] out InstalledFile? file, out string error)
    {
        file = null;
        if (element.ValueKind != JsonValueKind.Object
            || !TryGetString(element, "path", out var path)
            || !element.TryGetProperty("bytes", out var bytesElement) || bytesElement.ValueKind != JsonValueKind.Number || !bytesElement.TryGetInt64(out var bytes) || bytes < 0
            || !TryGetString(element, "sha256", out var sha) || !BundleManifest.IsLowerHex64(sha))
        {
            error = "a files entry has no path, no non-negative integer bytes or no sha256 of 64 lower-case hex digits";
            return false;
        }

        var verdict = BundleNames.Check(path);
        if (!verdict.Ok)
        {
            error = $"the name '{BundleNames.Show(path)}' is refused: {verdict.Detail}";
            return false;
        }

        file = new InstalledFile(path, bytes, sha);
        error = string.Empty;
        return true;
    }

    // ---------------------------------------------------------------------------------------------------------------------- write

    public static string Quote(string value)
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

    public static string QuoteOrNull(string? value) => value is null ? "null" : Quote(value);

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Appends <c>"files": [ ... ]</c> (no trailing newline), one line per file.</summary>
    public static void AppendFiles(StringBuilder text, string indent, IReadOnlyList<InstalledFile> files)
    {
        if (files.Count == 0)
        {
            text.Append(indent).Append("\"files\": []");
            return;
        }

        text.Append(indent).Append("\"files\": [\n");
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            text.Append(indent).Append("  { \"path\": ").Append(Quote(f.Path))
                .Append(", \"bytes\": ").Append(Number(f.Bytes))
                .Append(", \"sha256\": ").Append(Quote(f.Sha256))
                .Append(" }").Append(i + 1 < files.Count ? ",\n" : "\n");
        }

        text.Append(indent).Append(']');
    }
}
