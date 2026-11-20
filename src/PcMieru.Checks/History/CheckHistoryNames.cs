using System.Globalization;
using System.Text.RegularExpressions;

namespace PcMieru.Checks;

/// <summary>A history file name taken apart: <c>kind-stamp-id.json</c>, all three from the name only (the body is not read).</summary>
internal readonly record struct HistoryFileName(string Name, string Kind, string Stamp, string Id);

/// <summary>
/// The file names of the history folder (DESIGN-A §4). One place for the three shapes:
/// <list type="bullet">
/// <item><c>&lt;kind&gt;-&lt;UTC&gt;-&lt;runId:N&gt;.json</c>: a committed result. UTC is <see cref="CheckTime.FileStamp"/> of the end time.</item>
/// <item><c>&lt;that name&gt;.pcmtmp-&lt;pid&gt;-&lt;nonce&gt;.tmp</c>: a write in flight. It never matches the first shape, so no listing sees it.</item>
/// <item><c>&lt;that name&gt;.bad-&lt;UTC&gt;[-n]</c>: a file moved aside. It matches neither of the others, so nothing reads, counts or deletes it.</item>
/// </list>
/// Matching is on the whole name, lowercase only, ordinal: a name that is not exactly one of these is not ours and is left alone.
/// </summary>
internal static class CheckHistoryNames
{
    private const string KindPart = "[a-z][a-z0-9_]*";
    private const string StampPart = "[0-9]{8}T[0-9]{9}Z";
    private const string IdPart = "[0-9a-f]{32}";
    private static readonly Regex Final = new(
        "^(?<kind>" + KindPart + ")-(?<stamp>" + StampPart + ")-(?<id>" + IdPart + @")\.json\z",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    private static readonly Regex Temp = new(
        "^(?<final>(?<kind>" + KindPart + ")-(?<stamp>" + StampPart + ")-(?<id>" + IdPart + @")\.json)\.pcmtmp-[0-9]{1,10}-[0-9a-f]{8}\.tmp\z",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    public static bool TryParse(string name, out HistoryFileName parsed)
    {
        var match = Final.Match(name);
        if (match.Success)
        {
            parsed = new HistoryFileName(name, match.Groups["kind"].Value, match.Groups["stamp"].Value, match.Groups["id"].Value);
            return true;
        }

        parsed = default;
        return false;
    }

    /// <summary>The committed name of a result. The time is cut to whole milliseconds like the JSON time, so the two always agree.</summary>
    public static string Format(CheckKind kind, DateTimeOffset endedAtUtc, Guid runId) =>
        string.Create(CultureInfo.InvariantCulture, $"{SnakeCaseEnum<CheckKind>.Name(kind)}-{CheckTime.FileStamp(endedAtUtc)}-{runId:N}.json");

    public static string TempName(string finalName, int processId, string nonce) =>
        string.Create(CultureInfo.InvariantCulture, $"{finalName}.pcmtmp-{processId}-{nonce}.tmp");

    /// <summary>
    /// True for a name of the shape this saver makes for a write in flight, and nothing else: exactly the shape above, with a kind
    /// this version knows (a leftover of a newer version's saver is not ours to judge). This is the first half of the rule for
    /// cleaning (the second half is the age, see <see cref="CheckHistoryStore"/>). The pid in the name is not looked at: the rule is
    /// "an abandoned temporary file" by shape and age, not "a file this process owns". A foreign .tmp file never matches.
    /// <paramref name="final"/> is the committed name the temporary file was written for (kind, stamp and id taken from the name).
    /// </summary>
    public static bool TryParseTemp(string name, out HistoryFileName final)
    {
        var match = Temp.Match(name);
        if (match.Success && SnakeCaseEnum<CheckKind>.TryParse(match.Groups["kind"].Value, out _))
        {
            final = new HistoryFileName(match.Groups["final"].Value, match.Groups["kind"].Value, match.Groups["stamp"].Value, match.Groups["id"].Value);
            return true;
        }

        final = default;
        return false;
    }

    /// <summary>True when <paramref name="name"/> has the shape of a write in flight (see <see cref="TryParseTemp"/>).</summary>
    public static bool IsOwnTemp(string name) => TryParseTemp(name, out _);

    /// <summary>The name a corrupt file is moved to: <c>name.bad-&lt;UTC&gt;</c>, then <c>name.bad-&lt;UTC&gt;-1</c>, <c>-2</c> ... when the name is taken.</summary>
    public static string BadName(string name, DateTimeOffset when, int attempt) =>
        attempt == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{name}.bad-{CheckTime.FileStamp(when)}")
            : string.Create(CultureInfo.InvariantCulture, $"{name}.bad-{CheckTime.FileStamp(when)}-{attempt}");
}
