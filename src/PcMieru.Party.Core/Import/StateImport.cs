using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;
using PcMieru.Party.Game;

namespace PcMieru.Party.Import;

/// <summary>
/// Turns the bytes of an old Parts Life <c>state.json</c> into the bytes for PC Mieru's new place (DESIGN-C 4.1 and 4.2).
///
/// PURE: bytes in, verdict out. No file, registry, process, clock or environment access, and it never calls
/// <see cref="PartyState.Load(string, string?)"/> or <see cref="PartyState.Save"/> (those move and copy files).
/// Finding the file, reading it with the right sharing, checking that it did not change underneath, writing the result and
/// keeping the original are the caller's job; this only decides what the bytes mean.
///
/// What is refused (<see cref="ImportStatus.Hold"/>): more than <see cref="MaxBytes"/>; nesting deeper than
/// <see cref="MaxDepth"/>; broken JSON; a repeated key anywhere; a <c>SaveVersion</c> that is not an integer, is negative or is
/// newer than <see cref="PartyState.CurrentVersion"/>; Experience or Journey/Chapter/Scene missing or unreadable; a converted
/// state that would be larger than <see cref="MaxOutputBytes"/>.
///
/// What is converted: a source without <c>SaveVersion</c> (or 0) gets the old key migration (cpu, cpu-cooler, gpu become #0; if
/// the numbered name already exists it wins) and a default Adventure if it has none; a source with <c>SaveVersion</c> 1 is
/// carried as it is. Either way the output says <c>SaveVersion</c> 1. Unknown fields at the root and inside Adventure
/// round-trip untouched (JsonExtensionData), and so does a root field that only has the name of a runtime-only property
/// (<c>SaveBlocked</c>): it is kept as an unknown field and never bound to the runtime flag. Known fields that cannot be read
/// are replaced by defaults, and the repairs are applied to the output as well (see <see cref="StateNormalizer"/>); they are
/// listed in <see cref="ImportVerdict.RepairedFields"/> (status <see cref="ImportStatus.NeedsRepair"/>).
///
/// The output is compact JSON written in one pass into a buffer capped at <see cref="MaxOutputBytes"/>. It is not indented:
/// indentation makes deeply nested data many times larger than the source.
///
/// <c>UpdateMode</c> is DROPPED from the converted state (and never audited): it was Parts Life's own update setting, DESIGN-C 4.2
/// says it must not become a PC Mieru consent, and keeping a key of that name in the new file would invite exactly that mistake
/// (and PartyState would bind it straight back into its property). Nothing is lost: the caller keeps the original bytes
/// (see <see cref="ImportVerdict.SourceSha256"/>). It is listed in <see cref="ImportVerdict.DroppedFields"/>.
/// </summary>
public static class StateImport
{
    /// <summary>Upper limit of the source size (8 MiB). Larger is refused before it is parsed.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>Upper limit of the JSON nesting depth; the top-level object is depth 1.</summary>
    public const int MaxDepth = 64;

    /// <summary>Upper limit of the converted size (16 MiB). Escaping can make text larger than it was (a character outside the BMP: 4 bytes become 12).</summary>
    public const int MaxOutputBytes = 16 * 1024 * 1024;

    private const string UpdateModeKey = "UpdateMode";

    private static readonly string[] StoryFields = ["Journey", "Chapter", "Scene"];

    /// <summary>The state's own options, compact, and without UpdateMode (see the class comment).</summary>
    private static readonly JsonSerializerOptions OutputOptions = new(PartyState.Opts)
    {
        WriteIndented = false,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                static info =>
                {
                    if (info.Type != typeof(PartyState)) return;
                    foreach (var p in info.Properties)
                        if (p.Name == UpdateModeKey) p.ShouldSerialize = static (_, _) => false;
                },
            },
        },
    };

    /// <summary>Inspect the source and, unless it must be held, convert it. Never throws.</summary>
    public static ImportVerdict Inspect(ReadOnlySpan<byte> source) => Inspect(source, MaxOutputBytes);

    /// <summary>The same with a chosen output cap (tests use a small one).</summary>
    internal static ImportVerdict Inspect(ReadOnlySpan<byte> source, int maxOutputBytes)
    {
        string sha = Convert.ToHexStringLower(SHA256.HashData(source));
        try
        {
            return InspectCore(source, sha, maxOutputBytes);
        }
        catch (Exception e)
        {
            // Not expected (running out of memory included). A hold is the safe answer: nothing gets written.
            return Hold(sha, ImportReason.InternalError, e.GetType().Name);
        }
    }

    private static ImportVerdict InspectCore(ReadOnlySpan<byte> source, string sha, int maxOutputBytes)
    {
        if (source.Length > MaxBytes)
            return Hold(sha, ImportReason.TooLarge, $"{source.Length} bytes (limit {MaxBytes})");
        if (!Utf8.IsValid(source))
            return Hold(sha, ImportReason.BrokenJson, "not valid UTF-8");

        // A BOM is harmless in a file; the JSON reader would refuse it.
        ReadOnlySpan<byte> body = source.StartsWith<byte>([0xEF, 0xBB, 0xBF]) ? source[3..] : source;

        switch (JsonScan.Run(body, MaxDepth, out string scanDetail))
        {
            case ScanResult.TooDeep: return Hold(sha, ImportReason.TooDeep, scanDetail);
            case ScanResult.DuplicateKey: return Hold(sha, ImportReason.DuplicateKey, scanDetail);
            case ScanResult.Broken: return Hold(sha, ImportReason.BrokenJson, scanDetail);
        }

        using var doc = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = MaxDepth });
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return Hold(sha, ImportReason.NotAnObject, root.ValueKind.ToString());

        // ---- the version ----
        var versionProblem = ReadVersion(root, out int version, out string versionDetail);
        if (versionProblem is not null) return Hold(sha, versionProblem, versionDetail);

        // ---- the fields that make it "a continuation" ----
        var unreadable = UnreadableMainFields(root);
        if (unreadable.Count > 0)
            return Hold(sha, ImportReason.MainFieldsUnreadable, string.Join(", ", unreadable), version);

        try
        {
            // ---- convert ----
            var dropped = new List<string>();
            string text;
            if (root.TryGetProperty(UpdateModeKey, out _))
            {
                // Take it out before reading, so that even a malformed UpdateMode neither blocks nor flags the import.
                dropped.Add(UpdateModeKey);
                text = WithoutProperty(root, UpdateModeKey);
            }
            else
            {
                text = Encoding.UTF8.GetString(body);
            }

            var broken = new RepairSink();
            using (var doc2 = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = MaxDepth }))
                FieldAudit.AuditObject(doc2.RootElement, typeof(PartyState), "", PartyState.Opts, null, broken);

            PartyState state = PartyState.FromJson(text, out bool lenient);
            if (state.SaveVersion != PartyState.CurrentVersion)
                return Hold(sha, ImportReason.ConversionFailed, $"SaveVersion {state.SaveVersion} after migration", version);

            KeepIgnoredNames(state, root);
            StateNormalizer.Run(state);
            byte[] converted = Write(state, maxOutputBytes);

            if (lenient && broken.Count == 0)
                broken.Add("<unidentified>");   // the audit missed something the lenient reader had to fix: never call that a success
            return new ImportVerdict
            {
                Status = broken.Count == 0 ? ImportStatus.Acceptable : ImportStatus.NeedsRepair,
                SourceSha256 = sha,
                SourceVersion = version,
                RepairedFields = broken.ToList(),
                DroppedFields = dropped,
                Converted = converted,
            };
        }
        catch (Exception e) when (e is OutputTooLargeException || e.InnerException is OutputTooLargeException)
        {
            return Hold(sha, ImportReason.OutputTooLarge, $"the converted state would be larger than {maxOutputBytes} bytes", version);
        }
        catch (Exception e) when (LenientJson.IsReadError(e))
        {
            return Hold(sha, ImportReason.ConversionFailed, e.GetType().Name, version);
        }
    }

    /// <summary>
    /// The source object without one property, copied value by value (no tree is built, so nothing grows beyond the
    /// text itself).
    /// </summary>
    private static string WithoutProperty(JsonElement root, string name)
    {
        var buffer = new CappedBuffer(MaxBytes * 8);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            MaxDepth = MaxDepth,
        }))
        {
            w.WriteStartObject();
            foreach (var p in root.EnumerateObject())
                if (!p.NameEquals(name)) p.WriteTo(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.Written);
    }

    /// <summary>
    /// A root field whose name is that of a runtime-only property (<c>SaveBlocked</c>: [JsonIgnore], private setter) is never
    /// bound by the serializer, so it would vanish without a trace. Keep it as the unknown field it is.
    /// </summary>
    private static void KeepIgnoredNames(PartyState state, JsonElement root)
    {
        foreach (var p in typeof(PartyState).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.IsDefined(typeof(JsonIgnoreAttribute))) continue;
            if (!root.TryGetProperty(p.Name, out var value)) continue;
            (state.Unknown ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal))[p.Name] = value;
        }
    }

    /// <summary>The new destination's bytes: UTF-8 without BOM, compact PascalCase, no <c>UpdateMode</c>, at most <paramref name="limit"/> bytes.</summary>
    private static byte[] Write(PartyState state, int limit)
    {
        var buffer = new CappedBuffer(limit);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            MaxDepth = MaxDepth,
        }))
        {
            JsonSerializer.Serialize(w, state, OutputOptions);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// SaveVersion: absent means 0. Present must be an integer in 0..CurrentVersion.
    /// Digits are checked by hand: a number with millions of digits must not reach a big-integer parser.
    /// </summary>
    private static string? ReadVersion(JsonElement root, out int version, out string detail)
    {
        version = 0;
        detail = "";
        if (!root.TryGetProperty("SaveVersion", out var v)) return null;
        if (v.ValueKind != JsonValueKind.Number)
        {
            detail = "SaveVersion is " + v.ValueKind;
            return ImportReason.VersionType;
        }

        string raw = v.GetRawText();
        bool negative = raw.StartsWith('-');
        string digits = negative ? raw[1..] : raw;
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            detail = "SaveVersion is not an integer";   // 1.5, 1e0, ...
            return ImportReason.VersionType;
        }

        // 10+ digits does not fit an int: far beyond any version this program knows (or far below zero).
        bool huge = digits.Length > 9;
        int n = huge ? int.MaxValue : int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        if (negative && (huge || n != 0))
        {
            detail = "SaveVersion is negative";
            return ImportReason.VersionNegative;
        }
        if (n > PartyState.CurrentVersion)
        {
            detail = huge ? "SaveVersion is far above " + PartyState.CurrentVersion : $"SaveVersion {n} > {PartyState.CurrentVersion}";
            return ImportReason.VersionFuture;
        }
        version = n;
        return null;
    }

    /// <summary>
    /// Experience must be an object (an empty one is a fresh game, but if it has entries at least one must be a finite number),
    /// and Journey, Chapter and Scene must be finite numbers. A story position or a level table that reads as "zero" because it
    /// was unreadable would be a silent loss of the whole continuation, so these hold instead of repairing.
    /// </summary>
    private static List<string> UnreadableMainFields(JsonElement root)
    {
        var bad = new List<string>();

        if (!root.TryGetProperty("Experience", out var exp) || exp.ValueKind != JsonValueKind.Object)
            bad.Add("Experience");
        else
        {
            int total = 0, numeric = 0;
            foreach (var m in exp.EnumerateObject())
            {
                total++;
                if (m.Value.ValueKind == JsonValueKind.Number && m.Value.TryGetDouble(out double d) && double.IsFinite(d)) numeric++;
            }
            if (total > 0 && numeric == 0) bad.Add("Experience");
        }

        foreach (string name in StoryFields)
            if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number
                || !v.TryGetDouble(out double d) || !double.IsFinite(d))
                bad.Add(name);
        return bad;
    }

    private static ImportVerdict Hold(string sha, string reason, string detail, int? version = null) => new()
    {
        Status = ImportStatus.Hold,
        SourceSha256 = sha,
        Reason = reason,
        Detail = detail,
        SourceVersion = version,
    };
}
