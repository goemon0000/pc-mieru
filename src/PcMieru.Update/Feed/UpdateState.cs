// Provenance: ported from the owner's pl-cloud src/PartsLife.Update/UpdateState.cs (Parts Life, MIT, same owner; see NOTICE.txt).
// What is new here: the folder is L\update\<id> (DESIGN-B 4), the file is replaced with File.Replace after a flush to disk, a check holds check.lock for its whole
// run, a refusal is remembered, and the comments are in English.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcMieru.Update.Feed;

/// <summary>What the feed remembers between runs: when it last looked, until when it must leave GitHub alone, and what it found.</summary>
public sealed class UpdateState
{
    /// <summary>When the last check began (written before the request, so a crash does not make a loop).</summary>
    public DateTimeOffset? LastCheckUtc { get; set; }

    /// <summary>Until when a rate limit must be waited out.</summary>
    public DateTimeOffset? BackoffUntilUtc { get; set; }

    /// <summary>The last candidate: its version ("1.2.3"), or null when the last check found nothing newer.</summary>
    public string? LatestVersion { get; set; }

    /// <summary>The Release page of the last candidate.</summary>
    public string? LatestPageUrl { get; set; }

    /// <summary>The name of the <see cref="UpdateRefusal"/> the last check ended with, or null.</summary>
    public string? LastRefusal { get; set; }

    internal UpdateStatus ToStatus()
    {
        AppVersion? candidate = AppVersion.TryParse(LatestVersion, out var v) && LatestVersion == v.ToString() ? v : null;
        var page = candidate is { } c && UpdateEndpoints.IsReleasePage(LatestPageUrl) && LatestPageUrl == UpdateEndpoints.ReleasePageUrl(c.Tag) ? LatestPageUrl : null;
        var refusal = Enum.TryParse<UpdateRefusal>(LastRefusal, ignoreCase: false, out var r) && Enum.IsDefined(r) ? r : UpdateRefusal.None;
        return new UpdateStatus(LastCheckUtc, BackoffUntilUtc, candidate, page, refusal);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UpdateState))]
internal sealed partial class UpdateStateJson : JsonSerializerContext;

/// <summary>How <see cref="UpdateStateStore.TryLock"/> ended.</summary>
public enum LockOutcome
{
    /// <summary>The lock is held.</summary>
    Acquired,

    /// <summary>Another check of this install holds it.</summary>
    Busy,

    /// <summary>The folder or the file cannot be used (read-only, no rights, no such drive).</summary>
    Unavailable,
}

/// <summary>
/// L\update\&lt;id&gt;\state.json and check.lock (DESIGN-B 4). The state is written to a .tmp in the same folder, flushed to disk and put in place with
/// File.Replace, so a reader sees the old file or the new one and a crash leaves one of them. The lock serialises the checks of one install (two
/// windows, two users of one folder): the one that cannot get it does not ask GitHub.
/// </summary>
public sealed class UpdateStateStore
{
    public const string StateFileName = "state.json";
    public const string LockFileName = "check.lock";

    /// <summary>A state file is a few hundred bytes; a bigger one is not read.</summary>
    private const int MaxStateBytes = 64 * 1024;

    private readonly string _folder;

    public UpdateStateStore(string folder) => _folder = folder;

    public string Folder => _folder;

    public string StatePath => Path.Combine(_folder, StateFileName);

    public string LockPath => Path.Combine(_folder, LockFileName);

    /// <summary>The saved state; a fresh one when there is none or it cannot be read (a broken file must not stop the app).</summary>
    public UpdateState Read()
    {
        try
        {
            var info = new FileInfo(StatePath);
            if (!info.Exists || info.Length > MaxStateBytes)
            {
                return new UpdateState();
            }

            using var stream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(stream, UpdateStateJson.Default.UpdateState) ?? new UpdateState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new UpdateState();
        }
    }

    /// <summary>Writes the state (temp file, flush to disk, replace). False when it could not be written.</summary>
    public bool TryWrite(UpdateState state)
    {
        var tmp = StatePath + ".tmp";
        try
        {
            Directory.CreateDirectory(_folder);
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, UpdateStateJson.Default.UpdateState);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(StatePath))
            {
                File.Replace(tmp, StatePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, StatePath);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(tmp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            return false;
        }
    }

    /// <summary>Takes check.lock. <paramref name="held"/> is null unless the outcome is <see cref="LockOutcome.Acquired"/>; dispose it to let go.</summary>
    public LockOutcome TryLock(out IDisposable? held)
    {
        held = null;
        try
        {
            Directory.CreateDirectory(_folder);
            held = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return LockOutcome.Acquired;
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return LockOutcome.Busy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LockOutcome.Unavailable;
        }
    }

    /// <summary>ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33): somebody else has the file open.</summary>
    internal static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;
}
