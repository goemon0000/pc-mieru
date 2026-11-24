using System.Globalization;
using PcMieru.Update.Feed;

namespace PcMieru.Update.Launch;

/// <summary>
/// The one message a GUI sends the runner to ask for a swap (DESIGN-B 3.3, "nothing on the command line"): the stage folder, the SHA-256 of that stage's <c>release.manifest.json</c>, the version, the build id and
/// the tag, as ONE line of tab-separated fields. It names what the GUI believes; the runner trusts none of it and reads the folder from disk, ties it to the hash, and decides by its own checks.
/// </summary>
public sealed record ApplyMessage(string Stage, string ManifestSha256, string Version, string BuildId, string Tag)
{
    public const string Verb = "pcm-apply/1";

    public static ApplyMessage Of(StagedUpdate staged) => new(staged.Folder, staged.ManifestSha256, staged.Version, staged.BuildId, staged.Tag);

    public string ToLine() => string.Join('\t', Verb, Stage, ManifestSha256, Version, BuildId, Tag);

    /// <summary>False unless the line is exactly the six fields, each of the right shape (a plain X.Y.Z version, "v" + it as the tag, a lower-case GUID, 64 lower-case hex digits, an absolute path with no control character).</summary>
    public static bool TryParse(string? line, out ApplyMessage message)
    {
        message = new ApplyMessage(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        if (line is null)
        {
            return false;
        }

        var fields = line.Split('\t');
        if (fields.Length != 6 || fields[0] != Verb)
        {
            return false;
        }

        var (stage, sha, version, build, tag) = (fields[1], fields[2], fields[3], fields[4], fields[5]);
        if (stage.Length == 0 || stage.Any(char.IsControl) || !Path.IsPathFullyQualified(stage)
            || !Sha256List.IsHex64(sha) || sha != sha.ToLowerInvariant()
            || !AppVersion.TryParse(version, out var parsed) || parsed.ToString() != version || tag != "v" + version
            || !Guid.TryParseExact(build, "D", out var guid) || guid.ToString("D") != build)
        {
            return false;
        }

        message = new ApplyMessage(stage, sha, version, build, tag);
        return true;
    }
}

/// <summary>Why the runner (or the GUI, before it asked) did not take the update. Spelled on the wire as <c>refused &lt;Name&gt;</c>.</summary>
public enum ApplyRefusal
{
    /// <summary>The runner did not understand the message (or none came).</summary>
    BadMessage,

    /// <summary>The runner runs with an administrator token and swaps nothing.</summary>
    Elevated,

    /// <summary>The stage folder is not one of this install's stages (another place, a link on the way, no such folder).</summary>
    StageNotFound,

    /// <summary>The stage on disk is not what the GUI said (the manifest's hash, version, build or the inner manifest differ).</summary>
    StageChanged,

    /// <summary>The tag is in blocked.json: it was installed once and could not be started.</summary>
    Blocked,

    /// <summary>The version is not newer than the installed one.</summary>
    NotNewer,

    /// <summary>gui.ini does not say <c>update.mode=auto</c>.</summary>
    NotAutomatic,

    /// <summary>recover.exe could not be made ready or started (GUI side).</summary>
    RunnerNotReady,

    /// <summary>The runner did not answer in time (GUI side).</summary>
    NoAnswer,
}

/// <summary>The words of the answer line, and the plain sentences the GUI shows for a refusal.</summary>
public static class ApplyAnswer
{
    public const string Accepted = "accepted";
    private const string RefusedPrefix = "refused ";

    public static string Refuse(ApplyRefusal refusal) => RefusedPrefix + refusal.ToString();

    /// <summary>True for "accepted". False with a refusal for "refused &lt;Name&gt;"; anything else is false with <see cref="ApplyRefusal.BadMessage"/>.</summary>
    public static bool TryParse(string? line, out ApplyRefusal refusal)
    {
        refusal = ApplyRefusal.BadMessage;
        if (line == Accepted)
        {
            return true;
        }

        if (line is not null && line.StartsWith(RefusedPrefix, StringComparison.Ordinal)
            && Enum.TryParse<ApplyRefusal>(line[RefusedPrefix.Length..], out var named) && Enum.IsDefined(named) && named.ToString() == line[RefusedPrefix.Length..])
        {
            refusal = named;
        }

        return false;
    }

    public static string Ja(ApplyRefusal refusal) => refusal switch
    {
        ApplyRefusal.Elevated => "管理者として開いているため、更新を始められませんでした。ふつうの権限で開き直してください。",
        ApplyRefusal.StageNotFound or ApplyRefusal.StageChanged => "ダウンロードした更新が見つからないか、途中で変わっていたため、始めませんでした。もう一度ダウンロードします。",
        ApplyRefusal.Blocked => "この版は以前うまく起動しなかったため、入れません。",
        ApplyRefusal.NotNewer => "いま入っている版より新しい更新ではなかったため、始めませんでした。",
        ApplyRefusal.NotAutomatic => "自動更新が「自動」になっていないため、更新しませんでした。",
        ApplyRefusal.RunnerNotReady => "更新のためのプログラムを用意できませんでした。あとでもう一度試します。",
        ApplyRefusal.NoAnswer => "更新のためのプログラムから答えがなかったため、始めませんでした。あとでもう一度試します。",
        _ => "更新の引き渡しがうまくいかなかったため、始めませんでした。あとでもう一度試します。",
    };

    public static string En(ApplyRefusal refusal) => refusal switch
    {
        ApplyRefusal.Elevated => "PC Mieru is open as administrator, so the update did not start. Open it again with normal rights.",
        ApplyRefusal.StageNotFound or ApplyRefusal.StageChanged => "The downloaded update was missing or had changed, so it did not start. It will be downloaded again.",
        ApplyRefusal.Blocked => "This version could not be started before, so it will not be installed.",
        ApplyRefusal.NotNewer => "That update is not newer than the installed version, so it did not start.",
        ApplyRefusal.NotAutomatic => "Automatic updates are not turned on, so nothing was updated.",
        ApplyRefusal.RunnerNotReady => "The program that installs updates could not be made ready. PC Mieru will try again later.",
        ApplyRefusal.NoAnswer => "The program that installs updates did not answer, so the update did not start. PC Mieru will try again later.",
        _ => "The update could not be handed over, so it did not start. PC Mieru will try again later.",
    };
}

/// <summary>The lines of the launch check (DESIGN-B 5): the verified program says <c>drawn</c> once its window is painted and answers every <c>ping n</c> with <c>pong n</c> from its UI thread; <c>done</c> tells it to close.</summary>
public static class VerifyProtocol
{
    public const string Drawn = "drawn";
    public const string Done = "done";
    public const string PingPrefix = "ping ";
    public const string PongPrefix = "pong ";

    public static string Ping(int n) => PingPrefix + n.ToString(CultureInfo.InvariantCulture);

    public static string Pong(int n) => PongPrefix + n.ToString(CultureInfo.InvariantCulture);

    /// <summary>The number in "ping n" / "pong n" (the prefix says which); null when the line is not of that kind.</summary>
    public static int? NumberOf(string line, string prefix) =>
        line.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(line.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
}
