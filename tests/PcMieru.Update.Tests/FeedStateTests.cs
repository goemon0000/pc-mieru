// Provenance: Round_trips_and_survives_garbage of StateTests and Writable_probe_leaves_nothing_behind of SwapperTests of the owner's pl-cloud (Parts Life, MIT, same owner;
// see NOTICE.txt) are ported; the lock, the id of an install, the refusal that is remembered and the placement kinds are new (DESIGN-B 4 and 5).
using System.Text.RegularExpressions;
using PcMieru.Update.Feed;

namespace PcMieru.Update.Tests;

/// <summary>state.json and check.lock of one install: written whole or not at all, read without ever throwing.</summary>
public sealed class UpdateStateStoreTests : IDisposable
{
    private readonly TempFolder _temp = new();

    private UpdateStateStore Store() => new(_temp.Sub("update-state"));

    public void Dispose() => _temp.Dispose();

    private static readonly DateTimeOffset T = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_names_are_those_of_the_design()
    {
        Assert.Equal("state.json", UpdateStateStore.StateFileName);
        Assert.Equal("check.lock", UpdateStateStore.LockFileName);
        var store = Store();
        Assert.Equal(Path.Combine(store.Folder, "state.json"), store.StatePath);
        Assert.Equal(Path.Combine(store.Folder, "check.lock"), store.LockPath);
    }

    // Ported from Round_trips_and_survives_garbage (the round trip half).
    [Fact]
    public void A_state_round_trips_and_a_missing_file_is_a_fresh_state()
    {
        var store = Store();
        Assert.Null(store.Read().LastCheckUtc);
        Assert.False(Directory.Exists(store.Folder)); // reading makes nothing

        var written = new UpdateState
        {
            LastCheckUtc = T,
            BackoffUntilUtc = T.AddHours(30),
            LatestVersion = "0.5.0",
            LatestPageUrl = UpdateEndpoints.ReleasePageUrl("v0.5.0"),
            LastRefusal = nameof(UpdateRefusal.AssetMissing),
        };
        Assert.True(store.TryWrite(written));

        var read = store.Read();
        Assert.Equal(written.LastCheckUtc, read.LastCheckUtc);
        Assert.Equal(written.BackoffUntilUtc, read.BackoffUntilUtc);
        Assert.Equal("0.5.0", read.LatestVersion);
        Assert.Equal(written.LatestPageUrl, read.LatestPageUrl);
        Assert.Equal("AssetMissing", read.LastRefusal);
    }

    [Fact]
    public void A_write_leaves_one_file_and_no_temporary_one_and_a_second_write_replaces_the_first()
    {
        var store = Store();
        Assert.True(store.TryWrite(new UpdateState { LastCheckUtc = T }));
        Assert.True(store.TryWrite(new UpdateState { LastCheckUtc = T.AddDays(1), LatestVersion = "0.6.0" }));

        Assert.Equal(new[] { UpdateStateStore.StateFileName }, Directory.GetFileSystemEntries(store.Folder).Select(e => Path.GetFileName(e)!).ToArray());
        var read = store.Read();
        Assert.Equal(T.AddDays(1), read.LastCheckUtc);
        Assert.Equal("0.6.0", read.LatestVersion);
    }

    // Ported from Round_trips_and_survives_garbage (the garbage half).
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"LastCheckUtc\": \"yesterday\"}")]
    [InlineData("{\"LastCheckUtc\": 12}")]
    public void A_file_that_is_not_a_state_reads_as_a_fresh_state_and_does_not_throw(string text)
    {
        var store = Store();
        Directory.CreateDirectory(store.Folder);
        File.WriteAllText(store.StatePath, text);

        var read = store.Read();

        Assert.Null(read.LastCheckUtc);
        Assert.Null(read.BackoffUntilUtc);
        Assert.Null(read.LatestVersion);
    }

    [Fact]
    public void A_file_over_its_limit_is_not_read_and_unknown_keys_are_ignored()
    {
        var store = Store();
        Directory.CreateDirectory(store.Folder);
        File.WriteAllText(store.StatePath, "{\"LatestVersion\": \"0.5.0\", \"pad\": \"" + new string('x', 70 * 1024) + "\"}");
        Assert.Null(store.Read().LatestVersion);

        File.WriteAllText(store.StatePath, "{\"LatestVersion\": \"0.5.0\", \"somethingNew\": [1, 2]}");
        Assert.Equal("0.5.0", store.Read().LatestVersion);
    }

    [Fact]
    public void A_state_that_cannot_be_written_says_so_and_leaves_no_temporary_file()
    {
        // state.json is a folder: the file cannot be put in place.
        var store = Store();
        Directory.CreateDirectory(store.StatePath);

        Assert.False(store.TryWrite(new UpdateState { LastCheckUtc = T }));

        Assert.False(File.Exists(store.StatePath + ".tmp"));
        Assert.Null(store.Read().LastCheckUtc);
    }

    [Fact]
    public void A_folder_that_is_a_file_cannot_be_written_or_locked()
    {
        Directory.CreateDirectory(_temp.Path);
        var folder = _temp.Sub("a-file");
        File.WriteAllText(folder, "in the way");
        var store = new UpdateStateStore(folder);

        Assert.False(store.TryWrite(new UpdateState { LastCheckUtc = T }));
        Assert.Equal(LockOutcome.Unavailable, store.TryLock(out var held));
        Assert.Null(held);
    }

    [Fact]
    public void The_lock_is_held_by_one_at_a_time_and_is_free_again_after_it_is_let_go()
    {
        var store = Store();
        var second = Store();

        Assert.Equal(LockOutcome.Acquired, store.TryLock(out var first));
        Assert.NotNull(first);
        Assert.Equal(LockOutcome.Busy, second.TryLock(out var none));
        Assert.Null(none);
        Assert.Equal(LockOutcome.Busy, store.TryLock(out _)); // not even the holder takes it twice

        first!.Dispose();

        Assert.Equal(LockOutcome.Acquired, second.TryLock(out var again));
        again!.Dispose();
    }

    [Fact]
    public void Reading_and_writing_the_state_do_not_need_the_lock()
    {
        var store = Store();
        Assert.Equal(LockOutcome.Acquired, store.TryLock(out var held));
        using (held)
        {
            Assert.True(store.TryWrite(new UpdateState { LastCheckUtc = T }));
            Assert.Equal(T, store.Read().LastCheckUtc);
        }
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, true)]
    [InlineData(5, false)]
    [InlineData(183, false)]
    [InlineData(2, false)]
    public void A_sharing_or_lock_violation_is_the_busy_signal_and_nothing_else_is(int win32, bool busy)
    {
        var ex = new IOException("test", unchecked((int)(0x80070000u | (uint)win32)));

        Assert.Equal(busy, UpdateStateStore.IsSharingViolation(ex));
    }
}

/// <summary>What a page may read from state.json: only what is well formed, because the user can edit the file.</summary>
public sealed class StatusTests
{
    private static UpdateStatus Status(Action<UpdateState> set)
    {
        var state = new UpdateState();
        set(state);
        return state.ToStatus();
    }

    [Fact]
    public void An_empty_state_is_the_empty_status()
    {
        Assert.Equal(UpdateStatus.Empty, new UpdateState().ToStatus());
    }

    [Fact]
    public void A_well_formed_candidate_is_read()
    {
        var status = Status(s =>
        {
            s.LatestVersion = "0.5.0";
            s.LatestPageUrl = UpdateEndpoints.ReleasePageUrl("v0.5.0");
            s.LastRefusal = "ManifestInvalid";
        });

        Assert.Equal(new AppVersion(0, 5, 0), status.CandidateVersion);
        Assert.Equal("https://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0", status.CandidatePageUrl);
        Assert.Equal(UpdateRefusal.ManifestInvalid, status.LastRefusal);
    }

    [Theory]
    [InlineData("v0.5.0")]
    [InlineData("0.5")]
    [InlineData("0.5.0-beta")]
    [InlineData("0.05.0")]
    [InlineData(" 0.5.0")]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData(null)]
    public void A_version_that_is_not_spelled_X_Y_Z_is_no_candidate(string? version)
    {
        var status = Status(s =>
        {
            s.LatestVersion = version;
            s.LatestPageUrl = UpdateEndpoints.ReleasePageUrl("v0.5.0");
        });

        Assert.Null(status.CandidateVersion);
        Assert.Null(status.CandidatePageUrl); // a page without a candidate is not offered either
    }

    [Theory]
    [InlineData("https://evil.example/goemon0000/pc-mieru/releases/tag/v0.5.0")]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/tag/v0.4.9")]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0?x=1")]
    [InlineData("http://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("calc.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void A_page_that_is_not_this_repos_page_of_that_tag_is_not_offered_to_the_gui(string? page)
    {
        var status = Status(s =>
        {
            s.LatestVersion = "0.5.0";
            s.LatestPageUrl = page;
        });

        Assert.Equal(new AppVersion(0, 5, 0), status.CandidateVersion);
        Assert.Null(status.CandidatePageUrl);
    }

    [Theory]
    [InlineData("AssetMissing", UpdateRefusal.AssetMissing)]
    [InlineData("BlockedTag", UpdateRefusal.BlockedTag)]
    [InlineData("assetmissing", UpdateRefusal.None)]
    [InlineData("Bogus", UpdateRefusal.None)]
    [InlineData("9999", UpdateRefusal.None)]
    [InlineData("", UpdateRefusal.None)]
    [InlineData(null, UpdateRefusal.None)]
    public void A_remembered_refusal_is_one_of_the_known_names_or_none(string? text, UpdateRefusal expected)
    {
        Assert.Equal(expected, Status(s => s.LastRefusal = text).LastRefusal);
    }
}

/// <summary>Which install a state folder belongs to.</summary>
public sealed class InstallIdTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string File1(string folder, string name, string text = "x")
    {
        var dir = _temp.Sub(folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        if (name == "PCMieru.exe")
        {
            // An install has its gate file beside the program (step 3): the id and so the state folder are the gate file's.
            File.WriteAllBytes(Path.Combine(dir, PcMieru.Wire.Gate.GateFile.FileName), PcMieru.Wire.Gate.GateFile.Bytes());
        }

        return path;
    }

    [Fact]
    public void The_id_is_eight_hex_digits_of_the_volume_a_dash_and_sixteen_of_the_file()
    {
        var id = InstallId.OfFile(File1("a", "PCMieru.exe"));

        Assert.NotNull(id);
        Assert.Matches(new Regex("^[0-9a-f]{8}-[0-9a-f]{16}$"), id);
    }

    [Fact]
    public void The_same_file_has_the_same_id_however_it_is_spelled()
    {
        var path = File1("a", "PCMieru.exe");
        var around = Path.Combine(_temp.Sub("a"), "..", "a", ".", "PCMieru.exe");
        var upper = path.ToUpperInvariant();

        Assert.Equal(InstallId.OfFile(path), InstallId.OfFile(path));
        Assert.Equal(InstallId.OfFile(path), InstallId.OfFile(around));
        Assert.Equal(InstallId.OfFile(path), InstallId.OfFile(upper));
        Assert.Equal(InstallId.OfFile(Path.Combine(_temp.Sub("a"), PcMieru.Wire.Gate.GateFile.FileName)), InstallId.OfInstall(path));
    }

    [Fact]
    public void The_id_of_an_install_is_the_id_of_its_gate_file_and_not_of_the_program()
    {
        var program = File1("a", "PCMieru.exe");
        var gate = Path.Combine(_temp.Sub("a"), PcMieru.Wire.Gate.GateFile.FileName);

        Assert.Equal(InstallId.GateFileOf(program), gate);
        Assert.Equal(InstallId.OfFile(gate), InstallId.OfInstall(program));
        Assert.NotEqual(InstallId.OfFile(program), InstallId.OfInstall(program));

        // An update replaces the program; the id (and the state folder under it) does not move.
        var before = InstallId.OfInstall(program);
        File.Delete(program);
        File.WriteAllText(program, "the next version");
        Assert.Equal(before, InstallId.OfInstall(program));
    }

    [Fact]
    public void A_folder_without_the_gate_file_has_no_id_and_no_state_folder_there_is_no_fallback_to_the_program()
    {
        var program = File1("a", "PCMieru.exe");
        File.Delete(Path.Combine(_temp.Sub("a"), PcMieru.Wire.Gate.GateFile.FileName));

        Assert.NotNull(InstallId.OfFile(program)); // the exe itself is there and has a file id ...
        Assert.Null(InstallId.OfInstall(program)); // ... and still it is not an id of the install
        Assert.Null(InstallId.StateFolder(_temp.Sub("local"), program));
        Assert.Null(InstallId.GateFileOf(""));
        Assert.Null(InstallId.GateFileOf("PCMieru.exe"));
    }

    [Fact]
    public void Two_copies_of_the_program_in_two_folders_have_two_ids_even_with_the_same_bytes()
    {
        var one = File1("one", "PCMieru.exe", "same bytes");
        var two = File1("two", "PCMieru.exe", "same bytes");

        Assert.NotEqual(InstallId.OfFile(one), InstallId.OfFile(two));
    }

    [Fact]
    public void A_file_that_is_not_there_or_is_a_folder_has_no_id()
    {
        Assert.Null(InstallId.OfFile(_temp.Sub("nothing.exe")));
        Directory.CreateDirectory(_temp.Sub("a-folder"));
        Assert.Null(InstallId.OfFile(_temp.Sub("a-folder")));
        Assert.Null(InstallId.OfFile(""));
        Assert.Null(InstallId.OfFile("not a path |<>"));
    }

    [Fact]
    public void The_state_folder_is_below_the_local_folder_update_and_the_id_or_nothing()
    {
        var program = File1("a", "PCMieru.exe");
        var local = _temp.Sub("local");

        var folder = InstallId.StateFolder(local, program);

        Assert.Equal(Path.Combine(local, "update", InstallId.OfInstall(program)!), folder);
        Assert.Null(InstallId.StateFolder(local, _temp.Sub("missing.exe")));
        Assert.False(Directory.Exists(local)); // asking makes nothing
    }

    [Fact]
    public void Two_installs_get_two_state_folders_and_so_two_locks()
    {
        var local = _temp.Sub("local");
        var one = new UpdateStateStore(InstallId.StateFolder(local, File1("one", "PCMieru.exe"))!);
        var two = new UpdateStateStore(InstallId.StateFolder(local, File1("two", "PCMieru.exe"))!);

        Assert.Equal(LockOutcome.Acquired, one.TryLock(out var a));
        using (a)
        {
            Assert.Equal(LockOutcome.Acquired, two.TryLock(out var b));
            b!.Dispose();
        }
    }
}

/// <summary>The writability rule of DESIGN-B 5: a probe with the normal token, on a fixed drive, not elevated.</summary>
public sealed class InstallPlacementTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Install(bool withUpdateFolder = false)
    {
        var dir = _temp.Sub("I");
        Directory.CreateDirectory(dir);
        if (withUpdateFolder)
        {
            Directory.CreateDirectory(Path.Combine(dir, InstallPlacement.UpdateFolderName));
        }

        return dir;
    }

    private static Placement Check(string folder, bool elevated = false, DriveType drive = DriveType.Fixed) =>
        InstallPlacement.Check(folder, () => elevated, _ => drive);

    [Fact]
    public void A_writable_folder_on_a_fixed_drive_can_be_swapped_and_the_probe_leaves_nothing_behind()
    {
        // Ported from Writable_probe_leaves_nothing_behind.
        var folder = Install(withUpdateFolder: true);

        var placement = Check(folder);

        Assert.Equal(PlacementKind.CanSwap, placement.Kind);
        Assert.True(placement.CanSwap);
        var strays = Directory.GetFileSystemEntries(folder).Where(e => !string.Equals(Path.GetFileName(e), InstallPlacement.UpdateFolderName, StringComparison.Ordinal)).ToList();
        Assert.Empty(strays);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(folder, InstallPlacement.UpdateFolderName)));
    }

    [Fact]
    public void An_elevated_gui_is_notify_only_even_where_the_folder_is_writable()
    {
        var placement = Check(Install(), elevated: true);

        Assert.Equal(PlacementKind.Elevated, placement.Kind);
        Assert.False(placement.CanSwap);
        Assert.Empty(Directory.GetFileSystemEntries(Install()));
    }

    [Theory]
    [InlineData(DriveType.Removable)]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.CDRom)]
    [InlineData(DriveType.Ram)]
    [InlineData(DriveType.Unknown)]
    [InlineData(DriveType.NoRootDirectory)]
    public void A_drive_that_is_not_a_fixed_disk_is_notify_only_and_is_not_even_probed(DriveType drive)
    {
        var folder = Install();

        var placement = Check(folder, drive: drive);

        Assert.Equal(PlacementKind.NotFixedDrive, placement.Kind);
        Assert.False(placement.CanSwap);
        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    [Fact]
    public void An_elevated_gui_wins_over_a_removable_drive()
    {
        Assert.Equal(PlacementKind.Elevated, Check(Install(), elevated: true, drive: DriveType.Removable).Kind);
    }

    [Fact]
    public void A_folder_that_cannot_be_written_is_read_only_with_the_folder_named_in_the_detail()
    {
        // A file where the folder should be: a file cannot be created below it.
        Directory.CreateDirectory(_temp.Path);
        var notAFolder = _temp.Sub("I-is-a-file");
        File.WriteAllText(notAFolder, "x");

        var placement = Check(notAFolder);

        Assert.Equal(PlacementKind.ReadOnly, placement.Kind);
        Assert.False(placement.CanSwap);
        Assert.Contains(notAFolder, placement.Detail, StringComparison.Ordinal);
        Assert.Equal(new[] { "I-is-a-file" }, Directory.GetFileSystemEntries(_temp.Path).Select(e => Path.GetFileName(e)!).ToArray());
    }

    [Fact]
    public void A_missing_folder_is_read_only()
    {
        Assert.Equal(PlacementKind.ReadOnly, Check(_temp.Sub("not-there")).Kind);
    }

    [Fact]
    public void Success_is_not_remembered_every_call_looks_again()
    {
        var folder = Install();
        Assert.Equal(PlacementKind.CanSwap, Check(folder).Kind);

        Directory.Delete(folder);

        Assert.Equal(PlacementKind.ReadOnly, Check(folder).Kind);
    }

    [Fact]
    public void A_probe_of_a_good_folder_is_null_and_of_a_bad_one_says_what_went_wrong()
    {
        Assert.Null(InstallPlacement.Probe(Install()));
        Assert.NotNull(InstallPlacement.Probe(_temp.Sub("not-there")));
    }

    [Fact]
    public void The_real_questions_do_not_call_a_temp_folder_a_removable_drive_and_do_not_leave_files()
    {
        // The real overload asks Windows: elevated or not, a folder under the temp path is on a fixed drive and is writable.
        var folder = Install();

        var placement = InstallPlacement.Check(folder);

        Assert.NotEqual(PlacementKind.NotFixedDrive, placement.Kind);
        Assert.NotEqual(PlacementKind.ReadOnly, placement.Kind);
        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }
}

/// <summary>The five ways a check ends, and what each carries.</summary>
public sealed class UpdateCheckTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_candidate_says_whether_anything_was_asked()
    {
        var asked = UpdateCheck.NoCandidate(fetched: true);
        var silent = UpdateCheck.NoCandidate(fetched: false);

        Assert.Equal(UpdateCheckKind.NoCandidate, asked.Kind);
        Assert.True(asked.Fetched);
        Assert.False(silent.Fetched);
        Assert.Equal("no candidate", asked.ToString());
        Assert.Contains("nothing asked", silent.ToString(), StringComparison.Ordinal);
        Assert.Null(asked.Version);
        Assert.Equal(UpdateRefusal.None, asked.Refusal);
    }

    [Fact]
    public void A_candidate_carries_the_version_the_page_the_manifest_and_the_zip_it_never_asked_for()
    {
        var bundle = new TestBundle("0.5.0");
        var zip = UpdateEndpoints.AssetUrl("v0.5.0", "PCMieru-0.5.0-win-x64.zip");

        var found = UpdateCheck.Found(new AppVersion(0, 5, 0), UpdateEndpoints.ReleasePageUrl("v0.5.0"), bundle.Outer(), zip, new string('a', 64));

        Assert.Equal(UpdateCheckKind.Candidate, found.Kind);
        Assert.Equal(new AppVersion(0, 5, 0), found.Version);
        Assert.Equal("0.5.0", found.Manifest!.Version);
        Assert.Equal(zip, found.ZipUrl);
        Assert.Equal(new string('a', 64), found.ZipSha256);
        Assert.True(found.Fetched);
        Assert.True(UpdateEndpoints.IsReleasePage(found.PageUrl));
        Assert.Equal("candidate 0.5.0", found.ToString());
    }

    [Fact]
    public void A_refusal_carries_its_reason_and_a_detail_for_the_log()
    {
        var refused = UpdateCheck.Refuse(UpdateRefusal.AssetMissing, "SHA256.txt is not on the release", fetched: true);

        Assert.Equal(UpdateCheckKind.Refused, refused.Kind);
        Assert.Equal(UpdateRefusal.AssetMissing, refused.Refusal);
        Assert.Equal("SHA256.txt is not on the release", refused.Detail);
        Assert.True(refused.Fetched);
        Assert.Contains("AssetMissing", refused.ToString(), StringComparison.Ordinal);
        Assert.False(UpdateCheck.Refuse(UpdateRefusal.AnotherCheckRunning, "x", fetched: false).Fetched);
    }

    [Fact]
    public void A_wait_carries_its_end_and_a_not_allowed_carries_its_mode()
    {
        var waiting = UpdateCheck.Wait(T, fetched: true);
        var closed = UpdateCheck.NotAllowed(UpdateMode.Ask);

        Assert.Equal(UpdateCheckKind.Waiting, waiting.Kind);
        Assert.Equal(T, waiting.WaitUntil);
        Assert.Equal(UpdateCheckKind.NotAllowed, closed.Kind);
        Assert.Equal(UpdateMode.Ask, closed.Mode);
        Assert.False(closed.Fetched);
        Assert.Contains("Ask", closed.ToString(), StringComparison.Ordinal);
        Assert.Contains("waiting", waiting.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_refusal_has_its_own_name_and_none_is_the_zero()
    {
        var values = Enum.GetValues<UpdateRefusal>();

        Assert.Equal(UpdateRefusal.None, default(UpdateRefusal));
        Assert.Equal(values.Length, values.Select(v => v.ToString()).Distinct(StringComparer.Ordinal).Count());
    }
}
