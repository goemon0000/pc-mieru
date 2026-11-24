using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PcMieru.Update.Transaction;

/// <summary>One failure tag of blocked.json: a build that must not be tried again until the feed offers a different one (step 5 reads this).</summary>
public sealed record BlockedTag(string Version, string BuildId, string Reason, string Utc);

/// <summary>
/// <c>I\update\blocked.json</c> (DESIGN-B 5): the builds the rollback gave up on because they did not start (<see cref="TxTag.LaunchFailed"/>) or whose set did not hold (<see cref="TxTag.GenerationMismatch"/>).
/// <code>{ "format": "pcm-blocked/1", "tags": [ { "version": "0.5.1", "buildId": "...", "reason": "launch-failed", "utc": "2026-11-22T01:02:03Z" } ] }</code>
/// A pure swap failure (a sharing violation) writes no tag: it says nothing about the new build. The reader never throws: a missing or broken file is an empty list.
/// </summary>
public static class BlockedTags
{
    public const string Format = "pcm-blocked/1";
    public const int MaxTags = 32;

    public static IReadOnlyList<BlockedTag> Read(string path)
    {
        var tags = new List<BlockedTag>();
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > TxJson.MaxBytes)
            {
                return tags;
            }

            if (!TxJson.TryParseRoot(File.ReadAllBytes(path), out var document, out _))
            {
                return tags;
            }

            using (document)
            {
                if (!TxJson.TryGetString(document.RootElement, "format", out var format) || format != Format
                    || !document.RootElement.TryGetProperty("tags", out var array) || array.ValueKind != JsonValueKind.Array)
                {
                    return tags;
                }

                foreach (var element in array.EnumerateArray())
                {
                    if (TxJson.TryGetString(element, "version", out var version) && TxJson.TryGetString(element, "buildId", out var build)
                        && TxJson.TryGetString(element, "reason", out var reason) && TxJson.TryGetString(element, "utc", out var utc))
                    {
                        tags.Add(new BlockedTag(version, build, reason, utc));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            tags.Clear();
        }

        return tags;
    }

    /// <summary>The bytes of the file with <paramref name="added"/> in it (the same build and reason are kept once).</summary>
    internal static byte[] Bytes(IReadOnlyList<BlockedTag> existing, BlockedTag added)
    {
        var all = existing.Where(t => !(t.BuildId == added.BuildId && t.Reason == added.Reason)).Append(added).TakeLast(MaxTags).ToList();
        var text = new StringBuilder();
        text.Append("{\n  \"format\": ").Append(TxJson.Quote(Format)).Append(",\n  \"tags\": [");
        for (var i = 0; i < all.Count; i++)
        {
            var t = all[i];
            text.Append(i == 0 ? "\n" : ",\n").Append("    { \"version\": ").Append(TxJson.Quote(t.Version))
                .Append(", \"buildId\": ").Append(TxJson.Quote(t.BuildId))
                .Append(", \"reason\": ").Append(TxJson.Quote(t.Reason))
                .Append(", \"utc\": ").Append(TxJson.Quote(t.Utc)).Append(" }");
        }

        text.Append(all.Count == 0 ? "]\n}\n" : "\n  ]\n}\n");
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text.ToString());
    }
}

/// <summary>
/// <c>I\update\last-result.json</c>: what the settings page shows about the last transaction or recovery (the start gate does NOT read it). Written with a temporary file and a write-through rename.
/// <code>
/// {
///   "format": "pcm-last-result/1",
///   "utc": "2026-11-22T01:02:03Z",
///   "tx": "0123456789ab",            or null when no transaction id was made
///   "kind": "swap",                  "swap" (a transaction) | "recovery" (recover.exe, or the recovery at start)
///   "outcome": "rolledBack",         nothingToDo | committed | rolledBack | aborted | recoveryFailed
///   "reason": "launchFailed",        the camel-case name of TxReason ("none" when there is nothing to say)
///   "from": { "version": "0.5.0", "buildId": "..." },   or null
///   "to":   { "version": "0.5.1", "buildId": "..." },   or null
///   "detail": "English text for a log"
/// }
/// </code>
/// </summary>
public static class LastResult
{
    public const string Format = "pcm-last-result/1";

    /// <summary>Null when there is no result for this attempt, including an older result for the same build.</summary>
    internal static string? ReadAttemptReason(string path, string attemptId, string version, string buildId)
    {
        var bytes = TxEngine.ReadSmall(path, 65_536);
        if (bytes is null || !TxJson.TryParseRoot(bytes, out var doc, out _))
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!TxJson.TryGetString(root, "format", out var format) || format != Format
                || !TxJson.TryGetString(root, "tx", out var tx) || tx != attemptId
                || !root.TryGetProperty("to", out var to)
                || !TxJson.TryGetString(to, "version", out var toVersion) || toVersion != version
                || !TxJson.TryGetString(to, "buildId", out var toBuild) || toBuild != buildId)
            {
                return null;
            }

            return TxJson.TryGetString(root, "outcome", out var outcome) ? outcome switch
            {
                "busy" => "busy",
                "aborted" or "rolledBack" when TxJson.TryGetString(root, "reason", out var reason) => reason,
                _ => string.Empty,
            } : string.Empty;
        }
    }

    public static string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    internal static byte[] Bytes(DateTimeOffset utc, string kind, TxResult result)
    {
        var text = new StringBuilder();
        text.Append("{\n");
        text.Append("  \"format\": ").Append(TxJson.Quote(Format)).Append(",\n");
        text.Append("  \"utc\": ").Append(TxJson.Quote(utc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))).Append(",\n");
        text.Append("  \"tx\": ").Append(TxJson.QuoteOrNull(result.Tx)).Append(",\n");
        text.Append("  \"kind\": ").Append(TxJson.Quote(kind)).Append(",\n");
        text.Append("  \"outcome\": ").Append(TxJson.Quote(Camel(result.Outcome.ToString()))).Append(",\n");
        text.Append("  \"reason\": ").Append(TxJson.Quote(Camel(result.Reason.ToString()))).Append(",\n");
        text.Append("  \"from\": ").Append(Generation(result.FromVersion, result.FromBuildId)).Append(",\n");
        text.Append("  \"to\": ").Append(Generation(result.ToVersion, result.ToBuildId)).Append(",\n");
        text.Append("  \"detail\": ").Append(TxJson.Quote(result.Detail)).Append("\n}\n");
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text.ToString());
    }

    private static string Generation(string? version, string? buildId) =>
        version is null || buildId is null
            ? "null"
            : "{ \"version\": " + TxJson.Quote(version) + ", \"buildId\": " + TxJson.Quote(buildId) + " }";
}
