using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// newer than <see cref="PartyState.CurrentVersion"/>; Experience or Journey/Chapter/Scene missing or unreadable.
///
/// What is converted: a source without <c>SaveVersion</c> (or 0) gets the old key migration (cpu, cpu-cooler, gpu become #0; if
/// the numbered name already exists it wins) and a default Adventure if it has none; a source with <c>SaveVersion</c> 1 is
/// carried as it is. Either way the output says <c>SaveVersion</c> 1. Unknown fields at the root and inside Adventure
/// round-trip untouched (JsonExtensionData). Known fields that cannot be read are replaced by defaults and listed in
/// <see cref="ImportVerdict.RepairedFields"/> (status <see cref="ImportStatus.NeedsRepair"/>).
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

    private const string UpdateModeKey = "UpdateMode";

    private static readonly string[] StoryFields = ["Journey", "Chapter", "Scene"];

    /// <summary>Inspect the source and, unless it must be held, convert it. Never throws.</summary>
    public static ImportVerdict Inspect(ReadOnlySpan<byte> source)
    {
        string sha = Convert.ToHexStringLower(SHA256.HashData(source));
        try
        {
            return InspectCore(source, sha);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Not expected. A hold is the safe answer: nothing gets written.
            return Hold(sha, ImportReason.InternalError, e.GetType().Name);
        }
    }

    private static ImportVerdict InspectCore(ReadOnlySpan<byte> source, string sha)
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

        // ---- convert ----
        var dropped = new List<string>();
        string text;
        if (root.TryGetProperty(UpdateModeKey, out _))
        {
            // Take it out before reading, so that even a malformed UpdateMode neither blocks nor flags the import.
            dropped.Add(UpdateModeKey);
            var node = JsonNode.Parse(body.ToArray())!.AsObject();
            node.Remove(UpdateModeKey);
            text = node.ToJsonString();
        }
        else
        {
            text = Encoding.UTF8.GetString(body);
        }

        var broken = new List<string>();
        using (var doc2 = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = MaxDepth }))
            FieldAudit.AuditObject(doc2.RootElement, typeof(PartyState), "", PartyState.Opts, null, broken);

        PartyState state;
        bool lenient;
        try
        {
            state = PartyState.FromJson(text, out lenient);
            if (state.SaveVersion != PartyState.CurrentVersion)
                return Hold(sha, ImportReason.ConversionFailed, $"SaveVersion {state.SaveVersion} after migration", version);
            byte[] converted = Write(state);
            if (lenient && broken.Count == 0)
                broken.Add("<unidentified>");   // the audit missed something the lenient reader had to fix: never call that a success
            return new ImportVerdict
            {
                Status = broken.Count == 0 ? ImportStatus.Acceptable : ImportStatus.NeedsRepair,
                SourceSha256 = sha,
                SourceVersion = version,
                RepairedFields = broken,
                DroppedFields = dropped,
                Converted = converted,
            };
        }
        catch (Exception e) when (LenientJson.IsReadError(e))
        {
            return Hold(sha, ImportReason.ConversionFailed, e.GetType().Name, version);
        }
    }

    /// <summary>The new destination's bytes: UTF-8 without BOM, indented PascalCase, no <c>UpdateMode</c>.</summary>
    private static byte[] Write(PartyState state)
    {
        var node = JsonNode.Parse(state.ToJson())!.AsObject();
        node.Remove(UpdateModeKey);
        return new UTF8Encoding(false).GetBytes(node.ToJsonString(OutputOptions));
    }

    /// <summary>The state's own options, with LF line ends so that the output does not depend on the platform (Environment.NewLine).</summary>
    private static readonly JsonSerializerOptions OutputOptions = new(PartyState.Opts) { NewLine = "\n" };

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
    /// Experience must be an object (an empty one is a fresh game, but if it has entries at least one must be a number), and
    /// Journey, Chapter and Scene must be numbers. A story position or a level table that reads as "zero" because it was
    /// unreadable would be a silent loss of the whole continuation, so these hold instead of repairing.
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
                if (m.Value.ValueKind == JsonValueKind.Number && m.Value.TryGetDouble(out _)) numeric++;
            }
            if (total > 0 && numeric == 0) bad.Add("Experience");
        }

        foreach (string name in StoryFields)
            if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out _))
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
