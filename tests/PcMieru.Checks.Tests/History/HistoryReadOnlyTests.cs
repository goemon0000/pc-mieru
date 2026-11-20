using System.Reflection;
using System.Runtime.CompilerServices;

namespace PcMieru.Checks.Tests;

/// <summary>DESIGN-A §4, §8: the history as the MCP server sees it: it lists and reads, and changes nothing in the folder.</summary>
public sealed class HistoryReadOnlyTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    private string Root => Path.Combine(_tmp.Path, "checks");

    public void Dispose() => _tmp.Dispose();

    private static DateTimeOffset T(int seconds) => Samples.Start.AddSeconds(seconds);

    /// <summary>A reader that does the real reading and writes down every call, so a test can say which calls were made.</summary>
    private sealed class RecordingReader : ICheckHistoryFileReader
    {
        private readonly List<string> _calls = [];

        public IReadOnlyList<string> Calls => _calls;

        public IReadOnlyList<HistoryFileEntry> ListFiles(string directory)
        {
            _calls.Add("list");
            return PhysicalHistoryFileReader.Instance.ListFiles(directory);
        }

        public byte[] ReadAllBytes(string path, int maxBytes)
        {
            _calls.Add("read");
            return PhysicalHistoryFileReader.Instance.ReadAllBytes(path, maxBytes);
        }

        public bool Exists(string path)
        {
            _calls.Add("exists");
            return PhysicalHistoryFileReader.Instance.Exists(path);
        }
    }

    /// <summary>A folder with one of everything: valid files, every kind of broken file, a locked file, leftovers and neighbours.</summary>
    private (string LockedName, Guid ValidMemory, Guid ValidCpu, Guid Corrupt, Guid Locked) Populate()
    {
        // Valid.
        Kit.Put(Root, Kit.Make(CheckKind.Memory, T(10), Kit.Id(10)));
        Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(11), Kit.Id(11)));

        // Corrupt content.
        Kit.PutText(Root, Kit.NameOf(CheckKind.Memory, T(20), Kit.Id(20)), "{ corrupt");

        // Name and body disagree: end time, UUID, kind.
        Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(30), Kit.Id(30)), Kit.NameOf(CheckKind.Cpu, T(31), Kit.Id(30)));
        Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(32), Kit.Id(32)), Kit.NameOf(CheckKind.Cpu, T(32), Kit.Id(33)));
        Kit.Put(Root, Kit.Make(CheckKind.Storage, T(34), Kit.Id(34)), Kit.NameOf(CheckKind.Memory, T(34), Kit.Id(34)));

        // A newer format, and a kind this version does not know.
        var future = Kit.Make(CheckKind.System, T(40), Kit.Id(40));
        Kit.PutText(Root, Kit.NameOf(future), Kit.Edited(future, "\"format\": \"pcm-check/1\"", "\"format\": \"pcm-check/2\""));
        var gpu = Kit.Make(CheckKind.System, T(41), Kit.Id(41));
        Kit.PutText(Root, $"gpu-{Kit.Stamp(T(41))}-{Kit.Id(41):N}.json", Kit.Edited(gpu, "\"kind\": \"system\"", "\"kind\": \"gpu\""));

        // Held by another process.
        var locked = Kit.Make(CheckKind.Storage, T(50), Kit.Id(50));
        var lockedName = Kit.Put(Root, locked);

        // Leftovers and neighbours: an old tmp of ours, a .bad file, a note, a folder with a history-like file in it.
        var stale = Kit.PutText(Root, Kit.TempNameOf(Kit.NameOf(CheckKind.Memory, T(60), Kit.Id(60)), 1), "leftover");
        Kit.SetAge(Path.Combine(Root, stale), Kit.Now - TimeSpan.FromDays(5));
        Kit.Put(Root, Kit.Make(CheckKind.Memory, T(70), Kit.Id(70)), $"{Kit.NameOf(CheckKind.Memory, T(70), Kit.Id(70))}.bad-20261120T000000000Z");
        Kit.PutText(Root, "notes.txt", "keep me");
        Kit.Put(Path.Combine(Root, "baseline"), Kit.Make(CheckKind.Memory, T(80), Kit.Id(80)));

        return (lockedName, Kit.Id(10), Kit.Id(11), Kit.Id(20), Kit.Id(50));
    }

    // ---- nothing changes ----

    [Fact]
    public async Task Listing_and_getting_leave_the_folder_exactly_as_it_was()
    {
        var (lockedName, validMemory, validCpu, corrupt, locked) = Populate();

        IReadOnlyList<string> before;
        IReadOnlyList<string> after;
        CheckHistoryPage all;
        CheckHistoryPage memoryOnly;
        CheckHistoryPage limited;
        CheckHistoryGetResult gotValid;
        CheckHistoryGetResult gotCorrupt;
        CheckHistoryGetResult gotLocked;
        CheckHistoryGetResult gotNothing;
        using (new FileStream(Path.Combine(Root, lockedName), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            before = Kit.Snapshot(Root);
            var history = Kit.ReadOnly(Root);
            all = await history.ListAsync(Kit.All, CancellationToken.None);
            memoryOnly = await history.ListAsync(CheckHistoryQuery.ForMcp(CheckKind.Memory, null), CancellationToken.None);
            limited = await history.ListAsync(CheckHistoryQuery.ForMcp(null, 1), CancellationToken.None);
            gotValid = await history.GetAsync(validMemory, CancellationToken.None);
            gotCorrupt = await history.GetAsync(corrupt, CancellationToken.None);
            gotLocked = await history.GetAsync(locked, CancellationToken.None);
            gotNothing = await history.GetAsync(Kit.Id(999), CancellationToken.None);
            after = Kit.Snapshot(Root);
        }

        Assert.Equal(before, after);

        // The two valid runs, newest end time first; the other files are skipped and counted, never moved.
        Assert.Equal([validCpu, validMemory], Kit.RunIds(all));
        Assert.Equal(
            ["history_unreadable=4", "history_future_format=2", "history_locked=1"],
            Kit.WarningLines(all));
        Assert.DoesNotContain(all.Warnings, w => w.Code is "history_quarantined" or "history_quarantine_failed");

        // Memory only: the file of another kind named like a memory file (body says storage) and the corrupt one are skipped.
        Assert.Equal([validMemory], Kit.RunIds(memoryOnly));
        Assert.Equal(["history_unreadable=2"], Kit.WarningLines(memoryOnly));

        // Limit 1: the visit goes newest first and stops at the first valid run (cpu, T11); every broken file is newer than it.
        Assert.Equal([validCpu], Kit.RunIds(limited));
        Assert.Equal(["history_unreadable=4", "history_future_format=2", "history_locked=1"], Kit.WarningLines(limited));

        // Get: found; a file that exists but cannot be read is Unreadable with its code, never "not found"; no file at all is not found.
        Assert.Equal(CheckHistoryGetStatus.Found, gotValid.Status);
        Assert.Equal(validMemory, gotValid.Result?.RunId);
        Assert.Null(gotValid.WarningCode);
        Assert.Equal(CheckHistoryGetStatus.Unreadable, gotCorrupt.Status);
        Assert.Equal("history_unreadable", gotCorrupt.WarningCode);
        Assert.Null(gotCorrupt.Result);
        Assert.Equal(CheckHistoryGetStatus.Unreadable, gotLocked.Status);
        Assert.Equal("history_locked", gotLocked.WarningCode);
        Assert.Equal(CheckHistoryGetStatus.NotFound, gotNothing.Status);
        Assert.Null(gotNothing.WarningCode);
        Assert.Null(gotNothing.Result);
    }

    [Fact]
    public void The_reasons_behind_the_warnings_are_the_same_as_the_stores_but_nothing_is_moved()
    {
        Populate();

        var scan = Kit.ReadOnly(Root).Scan(Kit.All, CancellationToken.None);

        var byName = scan.Problems.ToDictionary(p => p.FileName, p => (p.Code, p.Reason));
        Assert.Equal(6, scan.Problems.Count); // 1 corrupt + 3 disagreeing + 2 of a later version; nothing holds the "locked" file in this test
        Assert.Equal(("history_unreadable", "invalid_content"), byName[Kit.NameOf(CheckKind.Memory, T(20), Kit.Id(20))]);
        Assert.Equal(("history_unreadable", "name_time_mismatch"), byName[Kit.NameOf(CheckKind.Cpu, T(31), Kit.Id(30))]);
        Assert.Equal(("history_unreadable", "name_id_mismatch"), byName[Kit.NameOf(CheckKind.Cpu, T(32), Kit.Id(33))]);
        Assert.Equal(("history_unreadable", "name_kind_mismatch"), byName[Kit.NameOf(CheckKind.Memory, T(34), Kit.Id(34))]);
        Assert.Equal(("history_future_format", "future_format"), byName[Kit.NameOf(CheckKind.System, T(40), Kit.Id(40))]);
        Assert.Equal(("history_future_format", "future_kind"), byName[$"gpu-{Kit.Stamp(T(41))}-{Kit.Id(41):N}.json"]);
        Assert.All(scan.Problems, p =>
        {
            Assert.Null(p.QuarantinedAs);
            Assert.False(p.QuarantineFailed);
        });
    }

    [Fact]
    public async Task A_corrupt_file_the_mcp_server_skipped_is_still_there_for_the_gui_to_set_aside()
    {
        var corruptName = Kit.PutText(Root, Kit.NameOf(CheckKind.Memory, T(20), Kit.Id(20)), "{ corrupt");
        Kit.Put(Root, Kit.Make(CheckKind.Memory, T(10), Kit.Id(10)));

        var viaMcp = await Kit.ReadOnly(Root).ListAsync(Kit.All, CancellationToken.None);
        Assert.Equal(["history_unreadable=1"], Kit.WarningLines(viaMcp));
        Assert.Contains(corruptName, Kit.Names(Root));

        var viaGui = await Kit.Store(Root).ListAsync(Kit.All, CancellationToken.None);
        Assert.Equal(["history_unreadable=1", "history_quarantined=1"], Kit.WarningLines(viaGui));
        Assert.DoesNotContain(corruptName, Kit.Names(Root));
        Assert.Contains($"{corruptName}.bad-{Kit.NowStamp}", Kit.Names(Root));
    }

    [Fact]
    public async Task A_missing_folder_is_an_empty_history_and_is_not_created()
    {
        var page = await Kit.ReadOnly(Root).ListAsync(Kit.All, CancellationToken.None);

        Assert.Empty(page.Entries);
        Assert.Empty(page.Warnings);
        Assert.False(Directory.Exists(Root));
        Assert.Equal(CheckHistoryGetStatus.NotFound, (await Kit.ReadOnly(Root).GetAsync(Kit.Id(1), CancellationToken.None)).Status);
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_listing()
    {
        Populate();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.ReadOnly(Root).ListAsync(Kit.All, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.ReadOnly(Root).GetAsync(Kit.Id(10), cts.Token));
    }

    [Theory]
    [InlineData("access", "history_access_denied=1")]
    [InlineData("sharing", "history_locked=1")]
    [InlineData("other", "history_read_failed=1")]
    public async Task A_file_that_cannot_be_read_is_skipped_and_counted_never_set_aside(string fault, string expectedWarning)
    {
        var name = Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(10), Kit.Id(1)));
        Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(5), Kit.Id(2)));
        var files = new FaultFileSystem
        {
            ReadFault = path => Path.GetFileName(path) != name
                ? null
                : fault switch
                {
                    "access" => (Exception)new UnauthorizedAccessException("denied"),
                    "sharing" => Kit.SharingViolation(),
                    _ => new IOException("device error"),
                },
        };
        var before = Kit.Snapshot(Root);

        var page = await Kit.ReadOnly(Root, files).ListAsync(Kit.All, CancellationToken.None);

        Assert.Equal([Kit.Id(2)], Kit.RunIds(page));
        Assert.Equal([expectedWarning], Kit.WarningLines(page));
        Assert.Equal(before, Kit.Snapshot(Root));
        Assert.Equal(0, files.Count("move") + files.Count("delete") + files.Count("create") + files.Count("mkdir"));
    }

    // ---- the guarantee is in the types, not in a flag ----

    [Fact]
    public async Task Everything_the_mcp_history_asks_of_the_file_system_is_list_read_and_exists()
    {
        Populate();
        var reader = new RecordingReader();
        var history = Kit.ReadOnly(Root, reader);

        await history.ListAsync(Kit.All, CancellationToken.None);
        await history.GetAsync(Kit.Id(10), CancellationToken.None);
        await history.GetAsync(Kit.Id(20), CancellationToken.None);

        Assert.NotEmpty(reader.Calls);
        Assert.All(reader.Calls, call => Assert.Contains(call, new[] { "list", "read", "exists" }));
    }

    [Fact]
    public void The_reading_side_of_the_file_system_has_no_way_to_write()
    {
        var readerMethods = typeof(ICheckHistoryFileReader).GetMethods().Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(["Exists", "ListFiles", "ReadAllBytes"], readerMethods);

        // The writing side is a separate interface that extends it, and the MCP history never asks for it.
        Assert.True(typeof(ICheckHistoryFileReader).IsAssignableFrom(typeof(ICheckHistoryFileSystem)));
        Assert.False(typeof(ICheckHistoryFileSystem).IsAssignableFrom(typeof(ICheckHistoryFileReader)));

        // The real reading implementation is its own class: it is not the real writing one, and is not a writing file system.
        Assert.False(typeof(ICheckHistoryFileSystem).IsAssignableFrom(typeof(PhysicalHistoryFileReader)));
        Assert.Equal(["Exists", "ListFiles", "ReadAllBytes"], PublicInstanceMethodNames(typeof(PhysicalHistoryFileReader)));

        AssertCannotWrite(Kit.ReadOnly(Root));
        Assert.Equal(["GetAsync", "ListAsync"], typeof(IReadOnlyCheckHistory).GetMethods().Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    // ---- the one way in: CheckHistory.OpenReadOnly ----

    [Fact]
    public void OpenReadOnly_gives_the_read_only_history_and_that_object_has_no_path_to_a_writing_file_system()
    {
        var history = CheckHistory.OpenReadOnly(Root);

        Assert.IsType<ReadOnlyCheckHistory>(history);
        AssertCannotWrite(history);

        // Opening neither creates nor touches the folder.
        Assert.False(Directory.Exists(Root));
        Assert.Throws<ArgumentException>(() => CheckHistory.OpenReadOnly(" "));
        Assert.Throws<ArgumentNullException>(() => CheckHistory.OpenReadOnly(null!));
    }

    [Fact]
    public async Task OpenReadOnly_reads_the_same_history_as_the_stores_listing()
    {
        Populate();
        var before = Kit.Snapshot(Root);

        var viaFactory = await CheckHistory.OpenReadOnly(Root).ListAsync(Kit.All, CancellationToken.None);
        var viaKit = await Kit.ReadOnly(Root).ListAsync(Kit.All, CancellationToken.None);

        Assert.Equal(Kit.RunIds(viaKit), Kit.RunIds(viaFactory));
        Assert.Equal(Kit.WarningLines(viaKit), Kit.WarningLines(viaFactory));
        Assert.Equal(before, Kit.Snapshot(Root));
    }

    [Fact]
    public void The_store_is_not_a_read_only_history_and_nothing_else_public_is()
    {
        // The store (which saves, moves aside and deletes) can never be passed where the read-only history is expected.
        Assert.False(typeof(IReadOnlyCheckHistory).IsAssignableFrom(typeof(CheckHistoryStore)));
        Assert.DoesNotContain(typeof(IReadOnlyCheckHistory), typeof(CheckHistoryStore).GetInterfaces());
        Assert.False((object)Kit.Store(Root) is IReadOnlyCheckHistory);
        Assert.Contains(typeof(ICheckHistoryMaintainer), typeof(CheckHistoryStore).GetInterfaces());

        // And the other way: the read-only history cannot be cast to something that writes.
        Assert.False(typeof(ICheckHistoryMaintainer).IsAssignableFrom(typeof(ReadOnlyCheckHistory)));
        Assert.False(typeof(ICheckHistoryMaintainer).IsAssignableFrom(typeof(IReadOnlyCheckHistory)));

        // The only public implementation of the read-only interface is none: CheckHistory.OpenReadOnly is the only way to one.
        var exported = typeof(CheckHistory).Assembly.GetExportedTypes();
        Assert.DoesNotContain(exported, t => t.IsClass && !t.IsAbstract && typeof(IReadOnlyCheckHistory).IsAssignableFrom(t));
        Assert.Equal(
            ["OpenReadOnly"],
            typeof(CheckHistory).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Select(m => m.Name).ToArray());
        Assert.Equal(typeof(IReadOnlyCheckHistory), typeof(CheckHistory).GetMethod(nameof(CheckHistory.OpenReadOnly))!.ReturnType);
    }

    private const BindingFlags AllMembers = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static string[] PublicInstanceMethodNames(Type type) =>
        [.. type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal)];

    /// <summary>
    /// The guard: nothing the object is built from, directly or through other objects of this assembly, is a writing file system, a
    /// new-file handle or a store, and it offers no method that changes anything. Looks at the runtime object, so it applies to whatever
    /// a factory really returns.
    /// </summary>
    private static void AssertCannotWrite(object history)
    {
        var type = history.GetType();
        foreach (var constructor in type.GetConstructors(AllMembers))
        {
            Assert.DoesNotContain(constructor.GetParameters(), p => typeof(ICheckHistoryFileSystem).IsAssignableFrom(p.ParameterType));
        }

        foreach (var held in typeof(CheckHistoryScanner).GetConstructors(AllMembers))
        {
            Assert.DoesNotContain(held.GetParameters(), p => typeof(ICheckHistoryFileSystem).IsAssignableFrom(p.ParameterType));
        }

        // Its own methods are the listing, the read and the scan behind the listing.
        var methods = type.GetMethods(AllMembers)
            .Where(m => m.GetCustomAttribute<CompilerGeneratedAttribute>() is null && !m.Name.StartsWith('<'))
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["GetAsync", "ListAsync", "Scan"], methods);
        Assert.Contains(typeof(IReadOnlyCheckHistory), type.GetInterfaces());

        // The object graph: every object reachable through instance fields (the walk goes into objects of this assembly only).
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<object>();
        pending.Push(history);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            var currentType = current.GetType();
            Assert.False(current is ICheckHistoryFileSystem, $"reachable: {currentType.Name} is a writing file system");
            Assert.False(current is ICheckHistoryNewFile, $"reachable: {currentType.Name} is a new-file handle");
            Assert.False(current is CheckHistoryStore, $"reachable: {currentType.Name} is a store");
            Assert.False(current is ICheckHistoryMaintainer, $"reachable: {currentType.Name} is a maintainer");

            if (currentType.Assembly != typeof(CheckHistory).Assembly)
            {
                continue;
            }

            for (var t = currentType; t is not null && t != typeof(object); t = t.BaseType)
            {
                foreach (var field in t.GetFields(AllMembers).Where(f => !f.IsStatic))
                {
                    Assert.False(typeof(ICheckHistoryFileSystem).IsAssignableFrom(field.FieldType), $"{currentType.Name}.{field.Name}");
                    Assert.False(typeof(CheckHistoryStore).IsAssignableFrom(field.FieldType), $"{currentType.Name}.{field.Name}");
                    if (!field.FieldType.IsValueType && field.GetValue(current) is { } value)
                    {
                        pending.Push(value);
                    }
                }
            }
        }

        Assert.Contains(seen, o => o is ICheckHistoryFileReader);
    }
}
