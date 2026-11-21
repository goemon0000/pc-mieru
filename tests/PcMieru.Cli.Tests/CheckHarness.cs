using System.Globalization;
using PcMieru.Checks;

namespace PcMieru.Cli.Tests;

/// <summary>
/// A folder for one test: under DUO_TEST_TMP when the runner sets it (this machine: C:\DuoStudio\test-tmp), else the system temp folder.
/// Not created until asked (a history folder that is not there is a case of its own). Gone when disposed.
/// </summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder(bool create = false)
    {
        var parent = Environment.GetEnvironmentVariable("DUO_TEST_TMP");
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            parent = System.IO.Path.GetTempPath();
        }

        Path = System.IO.Path.Combine(parent, "pcmi-check-" + Guid.NewGuid().ToString("N"));
        if (create)
        {
            Directory.CreateDirectory(Path);
        }
    }

    public string Path { get; }

    /// <summary>The checks folder under this folder, as the CLI takes it (<c>&lt;data root&gt;\checks</c>).</summary>
    public string Checks => System.IO.Path.Combine(Path, "checks");

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover in the test folder is not a failure of the test.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Results for the fake runner and the seeded history, built through the public API. Fixed times (whole milliseconds, UTC) and UUIDs that
/// follow from a number, so the output never changes between runs. Every one of them is valid (a test pins that).
/// </summary>
internal static class CheckSamples
{
    public const string App = "PCMieru 0.4.0";

    public static readonly DateTimeOffset Start = new(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);

    public static Guid Id(int n) => Guid.Parse(string.Create(CultureInfo.InvariantCulture, $"{n:x8}-1111-4222-8333-444455556666"));

    private static CheckItem I(
        string id,
        CheckValue? value,
        ItemVerdict verdict,
        CheckSource source,
        CheckReason? reason = null,
        CheckSubject? subject = null,
        CheckEvidence? evidence = null) =>
        CheckItem.Create(id, value, verdict, source, reason, subject, evidence);

    private static CheckResult Make(
        int n,
        CheckKind kind,
        CheckOutcome outcome,
        IReadOnlyList<CheckItem> items,
        IReadOnlyList<string>? notes = null,
        DateTimeOffset? start = null)
    {
        var from = start ?? Start.AddMinutes(n);
        return CheckResult.Create(App, Id(n), kind, from, from.AddMilliseconds(1250), outcome, items, notes);
    }

    /// <summary>The file name a saved result has (kind, end time to the millisecond, run ID without hyphens).</summary>
    public static string FileName(CheckResult result) =>
        result.Kind.ToString().ToLowerInvariant() + "-" + result.EndedAtUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + "-" + result.RunId.ToString("N") + ".json";

    /// <summary>A system check whose every judged row is fine: verdict ok.</summary>
    public static CheckResult SystemOk(int n = 1) =>
        Make(
            n,
            CheckKind.System,
            CheckOutcome.Completed,
            [
                I("system.os.uptime", CheckValue.Number(273600.5), ItemVerdict.Ok, CheckSource.Os),
                I("system.cpu.cores", CheckValue.Int(8), ItemVerdict.Info, CheckSource.Win32),
                I("system.eventlog.whea7d", CheckValue.Int(0), ItemVerdict.Ok, CheckSource.Eventlog),
            ]);

    /// <summary>A memory that runs below its rated speed: verdict caution. The model of a subject carries a name that redaction must blank.</summary>
    public static CheckResult SystemCaution(int n = 1, string? model = null) =>
        Make(
            n,
            CheckKind.System,
            CheckOutcome.Completed,
            [
                I("system.os.uptime", CheckValue.Number(100), ItemVerdict.Ok, CheckSource.Os),
                I("system.cpu.cores", CheckValue.Int(8), ItemVerdict.Info, CheckSource.Win32),
                I("system.memory.configured_speed", CheckValue.Int(4800), ItemVerdict.Caution, CheckSource.Smbios, null, new CheckSubject("ram-1", model), CheckEvidence.OfCode(CheckEvidenceCode.BelowRated)),
            ]);

    /// <summary>Hardware errors in the event log: verdict check ("needs review").</summary>
    public static CheckResult SystemCheck(int n = 1) =>
        Make(
            n,
            CheckKind.System,
            CheckOutcome.Completed,
            [
                I("system.os.uptime", CheckValue.Number(100), ItemVerdict.Ok, CheckSource.Os),
                I("system.eventlog.whea7d", CheckValue.Int(3), ItemVerdict.Check, CheckSource.Eventlog),
            ]);

    /// <summary>A row that could not be read (no permission) and nothing else judged: verdict unavailable ("not measured").</summary>
    public static CheckResult SystemUnavailable(int n = 1) =>
        Make(
            n,
            CheckKind.System,
            CheckOutcome.Completed,
            [
                I("system.cpu.cores", CheckValue.Int(8), ItemVerdict.Info, CheckSource.Win32),
                I("system.eventlog.volmgr7d", null, ItemVerdict.Unavailable, CheckSource.Eventlog, CheckReason.Denied),
            ]);

    /// <summary>A run that was stopped half way (outcome stopped). What was found by then is kept.</summary>
    public static CheckResult SystemStopped(int n = 1) =>
        Make(
            n,
            CheckKind.System,
            CheckOutcome.Stopped,
            [
                I("system.os.uptime", CheckValue.Number(100), ItemVerdict.Ok, CheckSource.Os),
            ]);

    /// <summary>A run that could not be done at all: the empty failed result.</summary>
    public static CheckResult Failed(CheckKind kind = CheckKind.System, int n = 1) => Make(n, kind, CheckOutcome.Failed, []);

    /// <summary>A storage check in which the NVMe drive was read.</summary>
    public static CheckResult StorageRead(int n = 1) =>
        Make(
            n,
            CheckKind.Storage,
            CheckOutcome.Completed,
            [
                I("storage.devices", CheckValue.Int(1), ItemVerdict.Info, CheckSource.StorageQuery),
                I("storage.health", CheckValue.Text("healthy"), ItemVerdict.Ok, CheckSource.StorageQuery, null, new CheckSubject("disk-1", "Samsung SSD 980 PRO 2TB")),
                I("storage.temperature", CheckValue.Number(41), ItemVerdict.Ok, CheckSource.Nvme, null, new CheckSubject("disk-1", "Samsung SSD 980 PRO 2TB")),
            ]);

    /// <summary>A storage check in which the drive could not be read without administrator rights (its temperature row is denied).</summary>
    public static CheckResult StorageDenied(int n = 1) =>
        Make(
            n,
            CheckKind.Storage,
            CheckOutcome.Completed,
            [
                I("storage.devices", CheckValue.Int(1), ItemVerdict.Info, CheckSource.StorageQuery),
                I("storage.health", CheckValue.Text("healthy"), ItemVerdict.Ok, CheckSource.StorageQuery, null, new CheckSubject("disk-1", "Samsung SSD 980 PRO 2TB")),
                I("storage.temperature", null, ItemVerdict.Unavailable, CheckSource.StorageQuery, CheckReason.Denied, new CheckSubject("disk-1", "Samsung SSD 980 PRO 2TB")),
            ]);

    /// <summary>
    /// Two drives: the first needed the approval and was not read, the second was read without it. With <c>--elevate</c> that is not a refusal
    /// the CLI can tell from a result alone (one temperature has a value), so the exit code stays 0.
    /// </summary>
    public static CheckResult StorageOneReadOneDenied(int n = 1) =>
        Make(
            n,
            CheckKind.Storage,
            CheckOutcome.Completed,
            [
                I("storage.devices", CheckValue.Int(2), ItemVerdict.Info, CheckSource.StorageQuery),
                I("storage.temperature", null, ItemVerdict.Unavailable, CheckSource.StorageQuery, CheckReason.Denied, new CheckSubject("disk-1", "Samsung SSD 980 PRO 2TB")),
                I("storage.temperature", CheckValue.Number(35.5), ItemVerdict.Ok, CheckSource.Ata, null, new CheckSubject("disk-2", "CT500MX500SSD1")),
            ]);

    /// <summary>A result with a note and a row of every value shape the table prints (a number with a unit, a count, a size, a yes/no, a version).</summary>
    public static CheckResult WithNoteAndValues(int n = 1) =>
        Make(
            n,
            CheckKind.System,
            CheckOutcome.Completed,
            [
                I("system.os.version", CheckValue.OsVersion(new OsVersionValue("Windows 11 Pro", "25H2", 26300, 7171)), ItemVerdict.Info, CheckSource.Os),
                I("system.os.uptime", CheckValue.Number(273600.5), ItemVerdict.Ok, CheckSource.Os),
                I("system.cpu.cores", CheckValue.Int(8), ItemVerdict.Info, CheckSource.Win32),
                I("system.memory.capacity", CheckValue.Dec("17179869184"), ItemVerdict.Info, CheckSource.Smbios, null, new CheckSubject("ram-1")),
                I("system.gpu.generic_driver", CheckValue.Bool(false), ItemVerdict.Ok, CheckSource.Dxgi, null, new CheckSubject("gpu-1", "NVIDIA GeForce RTX 5070")),
                I("system.eventlog.volmgr7d", null, ItemVerdict.Unavailable, CheckSource.Eventlog, CheckReason.Denied),
            ],
            [CheckNotes.CpuTemperatureNotMeasured]);

    public static IReadOnlyList<CheckResult> AllShapes() =>
    [
        SystemOk(), SystemCaution(), SystemCheck(), SystemUnavailable(), SystemStopped(), Failed(), Failed(CheckKind.Storage), StorageRead(),
        StorageDenied(), StorageOneReadOneDenied(), WithNoteAndValues(),
    ];

    /// <summary>Writes the results into a history folder the way a real run does (the store a run uses), and checks that each was saved.</summary>
    public static async Task SeedAsync(string root, params CheckResult[] results)
    {
        var store = new CheckHistoryStore(root);
        foreach (var result in results)
        {
            var save = await store.SaveAsync(result);
            Assert.Equal(CheckSaveStatus.Saved, save.Status);
        }
    }
}

/// <summary>A runner that answers as the test says and remembers what it was asked.</summary>
internal sealed class FakeCheckRunner(Func<CheckRequest, IProgress<CheckProgress>?, CancellationToken, Task<CheckRun>> handler) : ICheckRunner
{
    private readonly object _lock = new();
    private readonly List<CheckRequest> _requests = [];

    public IReadOnlyList<CheckRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    public static FakeCheckRunner Returning(CheckRun run) => new((_, _, _) => Task.FromResult(run));

    public static FakeCheckRunner Returning(CheckResult result) => Returning(Saved(result));

    /// <summary>A run that was saved under its proper file name.</summary>
    public static CheckRun Saved(CheckResult result) => new(result, CheckSaveState.Saved(CheckSamples.FileName(result)));

    public Task<CheckRun> RunAsync(CheckRequest request, IProgress<CheckProgress>? progress, CancellationToken ct)
    {
        lock (_lock)
        {
            _requests.Add(request);
        }

        return handler(request, progress, ct);
    }
}

/// <summary>The texts of the CLI in one language, read from the tables themselves (so a test names what the user is told, not how it is spelled today).</summary>
internal static class CliWords
{
    private static StringEntry Find(string key)
    {
        _ = CliApp.Commands;
        return CliStrings.All.Concat(CliApp.Commands.SelectMany(c => c.Texts)).Single(e => e.Key == key);
    }

    public static string En(string key) => Find(key).En;

    public static string Ja(string key) => Find(key).Ja;

    public static string Of(string key, Language language) => language == Language.Ja ? Ja(key) : En(key);

    /// <summary>The text with its {placeholders} filled.</summary>
    public static string Fill(string text, params (string Name, string Value)[] values)
    {
        foreach (var (name, value) in values)
        {
            text = text.Replace("{" + name + "}", value, StringComparison.Ordinal);
        }

        return text;
    }
}
