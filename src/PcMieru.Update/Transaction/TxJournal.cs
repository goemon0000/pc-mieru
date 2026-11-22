using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using PcMieru.Update.Bundle;
using PcMieru.Update.Feed;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Transaction;

public enum TxOpIntent
{
    /// <summary>A name the old version did not have: the new file is renamed into place.</summary>
    Add,

    /// <summary>A name both have, with different content: the file is replaced.</summary>
    Overwrite,

    /// <summary>A name only the old version has: the file moves to displaced/.</summary>
    Remove,
}

public enum TxOpState
{
    /// <summary>Not started.</summary>
    Pending,

    /// <summary>The intent is on the disk; the rename may or may not have happened.</summary>
    Intent,

    /// <summary>Done and recorded (the recovery does not rely on it: it looks at the real hashes).</summary>
    Done,
}

/// <summary>One change of a swap: a name whose content differs between the two generations. Names that are the same in both are not here (the records and the full re-hash cover them).</summary>
public sealed class TxOp(string path, TxOpIntent intent, string? oldSha256, string? newSha256)
{
    public string Path { get; } = path;

    public TxOpIntent Intent { get; } = intent;

    public TxOpState State { get; set; }

    /// <summary>The SHA-256 of the file in the old generation (null for an add).</summary>
    public string? OldSha256 { get; } = oldSha256;

    /// <summary>The SHA-256 of the file in the new generation (null for a remove).</summary>
    public string? NewSha256 { get; } = newSha256;
}

/// <summary>The failure tags (blocked.json) the rollback may write.</summary>
public static class TxTag
{
    /// <summary>The new version did not start in three attempts.</summary>
    public const string LaunchFailed = "launch-failed";

    /// <summary>The installed set was not the new generation when it was verified.</summary>
    public const string GenerationMismatch = "generation-mismatch";

    public static bool IsKnown(string? tag) => tag is null or LaunchFailed or GenerationMismatch;
}

/// <summary>
/// The journal (<c>I\update\journal.json</c>, DESIGN-B 4, 5): the persistent state of a transaction. Written only through "&lt;name&gt;.tmp" + flush + write-through rename (<see cref="TxIo.WriteReplace"/>), so it is
/// always a whole old journal or a whole new one.
///
/// <code>
/// {
///   "format": "pcm-journal/1",
///   "tx": "0123456789ab",                 12 hex digits
///   "phase": "swapping",                  prepared | swapping | verifyPending | commitIntent | rollback
///   "launchAttempts": 0,                  counted BEFORE each launch of the new version, at most 3
///   "recoveryAttempts": 0,                counted BEFORE each recovery acts; automatic recovery stops at 3
///   "tag": null,                          "launch-failed" | "generation-mismatch" | null (what the rollback writes to blocked.json)
///   "from": { "version": "0.5.0", "buildId": "...", "installedSha256": "...", "files": [ { "path", "bytes", "sha256" } ] },
///   "to":   { "version": "0.5.1", "buildId": "...", "installedSha256": "...", "files": [ ... ] },
///   "ops": [ { "path": "PCMieru.exe", "intent": "overwrite", "state": "pending", "old": "&lt;sha&gt;", "new": "&lt;sha&gt;" } ]
/// }
/// </code>
/// "from" and "to" are the installed records of the two generations (the file installed.json holds the same lines); their "installedSha256" is the hash of the installed.json BYTES of that generation, so a
/// recovery can tell by hash which installed.json is on the disk. The ops are exactly the names that differ (adds, then overwrites, then removes, each in ordinal order); a journal whose ops are not that
/// difference is refused. The gate file is never an op. UTF-8, no byte order mark, LF line ends.
/// </summary>
public sealed class TxJournal
{
    public const string Format = "pcm-journal/1";

    public TxJournal(string tx, InstalledRecord from, string fromInstalledSha256, InstalledRecord to, string toInstalledSha256, IReadOnlyList<TxOp> ops)
    {
        Tx = tx;
        From = from;
        FromInstalledSha256 = fromInstalledSha256;
        To = to;
        ToInstalledSha256 = toInstalledSha256;
        Ops = ops;
    }

    public string Tx { get; }

    public TxPhase Phase { get; set; } = TxPhase.Prepared;

    public int LaunchAttempts { get; set; }

    public int RecoveryAttempts { get; set; }

    /// <summary>The failure tag the rollback writes to blocked.json, or null.</summary>
    public string? Tag { get; set; }

    public InstalledRecord From { get; }

    /// <summary>The SHA-256 of the installed.json bytes of the OLD generation.</summary>
    public string FromInstalledSha256 { get; }

    public InstalledRecord To { get; }

    /// <summary>The SHA-256 of the installed.json bytes of the NEW generation (= <c>To.Sha256</c>).</summary>
    public string ToInstalledSha256 { get; }

    public IReadOnlyList<TxOp> Ops { get; }

    // ---------------------------------------------------------------------------------------------------------------- the difference

    /// <summary>
    /// The ops that turn <paramref name="from"/> into <paramref name="to"/>: adds, then overwrites, then removes (each group in ordinal order of the path). The gate file is left out. Returns null with a
    /// <paramref name="problem"/> when a name is spelled differently in the two (they would be the same file on disk).
    /// </summary>
    public static List<TxOp>? Diff(InstalledRecord from, InstalledRecord to, out string problem)
    {
        problem = string.Empty;
        var adds = new List<TxOp>();
        var overwrites = new List<TxOp>();
        var removes = new List<TxOp>();
        foreach (var file in to.Files)
        {
            if (GateFile.IsExcludedFromSwap(file.Path))
            {
                continue;
            }

            if (!from.TryGet(file.Path, out var old))
            {
                adds.Add(new TxOp(file.Path, TxOpIntent.Add, null, file.Sha256));
            }
            else if (!string.Equals(old.Path, file.Path, StringComparison.Ordinal))
            {
                problem = $"'{BundleNames.Show(old.Path)}' and '{BundleNames.Show(file.Path)}' are the same name spelled differently";
                return null;
            }
            else if (!string.Equals(old.Sha256, file.Sha256, StringComparison.Ordinal) || old.Bytes != file.Bytes)
            {
                overwrites.Add(new TxOp(file.Path, TxOpIntent.Overwrite, old.Sha256, file.Sha256));
            }
        }

        foreach (var file in from.Files)
        {
            if (!GateFile.IsExcludedFromSwap(file.Path) && !to.TryGet(file.Path, out _))
            {
                removes.Add(new TxOp(file.Path, TxOpIntent.Remove, file.Sha256, null));
            }
        }

        return [.. adds, .. overwrites, .. removes];
    }

    // ---------------------------------------------------------------------------------------------------------------- write

    public static string PhaseName(TxPhase phase) => phase switch
    {
        TxPhase.Prepared => "prepared",
        TxPhase.Swapping => "swapping",
        TxPhase.VerifyPending => "verifyPending",
        TxPhase.CommitIntent => "commitIntent",
        TxPhase.Rollback => "rollback",
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    private static bool TryPhase(string? name, out TxPhase phase)
    {
        switch (name)
        {
            case "prepared": phase = TxPhase.Prepared; return true;
            case "swapping": phase = TxPhase.Swapping; return true;
            case "verifyPending": phase = TxPhase.VerifyPending; return true;
            case "commitIntent": phase = TxPhase.CommitIntent; return true;
            case "rollback": phase = TxPhase.Rollback; return true;
            default: phase = default; return false;
        }
    }

    private static string IntentName(TxOpIntent intent) => intent switch
    {
        TxOpIntent.Add => "add",
        TxOpIntent.Overwrite => "overwrite",
        _ => "remove",
    };

    private static string StateName(TxOpState state) => state switch
    {
        TxOpState.Pending => "pending",
        TxOpState.Intent => "intent",
        _ => "done",
    };

    public byte[] ToUtf8Bytes()
    {
        var text = new StringBuilder();
        text.Append("{\n");
        text.Append("  \"format\": ").Append(TxJson.Quote(Format)).Append(",\n");
        text.Append("  \"tx\": ").Append(TxJson.Quote(Tx)).Append(",\n");
        text.Append("  \"phase\": ").Append(TxJson.Quote(PhaseName(Phase))).Append(",\n");
        text.Append("  \"launchAttempts\": ").Append(TxJson.Number(LaunchAttempts)).Append(",\n");
        text.Append("  \"recoveryAttempts\": ").Append(TxJson.Number(RecoveryAttempts)).Append(",\n");
        text.Append("  \"tag\": ").Append(TxJson.QuoteOrNull(Tag)).Append(",\n");
        AppendGeneration(text, "from", From, FromInstalledSha256);
        text.Append(",\n");
        AppendGeneration(text, "to", To, ToInstalledSha256);
        text.Append(",\n");
        if (Ops.Count == 0)
        {
            text.Append("  \"ops\": []");
        }
        else
        {
            text.Append("  \"ops\": [\n");
            for (var i = 0; i < Ops.Count; i++)
            {
                var op = Ops[i];
                text.Append("    { \"path\": ").Append(TxJson.Quote(op.Path))
                    .Append(", \"intent\": ").Append(TxJson.Quote(IntentName(op.Intent)))
                    .Append(", \"state\": ").Append(TxJson.Quote(StateName(op.State)))
                    .Append(", \"old\": ").Append(TxJson.QuoteOrNull(op.OldSha256))
                    .Append(", \"new\": ").Append(TxJson.QuoteOrNull(op.NewSha256))
                    .Append(" }").Append(i + 1 < Ops.Count ? ",\n" : "\n");
            }

            text.Append("  ]");
        }

        text.Append("\n}\n");
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text.ToString());
    }

    private static void AppendGeneration(StringBuilder text, string name, InstalledRecord record, string installedSha256)
    {
        text.Append("  \"").Append(name).Append("\": {\n");
        text.Append("    \"version\": ").Append(TxJson.Quote(record.Version)).Append(",\n");
        text.Append("    \"buildId\": ").Append(TxJson.Quote(record.BuildId)).Append(",\n");
        text.Append("    \"installedSha256\": ").Append(TxJson.Quote(installedSha256)).Append(",\n");
        TxJson.AppendFiles(text, "    ", record.Files);
        text.Append("\n  }");
    }

    // ---------------------------------------------------------------------------------------------------------------- read

    /// <summary>The strict reader. A broken file, an unknown phase, an unknown tag, a bad transaction id, a bad record or ops that are not the difference of the two records are all refused (with a reason for a log).</summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8Json, [NotNullWhen(true)] out TxJournal? journal, out string error)
    {
        journal = null;
        if (!TxJson.TryParseRoot(utf8Json, out var document, out error))
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
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

            if (!TxJson.TryGetString(root, "tx", out var tx) || !TxPaths.IsValidTx(tx))
            {
                error = "tx is not 12 lower-case hex digits";
                return false;
            }

            if (!TxJson.TryGetString(root, "phase", out var phaseName) || !TryPhase(phaseName, out var phase))
            {
                error = $"unknown phase '{(phaseName is null ? "(none)" : BundleNames.Show(phaseName))}'";
                return false;
            }

            if (!TxJson.TryGetInt(root, "launchAttempts", out var launch) || launch is < 0 or > 1000
                || !TxJson.TryGetInt(root, "recoveryAttempts", out var recovery) || recovery is < 0 or > 1000)
            {
                error = "launchAttempts or recoveryAttempts is not a small non-negative integer";
                return false;
            }

            string? tag = null;
            if (root.TryGetProperty("tag", out var tagElement) && tagElement.ValueKind != JsonValueKind.Null)
            {
                if (tagElement.ValueKind != JsonValueKind.String || !TxTag.IsKnown(tag = tagElement.GetString()))
                {
                    error = "unknown tag";
                    return false;
                }
            }

            if (!TryGeneration(root, "from", out var from, out var fromSha, out error)
                || !TryGeneration(root, "to", out var to, out var toSha, out error))
            {
                return false;
            }

            if (!root.TryGetProperty("ops", out var opsElement) || opsElement.ValueKind != JsonValueKind.Array)
            {
                error = "ops is not an array";
                return false;
            }

            var expected = Diff(from, to, out var problem);
            if (expected is null)
            {
                error = problem;
                return false;
            }

            var ops = new List<TxOp>();
            foreach (var element in opsElement.EnumerateArray())
            {
                if (!TryOp(element, out var op, out error))
                {
                    return false;
                }

                ops.Add(op);
            }

            if (ops.Count != expected.Count
                || ops.Where((op, i) => op.Path != expected[i].Path || op.Intent != expected[i].Intent || op.OldSha256 != expected[i].OldSha256 || op.NewSha256 != expected[i].NewSha256).Any())
            {
                error = "ops are not the difference of from and to";
                return false;
            }

            journal = new TxJournal(tx, from, fromSha, to, toSha, ops)
            {
                Phase = phase,
                LaunchAttempts = launch,
                RecoveryAttempts = recovery,
                Tag = tag,
            };
            error = string.Empty;
            return true;
        }
    }

    private static bool TryGeneration(JsonElement root, string name, [NotNullWhen(true)] out InstalledRecord? record, [NotNullWhen(true)] out string? installedSha256, out string error)
    {
        record = null;
        installedSha256 = null;
        if (!root.TryGetProperty(name, out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            error = $"{name} is not an object";
            return false;
        }

        if (!TxJson.TryReadGeneration(obj, out var version, out var buildId, out var files, out error))
        {
            error = $"{name}: {error}";
            return false;
        }

        if (!TxJson.TryGetString(obj, "installedSha256", out var sha) || !BundleManifest.IsLowerHex64(sha))
        {
            error = $"{name}: installedSha256 is not 64 lower-case hex digits";
            return false;
        }

        try
        {
            record = new InstalledRecord(version, buildId, files);
        }
        catch (ArgumentException ex)
        {
            error = $"{name}: {ex.Message}";
            return false;
        }

        installedSha256 = sha;
        error = string.Empty;
        return true;
    }

    private static bool TryOp(JsonElement element, [NotNullWhen(true)] out TxOp? op, out string error)
    {
        op = null;
        if (!TxJson.TryGetString(element, "path", out var path) || !BundleNames.Check(path).Ok || GateFile.IsExcludedFromSwap(path)
            || !TxJson.TryGetString(element, "intent", out var intentName) || !TxJson.TryGetString(element, "state", out var stateName))
        {
            error = "an op has no usable path, intent or state";
            return false;
        }

        TxOpIntent intent;
        switch (intentName)
        {
            case "add": intent = TxOpIntent.Add; break;
            case "overwrite": intent = TxOpIntent.Overwrite; break;
            case "remove": intent = TxOpIntent.Remove; break;
            default: error = "an op has an unknown intent"; return false;
        }

        TxOpState state;
        switch (stateName)
        {
            case "pending": state = TxOpState.Pending; break;
            case "intent": state = TxOpState.Intent; break;
            case "done": state = TxOpState.Done; break;
            default: error = "an op has an unknown state"; return false;
        }

        if (!TryHashOrNull(element, "old", out var oldSha) || !TryHashOrNull(element, "new", out var newSha))
        {
            error = "an op has a bad old or new hash";
            return false;
        }

        op = new TxOp(path, intent, oldSha, newSha) { State = state };
        error = string.Empty;
        return true;
    }

    private static bool TryHashOrNull(JsonElement element, string name, out string? sha)
    {
        sha = null;
        if (!element.TryGetProperty(name, out var value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        sha = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return sha is not null && BundleManifest.IsLowerHex64(sha);
    }
}
