// Provenance: the idea of "hash mismatch stages nothing / a bad signature stops the update" is that of Hash_mismatch_stages_nothing and the Authenticode tests of the owner's pl-cloud
// tests/PartsLife.Update.Tests (Parts Life, MIT, same owner; see NOTICE.txt). The tests are new: they stage into a scratch state folder from a fake GitHub (nothing real is asked),
// count the requests per asset, and look at what is left on disk.
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using PcMieru.Update.Bundle;
using PcMieru.Update.Feed;
using PcMieru.Update.Staging;
using PcMieru.Update.Transaction;

namespace PcMieru.Update.Tests;

/// <summary>A signature inspector that answers from a table and remembers which bundle files it was asked about.</summary>
internal sealed class ScriptedInspector : ISignatureInspector
{
    private readonly object _gate = new();
    private readonly List<string> _asked = [];

    public Func<string, SignatureInfo> Answer { get; set; } = _ => SignatureInfo.Unsigned;

    /// <summary>The bundle-relative names it was asked about, in order.</summary>
    public IReadOnlyList<string> Asked
    {
        get
        {
            lock (_gate)
            {
                return [.. _asked];
            }
        }
    }

    public SignatureInfo Inspect(string path)
    {
        var marker = Path.DirectorySeparatorChar + StageLayout.FilesFolderName + Path.DirectorySeparatorChar;
        var name = path[(path.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase) + marker.Length)..].Replace('\\', '/');
        lock (_gate)
        {
            _asked.Add(name);
        }

        return Answer(name);
    }
}

/// <summary>Free space that a test sets.</summary>
internal sealed class FakeDisk(long free) : IDiskSpace
{
    public long Free { get; set; } = free;

    public long FreeBytes(string folder) => Free;
}

/// <summary>A <see cref="FeedRig"/> that can stage: an install folder, a placement that allows a swap, a scripted signature inspector and plenty of room.</summary>
internal sealed class StageRig : IDisposable
{
    public StageRig()
    {
        Feed = new FeedRig();
        Install = Path.Combine(Feed.Temp, "install");
        Directory.CreateDirectory(Install);
        Feed.InstallFolder = Install;
        Feed.PlacementOf = _ => new Placement(PlacementKind.CanSwap, "test");
        Feed.Inspector = Inspector;
        Feed.Disk = Disk;
    }

    public FeedRig Feed { get; }

    public string Install { get; }

    public ScriptedInspector Inspector { get; } = new();

    public FakeDisk Disk { get; } = new(long.MaxValue / 4);

    public string Cache => StageLayout.CacheOf(Feed.StateFolder);

    public FakeGitHub Gh => Feed.Gh;

    /// <summary>The folders that are in the cache now.</summary>
    public string[] CacheFolders() => Directory.Exists(Cache) ? [.. Directory.GetFileSystemEntries(Cache).Order(StringComparer.Ordinal)] : [];

    /// <summary>Publishes a release (default 0.5.0) and runs a check in <paramref name="mode"/>; the result must be a candidate.</summary>
    public async Task<(FakeRelease Release, UpdateCheck Check)> Found(UpdateMode mode = UpdateMode.Automatic, string version = "0.5.0", Action<FakeRelease>? edit = null)
    {
        var release = Feed.Release(version, edit);
        var check = await Feed.Check(mode, manual: true);
        Assert.Equal(UpdateCheckKind.Candidate, check.Kind);
        return (release, check);
    }

    public Task<UpdateStageResult> Stage(UpdateCheck check, UpdateMode mode = UpdateMode.Automatic, CancellationToken ct = default) =>
        Feed.Service().StageAsync(mode, check, ct);

    public string ZipUrl(FakeRelease release) => release.AssetUrl(release.ZipName);

    public int ZipRequests(FakeRelease release) => Gh.Urls.Count(u => u == ZipUrl(release));

    public void Dispose() => Feed.Dispose();
}

/// <summary>The stage (DESIGN-B 3.3): what it asks for, what it leaves, what it refuses, and what it does about what an earlier one left.</summary>
[Collection(FeedCollection.Name)]
public sealed class StageTests : IDisposable
{
    private readonly StageRig _r = new();

    public void Dispose() => _r.Dispose();

    private static StagedUpdate Staged(UpdateStageResult result)
    {
        Assert.Equal(UpdateStageKind.Staged, result.Kind);
        return result.Staged!;
    }

    // ---------------------------------------------------------------------------------------------------------------- only Automatic downloads

    [Theory]
    [InlineData(UpdateMode.Notify)]
    [InlineData(UpdateMode.Ask)]
    [InlineData(UpdateMode.Off)]
    public async Task Notify_Ask_and_Off_never_ask_for_the_zip_whatever_the_candidate(UpdateMode mode)
    {
        var (_, check) = await _r.Found(UpdateMode.Notify);
        var requests = _r.Gh.Count;
        var clients = _r.Feed.ClientsMade;

        var result = await _r.Stage(check, mode);

        Assert.Equal(UpdateStageKind.NotAttempted, result.Kind);
        Assert.Equal(requests, _r.Gh.Count);
        Assert.False(_r.Gh.AskedForAZip);
        Assert.Equal(clients, _r.Feed.ClientsMade); // not even a client was made
        Assert.Empty(_r.CacheFolders());
        Assert.Null(_r.Feed.State().Staged);
    }

    [Fact]
    public async Task A_check_in_Automatic_mode_is_still_only_three_requests_and_never_the_zip()
    {
        await _r.Found(UpdateMode.Automatic);

        Assert.Equal(3, _r.Gh.Count);
        Assert.False(_r.Gh.AskedForAZip);
    }

    [Fact]
    public async Task A_candidate_that_a_check_did_not_validate_is_not_staged()
    {
        var (release, found) = await _r.Found(UpdateMode.Notify);
        var requests = _r.Gh.Count;
        var handBuilt = UpdateCheck.Found(found.Version!.Value, found.PageUrl!, release.Bundle.Outer(), new Uri(_r.ZipUrl(release)), FakeRelease.Hex(release.ZipBytes));

        Assert.Equal(UpdateStageKind.NotAttempted, (await _r.Stage(handBuilt)).Kind);
        Assert.Equal(UpdateStageKind.NotAttempted, (await _r.Stage(UpdateCheck.NoCandidate(fetched: true))).Kind);
        Assert.Equal(UpdateStageKind.NotAttempted, (await _r.Stage(UpdateCheck.Refuse(UpdateRefusal.BlockedTag, "x", fetched: true))).Kind);
        Assert.Equal(UpdateStageKind.NotAttempted, (await _r.Stage(UpdateCheck.NotAllowed(UpdateMode.Off))).Kind);
        Assert.Equal(requests, _r.Gh.Count);
    }

    [Theory]
    [InlineData(PlacementKind.ReadOnly)]
    [InlineData(PlacementKind.Elevated)]
    [InlineData(PlacementKind.NotFixedDrive)]
    [InlineData(PlacementKind.NotNtfs)]
    public async Task An_install_that_cannot_take_a_swap_does_not_stage(PlacementKind kind)
    {
        var (_, check) = await _r.Found();
        _r.Feed.PlacementOf = _ => new Placement(kind, "test");

        var result = await _r.Stage(check);

        Assert.Equal(UpdateStageKind.NotAttempted, result.Kind);
        Assert.False(_r.Gh.AskedForAZip);
        Assert.Empty(_r.CacheFolders());
    }

    [Fact]
    public async Task A_service_without_an_install_folder_does_not_stage()
    {
        var (_, check) = await _r.Found();
        _r.Feed.InstallFolder = null;

        Assert.Equal(UpdateStageKind.NotAttempted, (await _r.Stage(check)).Kind);
        Assert.False(_r.Gh.AskedForAZip);
    }

    // ---------------------------------------------------------------------------------------------------------------- the stage itself

    [Fact]
    public async Task Automatic_stages_with_exactly_one_more_request_the_zip_and_leaves_only_the_unpacked_files()
    {
        var (release, check) = await _r.Found();

        var result = await _r.Stage(check);

        var staged = Staged(result);
        Assert.False(result.Reused);
        Assert.Equal(4, _r.Gh.Count);
        Assert.Equal(_r.ZipUrl(release), _r.Gh.Urls[3]);
        Assert.Equal(1, _r.ZipRequests(release));

        // The request of the zip carries no credentials: a user agent and an accept, nothing else.
        var zipRequest = _r.Gh.Seen[3];
        Assert.Equal(["Accept", "User-Agent"], zipRequest.HeaderNames);
        Assert.False(zipRequest.HadBody);

        Assert.Equal("0.5.0", staged.Version);
        Assert.Equal("v0.5.0", staged.Tag);
        Assert.Equal(release.Bundle.BuildId, staged.BuildId);
        Assert.Equal(FakeRelease.Hex(release.ManifestBytes), staged.ManifestSha256);
        Assert.Equal(_r.Cache, Path.GetDirectoryName(staged.Folder));
        Assert.True(TxPaths.IsValidTx(Path.GetFileName(staged.Folder)));
        Assert.Equal([staged.Folder], _r.CacheFolders());

        // What is in the folder: the two small files as they were read, the unpacked files, and no zip (the swap takes the files, not the zip).
        Assert.Equal(release.ShaBytes(), File.ReadAllBytes(Path.Combine(staged.Folder, StageLayout.HashListFileName)));
        Assert.Equal(release.ManifestBytes, File.ReadAllBytes(Path.Combine(staged.Folder, StageLayout.ManifestFileName)));
        Assert.False(File.Exists(Path.Combine(staged.Folder, StageLayout.ZipFileName)));
        Assert.False(File.Exists(Path.Combine(staged.Folder, StageLayout.ZipPartName)));
        foreach (var (name, data) in release.Bundle.Files)
        {
            Assert.Equal(data, File.ReadAllBytes(TxPaths.Under(staged.FilesFolder(), name)));
        }

        Assert.Equal(release.Bundle.InnerBytes(), File.ReadAllBytes(Path.Combine(staged.FilesFolder(), BundleFormat.InnerManifestName)));
        Assert.True(StageCache.IsIntact(_r.Feed.StateFolder, staged));

        // It is recorded in state.json, and the status says it.
        Assert.Equal(staged, _r.Feed.State().Staged);
        Assert.Equal(staged, _r.Feed.Service().ReadStatus().Staged);
    }

    [Fact]
    public async Task Every_program_file_is_looked_at_when_nothing_is_recorded_as_installed_and_only_the_changed_ones_when_something_is()
    {
        var (release, check) = await _r.Found();
        var programs = release.Bundle.Entries().Select(e => e.Name[(release.Bundle.Root.Length + 1)..]).Where(SignaturePolicy.IsProgramFile).Order(StringComparer.Ordinal).ToList();
        Assert.True(programs.Count > 5);

        Staged(await _r.Stage(check));
        Assert.Equal(programs, _r.Inspector.Asked.Order(StringComparer.Ordinal));

        // With installed.json: everything the same as installed is skipped, one program changed and one is new are looked at.
        _r.Feed.Service().DiscardStage();
        var installedFiles = release.Bundle.Outer().Files.Select(f => new InstalledFile(f.Path, f.Bytes, f.Sha256)).ToList();
        var changed = installedFiles.FindIndex(f => f.Path == "System.Runtime.dll");
        installedFiles[changed] = installedFiles[changed] with { Sha256 = new string('0', 64) };
        installedFiles.RemoveAll(f => f.Path == "createdump.exe"); // not installed yet: new
        var paths = new TxPaths(_r.Install);
        Directory.CreateDirectory(paths.Update);
        File.WriteAllBytes(paths.Installed, new InstalledRecord("0.4.0", release.Bundle.BuildId, installedFiles).ToUtf8Bytes());
        var before = _r.Inspector.Asked.Count;

        Staged(await _r.Stage(check));

        Assert.Equal(["System.Runtime.dll", "createdump.exe"], _r.Inspector.Asked.Skip(before).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Valid_and_Unsigned_pass_and_the_signer_is_not_compared_yet()
    {
        var (_, check) = await _r.Found();
        _r.Inspector.Answer = name => name == "System.Runtime.dll" ? new SignatureInfo(SignatureKind.Valid, "CN=Somebody Else") : SignatureInfo.Unsigned;

        Staged(await _r.Stage(check));
    }

    // ---------------------------------------------------------------------------------------------------------------- refusals

    [Fact]
    public async Task A_zip_that_is_not_the_one_SHA256_names_is_refused_removed_and_asked_again_next_time()
    {
        var (release, check) = await _r.Found();
        _r.Gh.Bytes(_r.ZipUrl(release), release.Bundle.Zip(e => e[0].Data = Encoding.ASCII.GetBytes("another file entirely")));

        var refused = await _r.Stage(check);

        Assert.Equal(UpdateStageKind.Refused, refused.Kind);
        Assert.Equal(UpdateRefusal.ZipHashMismatch, refused.Reason);
        Assert.Empty(_r.CacheFolders());
        Assert.Null(_r.Feed.State().Staged);
        Assert.Null(_r.Feed.State().StageRefusedTag); // a cut transfer or an upload in progress is not held against the release

        // The right file is there on the next try: that one is staged (and asked for again).
        _r.Gh.Bytes(_r.ZipUrl(release), release.ZipBytes);
        Staged(await _r.Stage(check));
        Assert.Equal(2, _r.ZipRequests(release));
    }

    [Fact]
    public async Task A_zip_the_API_digest_disagrees_with_is_refused_even_when_SHA256_txt_agrees_with_it()
    {
        // SHA256.txt names the zip that is served; the API's digest names another. (The check compares the digest to SHA256.txt and refuses first, so the stage sees it only when the API changed between the two.)
        var (release, check) = await _r.Found(edit: r => r.WithDigests = true);
        var other = release.Bundle.Zip(e => e[0].Data = Encoding.ASCII.GetBytes("changed"));
        _r.Gh.Bytes(_r.ZipUrl(release), other);

        var refused = await _r.Stage(check);

        Assert.Equal(UpdateRefusal.ZipHashMismatch, refused.Reason);
        Assert.Empty(_r.CacheFolders());
    }

    [Fact]
    public async Task A_zip_whose_contents_are_not_its_manifest_is_refused_removed_and_not_asked_for_again_until_the_zip_changes()
    {
        var (release, check) = await _r.Found(edit: r => r.ZipBytes = r.Bundle.Zip(e => e.First(x => x.Name.EndsWith("/NOTICE.txt", StringComparison.Ordinal)).Data = Encoding.ASCII.GetBytes("a different notice")));

        var refused = await _r.Stage(check);

        Assert.Equal(UpdateStageKind.Refused, refused.Kind);
        Assert.Equal(UpdateRefusal.BundleInvalid, refused.Reason);
        Assert.Empty(_r.CacheFolders());
        Assert.Null(_r.Feed.State().Staged);
        Assert.Equal("v0.5.0", _r.Feed.State().StageRefusedTag);
        Assert.Equal(1, _r.ZipRequests(release));

        // The same release is not downloaded again.
        var again = await _r.Stage(check);
        Assert.Equal(UpdateRefusal.BundleInvalid, again.Reason);
        Assert.Equal(1, _r.ZipRequests(release));

        // The page says why the candidate is not used.
        var status = _r.Feed.Service().ReadStatus();
        Assert.Equal(UpdateRefusal.BundleInvalid, status.LastRefusal);

        // The owner publishes a corrected zip under the same tag (a new SHA256.txt): that one is tried.
        var fixedRelease = _r.Feed.Release("0.5.0");
        var next = await _r.Feed.Check(UpdateMode.Automatic, manual: true);
        Assert.Equal(UpdateCheckKind.Candidate, next.Kind);
        Staged(await _r.Stage(next));
        Assert.Equal(2, _r.ZipRequests(fixedRelease));
        Assert.Null(_r.Feed.State().StageRefusedTag);
    }

    [Fact]
    public async Task A_program_with_a_broken_signature_refuses_the_whole_candidate_and_is_remembered()
    {
        var (release, check) = await _r.Found();
        _r.Inspector.Answer = name => name == "System.Runtime.dll" ? new SignatureInfo(SignatureKind.Invalid, null) : SignatureInfo.Unsigned;

        var refused = await _r.Stage(check);

        Assert.Equal(UpdateRefusal.SignatureInvalid, refused.Reason);
        Assert.Empty(_r.CacheFolders());
        Assert.Null(_r.Feed.State().Staged);
        Assert.Equal("v0.5.0", _r.Feed.State().StageRefusedTag);
        Assert.Equal(UpdateRefusal.SignatureInvalid, _r.Feed.Service().ReadStatus().LastRefusal);

        Assert.Equal(UpdateRefusal.SignatureInvalid, (await _r.Stage(check)).Reason);
        Assert.Equal(1, _r.ZipRequests(release));
    }

    [Fact]
    public async Task A_stage_refusal_is_forgotten_when_the_install_has_outgrown_the_release()
    {
        var (_, check) = await _r.Found();
        _r.Inspector.Answer = _ => new SignatureInfo(SignatureKind.Invalid, null);
        Assert.Equal(UpdateRefusal.SignatureInvalid, (await _r.Stage(check)).Reason);

        _r.Feed.Service().Tidy(new AppVersion(0, 5, 0));

        Assert.Null(_r.Feed.State().StageRefusedTag);
        Assert.Null(_r.Feed.State().StageRefusedReason);
        Assert.Null(_r.Feed.State().StageRefusedZipSha256);
    }

    // ---------------------------------------------------------------------------------------------------------------- failures of this PC and of the network

    [Fact]
    public async Task A_stage_that_cannot_get_the_lock_gives_way_and_asks_for_nothing()
    {
        var (release, check) = await _r.Found();
        Assert.Equal(LockOutcome.Acquired, _r.Feed.Store.TryLock(out var held));
        using (held)
        {
            var refused = await _r.Stage(check);

            Assert.Equal(UpdateRefusal.AnotherCheckRunning, refused.Reason);
            Assert.Equal(0, _r.ZipRequests(release));
        }

        Staged(await _r.Stage(check));
    }

    [Fact]
    public async Task Not_enough_room_refuses_before_the_zip_is_asked_for_and_leaves_nothing()
    {
        var (release, check) = await _r.Found();
        _r.Disk.Free = 1024;

        var refused = await _r.Stage(check);

        Assert.Equal(UpdateRefusal.StageFailed, refused.Reason);
        Assert.Equal(0, _r.ZipRequests(release));
        Assert.Empty(_r.CacheFolders());
        Assert.Null(_r.Feed.State().StageRefusedTag); // a full disk is not held against the release
    }

    [Fact]
    public async Task A_cache_that_is_a_plain_file_is_a_failure_of_this_PC_and_asks_for_nothing()
    {
        var (release, check) = await _r.Found();
        File.WriteAllText(_r.Cache, "in the way");

        var refused = await _r.Stage(check);

        Assert.Equal(UpdateRefusal.StageFailed, refused.Reason);
        Assert.Equal(0, _r.ZipRequests(release));
        Assert.Equal("in the way", File.ReadAllText(_r.Cache)); // not removed: it is not ours
    }

    [Fact]
    public async Task A_cancelled_stage_throws_and_leaves_no_folder()
    {
        var (release, check) = await _r.Found();
        using var cts = new CancellationTokenSource();
        _r.Gh.Handle(_r.ZipUrl(release), async ct =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _r.Stage(check, ct: cts.Token));

        Assert.Empty(_r.CacheFolders());
        Assert.Null(_r.Feed.State().Staged);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_zip_that_the_server_does_not_give_is_a_failure_that_is_tried_again_at_the_next_check(HttpStatusCode code)
    {
        var (release, check) = await _r.Found();
        _r.Gh.Status(_r.ZipUrl(release), code);

        var refused = await _r.Stage(check);

        Assert.Equal(UpdateStageKind.Refused, refused.Kind);
        Assert.True(UpdateRefusals.IsOperational(refused.Reason), refused.Reason.ToString());
        Assert.Empty(_r.CacheFolders());
        Assert.Null(_r.Feed.State().StageRefusedTag);

        _r.Gh.Bytes(_r.ZipUrl(release), release.ZipBytes);
        Staged(await _r.Stage(check));
        Assert.Equal(2, _r.ZipRequests(release));
    }

    [Fact]
    public async Task A_rate_limit_on_the_zip_sets_the_wait_that_the_next_check_keeps()
    {
        var (release, check) = await _r.Found();
        _r.Gh.Status(_r.ZipUrl(release), HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(2)));

        var refused = await _r.Stage(check);

        Assert.Equal(UpdateRefusal.HttpError, refused.Reason);
        Assert.Empty(_r.CacheFolders());
        Assert.NotNull(_r.Feed.State().BackoffUntilUtc);
        var wait = await _r.Feed.Check(UpdateMode.Automatic, manual: true);
        Assert.Equal(UpdateCheckKind.Waiting, wait.Kind);
    }

    // ---------------------------------------------------------------------------------------------------------------- a stage that is already there

    [Fact]
    public async Task A_stage_that_is_whole_is_kept_and_nothing_is_downloaded_again()
    {
        var (release, check) = await _r.Found();
        var first = Staged(await _r.Stage(check));

        var second = await _r.Stage(check);

        Assert.True(second.Reused);
        Assert.Equal(first, Staged(second));
        Assert.Equal(1, _r.ZipRequests(release));
        Assert.Equal([first.Folder], _r.CacheFolders());
    }

    public static TheoryData<string> Tampers() => new()
    {
        "a staged file is longer",
        "a staged file has other bytes",
        "a staged file is gone",
        "an extra file is there",
        "release.manifest.json has other bytes",
        "release.manifest.json is gone",
    };

    [Theory]
    [MemberData(nameof(Tampers))]
    public async Task A_stage_that_was_changed_after_it_was_made_is_not_reused_it_is_made_again(string how)
    {
        var (release, check) = await _r.Found();
        var first = Staged(await _r.Stage(check));
        var notice = TxPaths.Under(first.FilesFolder(), "NOTICE.txt");
        switch (how)
        {
            case "a staged file is longer":
                File.AppendAllText(notice, "x");
                break;
            case "a staged file has other bytes":
                File.WriteAllBytes(notice, new byte[File.ReadAllBytes(notice).Length]);
                break;
            case "a staged file is gone":
                File.Delete(notice);
                break;
            case "an extra file is there":
                File.WriteAllText(Path.Combine(first.FilesFolder(), "extra.dll"), "extra");
                break;
            case "release.manifest.json has other bytes":
                File.AppendAllText(Path.Combine(first.Folder, StageLayout.ManifestFileName), " ");
                break;
            case "release.manifest.json is gone":
                File.Delete(Path.Combine(first.Folder, StageLayout.ManifestFileName));
                break;
        }

        var second = Staged(await _r.Stage(check));

        Assert.NotEqual(first.Folder, second.Folder);
        Assert.Equal(2, _r.ZipRequests(release));
        Assert.Equal([second.Folder], _r.CacheFolders()); // the old one is gone
        Assert.True(StageCache.IsIntact(_r.Feed.StateFolder, second));
    }

    [Fact]
    public async Task A_staged_update_that_is_not_in_this_installs_cache_is_not_reported_and_is_not_reused()
    {
        var (release, check) = await _r.Found();
        var staged = Staged(await _r.Stage(check));
        var state = _r.Feed.State();
        var elsewhere = Path.Combine(_r.Feed.Temp, "elsewhere", Path.GetFileName(staged.Folder));
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        Directory.Move(staged.Folder, elsewhere);
        state.Staged = staged with { Folder = elsewhere };
        Assert.True(_r.Feed.Store.TryWrite(state));

        Assert.Null(_r.Feed.Service().ReadStatus().Staged);
        var again = Staged(await _r.Stage(check));

        Assert.Equal(_r.Cache, Path.GetDirectoryName(again.Folder));
        Assert.Equal(2, _r.ZipRequests(release));
    }

    // ---------------------------------------------------------------------------------------------------------------- cleaning up

    [Fact]
    public async Task Old_stage_folders_are_removed_at_the_next_stage_and_a_junction_in_the_cache_is_unlinked_not_followed()
    {
        var (_, check) = await _r.Found();
        var victim = Path.Combine(_r.Feed.Temp, "victim");
        Directory.CreateDirectory(victim);
        var victimFile = Path.Combine(victim, "keep.txt");
        File.WriteAllText(victimFile, "must survive");
        var old = Path.Combine(_r.Cache, "aaaaaaaaaaaa");
        Directory.CreateDirectory(Path.Combine(old, "files"));
        File.WriteAllText(Path.Combine(old, "files", "x.dll"), "old");
        var junction = Path.Combine(_r.Cache, "bbbbbbbbbbbb");
        Rig.Junction(junction, victim);

        var staged = Staged(await _r.Stage(check));

        Assert.Equal([staged.Folder], _r.CacheFolders());
        Assert.Equal("must survive", File.ReadAllText(victimFile));
    }

    [Fact]
    public async Task A_cache_folder_that_is_a_junction_is_unlinked_and_what_it_pointed_to_is_not_touched()
    {
        var (_, check) = await _r.Found();
        var victim = Path.Combine(_r.Feed.Temp, "victim");
        Directory.CreateDirectory(Path.Combine(victim, "deep"));
        File.WriteAllText(Path.Combine(victim, "deep", "keep.txt"), "must survive");
        Directory.CreateDirectory(_r.Feed.StateFolder);
        Rig.Junction(_r.Cache, victim);

        var staged = Staged(await _r.Stage(check));

        Assert.False(TxPaths.IsLink(_r.Cache));
        Assert.Equal([staged.Folder], _r.CacheFolders());
        Assert.Equal("must survive", File.ReadAllText(Path.Combine(victim, "deep", "keep.txt")));
        Assert.Equal([Path.Combine(victim, "deep")], Directory.GetFileSystemEntries(victim)); // nothing was written into it either
    }

    [Fact]
    public async Task A_check_that_finds_another_release_drops_the_stage_of_the_old_one()
    {
        var (_, check) = await _r.Found();
        var first = Staged(await _r.Stage(check));

        await _r.Found(version: "0.6.0");

        Assert.Null(_r.Feed.State().Staged);
        Assert.Empty(_r.CacheFolders());
        Assert.False(Directory.Exists(first.Folder));
    }

    [Fact]
    public async Task A_check_that_finds_the_same_release_keeps_the_stage()
    {
        var (_, check) = await _r.Found();
        var first = Staged(await _r.Stage(check));

        var again = await _r.Feed.Check(UpdateMode.Automatic, manual: true);

        Assert.Equal(UpdateCheckKind.Candidate, again.Kind);
        Assert.Equal(first, _r.Feed.State().Staged);
        Assert.True(Directory.Exists(first.Folder));
    }

    [Fact]
    public async Task A_check_that_finds_nothing_newer_drops_the_stage_and_a_check_that_could_not_ask_keeps_it()
    {
        var (_, check) = await _r.Found();
        var first = Staged(await _r.Stage(check));

        _r.Gh.Status(FakeRelease.Api, HttpStatusCode.InternalServerError);
        var failed = await _r.Feed.Check(UpdateMode.Automatic, manual: true);
        Assert.Equal(UpdateCheckKind.Refused, failed.Kind);
        Assert.Equal(first, _r.Feed.State().Staged); // learned nothing about the release
        Assert.True(Directory.Exists(first.Folder));

        _r.Feed.Release("0.4.0");
        var nothing = await _r.Feed.Check(UpdateMode.Automatic, manual: true);
        Assert.Equal(UpdateCheckKind.NoCandidate, nothing.Kind);
        Assert.Null(_r.Feed.State().Staged);
        Assert.False(Directory.Exists(first.Folder));
    }

    [Fact]
    public async Task Tidy_keeps_a_stage_that_is_newer_than_the_install_and_removes_the_strays_and_forgets_one_the_install_has_outgrown()
    {
        var (_, check) = await _r.Found();
        var staged = Staged(await _r.Stage(check));
        var stray = Path.Combine(_r.Cache, "cccccccccccc");
        Directory.CreateDirectory(stray);
        File.WriteAllText(Path.Combine(stray, "x"), "x");

        _r.Feed.Service().Tidy(new AppVersion(0, 4, 0));

        Assert.Equal([staged.Folder], _r.CacheFolders());
        Assert.Equal(staged, _r.Feed.State().Staged);
        Assert.Equal(new AppVersion(0, 5, 0), _r.Feed.State().ToStatus(_r.Feed.StateFolder).CandidateVersion);

        _r.Feed.Service().Tidy(new AppVersion(0, 5, 0));

        Assert.Null(_r.Feed.State().Staged);
        Assert.Null(_r.Feed.State().LatestVersion);
        Assert.Empty(_r.CacheFolders());
    }

    [Fact]
    public async Task Tidy_does_nothing_while_a_check_holds_the_lock()
    {
        var (_, check) = await _r.Found();
        var staged = Staged(await _r.Stage(check));
        Assert.Equal(LockOutcome.Acquired, _r.Feed.Store.TryLock(out var held));
        using (held)
        {
            _r.Feed.Service().Tidy(new AppVersion(0, 9, 0));
        }

        Assert.Equal(staged, _r.Feed.State().Staged);
        Assert.True(Directory.Exists(staged.Folder));
    }

    [Fact]
    public async Task DiscardStage_forgets_the_stage_and_removes_its_folder_and_the_next_stage_downloads_again()
    {
        var (release, check) = await _r.Found();
        var staged = Staged(await _r.Stage(check));

        _r.Feed.Service().DiscardStage();

        Assert.Null(_r.Feed.State().Staged);
        Assert.False(Directory.Exists(staged.Folder));
        Staged(await _r.Stage(check));
        Assert.Equal(2, _r.ZipRequests(release));
    }

    [Fact]
    public async Task A_shown_result_is_remembered_once_and_only_for_a_real_transaction_id()
    {
        await _r.Found();
        var service = _r.Feed.Service();

        service.MarkResultShown("not-a-tx");
        service.MarkResultShown("..\\..\\x");
        Assert.Null(service.ReadStatus().ShownResultTx);

        service.MarkResultShown("0123456789ab");
        Assert.Equal("0123456789ab", service.ReadStatus().ShownResultTx);

        // A state file edited by hand to hold something else is not reported.
        var state = _r.Feed.State();
        state.ShownResultTx = "<script>";
        Assert.True(_r.Feed.Store.TryWrite(state));
        Assert.Null(service.ReadStatus().ShownResultTx);
    }
}

/// <summary>The download of the zip in <see cref="UpdateHttp"/>: the same address, redirect and credential rules as every other asset, and the byte limit on the real stream.</summary>
[Collection(FeedCollection.Name)]
public sealed class StageDownloadTests
{
    private const string Zip = "https://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/PCMieru-0.5.0-win-x64.zip";
    private static readonly byte[] Body = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 31 % 251)).ToArray();

    private readonly FakeGitHub _gh = new();

    private UpdateHttp Client(TimeSpan? zipTimeout = null) => new("0.4.0", _gh, zipTimeout: zipTimeout);

    private static string Hop(int i) => $"https://release-assets.githubusercontent.com/github-production-release-asset/1/hop{i}?sig=x";

    private static async Task<UpdateHttpException> Refused(Task task) => await Assert.ThrowsAsync<UpdateHttpException>(() => task);

    private static Task<AssetDownload> Get(UpdateHttp client, Stream into, string url = Zip, long declared = 200_000, long max = 1_000_000, CancellationToken ct = default) =>
        client.DownloadAssetAsync(new Uri(url), declared, max, into, ct);

    [Fact]
    public async Task The_bytes_are_written_counted_and_hashed_as_they_arrive_with_a_user_agent_and_an_accept_only()
    {
        _gh.Bytes(Zip, Body);
        var requests = FeedTraffic.Requests;
        using var client = Client();
        using var into = new MemoryStream();

        var result = await Get(client, into);

        Assert.Equal(Body.Length, result.Bytes);
        Assert.Equal(FakeRelease.Hex(Body), result.Sha256);
        Assert.Equal(Body, into.ToArray());
        Assert.Equal(requests + 1, FeedTraffic.Requests);
        var seen = Assert.Single(_gh.Seen);
        Assert.Equal(["Accept", "User-Agent"], seen.HeaderNames);
        Assert.Equal("GET", seen.Method);
    }

    [Fact]
    public async Task A_size_the_release_declares_over_the_limit_is_refused_before_anything_is_sent()
    {
        _gh.Bytes(Zip, Body);
        using var client = Client();

        var ex = await Refused(Get(client, new MemoryStream(), declared: 1_000_001));

        Assert.Equal(UpdateRefusal.AssetTooLarge, ex.Reason);
        Assert.Equal(0, _gh.Count);
    }

    [Fact]
    public async Task An_address_that_is_not_this_repos_download_address_is_refused_before_anything_is_sent()
    {
        using var client = Client();

        foreach (var url in new[]
        {
            "https://github.com/goemon0000/pc-mieru/releases/latest/download/PCMieru-0.5.0-win-x64.zip",
            "https://github.com/other/pc-mieru/releases/download/v0.5.0/PCMieru-0.5.0-win-x64.zip",
            "http://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/PCMieru-0.5.0-win-x64.zip",
            "https://release-assets.githubusercontent.com/github-production-release-asset/1/x?sig=y",
            "https://user:pw@github.com/goemon0000/pc-mieru/releases/download/v0.5.0/PCMieru-0.5.0-win-x64.zip",
        })
        {
            var ex = await Refused(Get(client, new MemoryStream(), url));
            Assert.Contains(ex.Reason, new[] { UpdateRefusal.RedirectHostNotAllowed, UpdateRefusal.RedirectDowngrade, UpdateRefusal.RedirectCredentials });
        }

        Assert.Equal(0, _gh.Count);
    }

    [Fact]
    public async Task A_redirect_to_the_release_asset_host_is_followed_and_every_hop_carries_no_credentials()
    {
        _gh.Redirect(Zip, Hop(1));
        _gh.Redirect(Hop(1), Hop(2));
        _gh.Bytes(Hop(2), Body);
        using var client = Client();
        using var into = new MemoryStream();

        var result = await Get(client, into);

        Assert.Equal(Body.Length, result.Bytes);
        Assert.Equal([Zip, Hop(1), Hop(2)], _gh.Urls);
        Assert.All(_gh.Seen, s => Assert.Equal(["Accept", "User-Agent"], s.HeaderNames));
    }

    [Theory]
    [InlineData("https://evil.example/zip")]
    [InlineData("http://release-assets.githubusercontent.com/github-production-release-asset/1/x?sig=y")]
    [InlineData("https://user:pw@release-assets.githubusercontent.com/github-production-release-asset/1/x?sig=y")]
    [InlineData("https://github.com/other/other/releases/download/v1/x.zip")]
    public async Task A_redirect_anywhere_else_is_refused_and_that_address_is_not_asked(string target)
    {
        _gh.Redirect(Zip, target);
        _gh.Bytes(target, Body);
        using var client = Client();
        using var into = new MemoryStream();

        await Refused(Get(client, into));

        Assert.Equal([Zip], _gh.Urls);
        Assert.Equal(0, into.Length);
    }

    [Fact]
    public async Task A_sixth_redirect_is_refused()
    {
        _gh.Redirect(Zip, Hop(1));
        for (var i = 1; i <= UpdateHttp.MaxRedirects; i++)
        {
            _gh.Redirect(Hop(i), Hop(i + 1));
        }

        _gh.Bytes(Hop(UpdateHttp.MaxRedirects + 1), Body);
        using var client = Client();

        var ex = await Refused(Get(client, new MemoryStream()));

        Assert.Equal(UpdateRefusal.RedirectTooMany, ex.Reason);
        Assert.DoesNotContain(Hop(UpdateHttp.MaxRedirects + 1), _gh.Urls);
    }

    [Fact]
    public async Task A_body_that_says_it_is_over_the_limit_is_refused_and_nothing_is_written()
    {
        _gh.BytesWithDeclaredLength(Zip, Body, declared: 1_000_001);
        using var client = Client();
        using var into = new MemoryStream();

        var ex = await Refused(Get(client, into));

        Assert.Equal(UpdateRefusal.BodyTooLarge, ex.Reason);
        Assert.Equal(0, into.Length);
    }

    [Fact]
    public async Task A_body_with_no_length_that_runs_past_the_limit_is_cut_at_the_limit()
    {
        _gh.BytesWithoutLength(Zip, Body);
        using var client = Client();
        using var into = new MemoryStream();

        var ex = await Refused(Get(client, into, declared: 100, max: 150_000));

        Assert.Equal(UpdateRefusal.BodyTooLarge, ex.Reason);
        Assert.True(into.Length <= 150_000, "nothing past the limit was written: " + into.Length);
    }

    [Fact]
    public async Task An_error_status_is_refused_and_writes_nothing()
    {
        _gh.Status(Zip, HttpStatusCode.NotFound);
        using var client = Client();
        using var into = new MemoryStream();

        await Refused(Get(client, into));

        Assert.Equal(0, into.Length);
    }

    [Fact]
    public async Task A_slow_server_ends_in_a_network_failure_at_the_zip_timeout_and_not_at_the_small_file_timeout()
    {
        _gh.Handle(Zip, async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = Client(zipTimeout: TimeSpan.FromMilliseconds(300));

        var ex = await Refused(Get(client, new MemoryStream()));

        Assert.Equal(UpdateRefusal.NetworkFailure, ex.Reason);
        Assert.True(UpdateHttp.ZipTimeout > UpdateHttp.AssetTimeout);
    }

    [Fact]
    public async Task A_cancel_by_the_caller_is_a_cancel_and_not_a_network_failure()
    {
        using var cts = new CancellationTokenSource();
        _gh.Handle(Zip, async ct =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = Client();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Get(client, new MemoryStream(), ct: cts.Token));
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("disk full");
    }

    [Fact]
    public async Task A_destination_that_cannot_be_written_is_the_disks_failure_not_the_networks()
    {
        _gh.Bytes(Zip, Body);
        using var client = Client();

        var ex = await Refused(Get(client, new FailingStream()));

        Assert.Equal(UpdateRefusal.StageFailed, ex.Reason);
    }
}

/// <summary>The cache folders and the signature check on their own.</summary>
public sealed class StageCacheTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Remove_takes_a_folder_with_everything_in_it_and_says_whether_nothing_is_left()
    {
        var folder = _temp.Sub("a");
        Directory.CreateDirectory(Path.Combine(folder, "b", "c"));
        File.WriteAllText(Path.Combine(folder, "b", "c", "x"), "x");

        Assert.True(StageCache.Remove(folder));
        Assert.False(Directory.Exists(folder));
        Assert.True(StageCache.Remove(folder)); // nothing there is also "nothing left"
    }

    [Fact]
    public void Remove_does_not_throw_for_a_folder_that_a_scanner_holds()
    {
        var folder = _temp.Sub("held");
        Directory.CreateDirectory(folder);
        using var open = new FileStream(Path.Combine(folder, "x"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var gone = StageCache.Remove(folder);

        Assert.False(gone);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void Clear_keeps_the_stage_that_is_wanted_and_does_nothing_when_there_is_no_cache()
    {
        var state = _temp.Sub("state");
        StageCache.Clear(state); // no folder at all: nothing happens

        var keep = Path.Combine(StageLayout.CacheOf(state), "aaaaaaaaaaaa");
        var other = Path.Combine(StageLayout.CacheOf(state), "bbbbbbbbbbbb");
        Directory.CreateDirectory(keep);
        Directory.CreateDirectory(other);

        StageCache.Clear(state, keep: keep + Path.DirectorySeparatorChar);

        Assert.True(Directory.Exists(keep));
        Assert.False(Directory.Exists(other));
    }

    [Fact]
    public void A_stage_record_is_well_formed_only_for_a_folder_directly_in_this_installs_cache()
    {
        var state = _temp.Sub("state");
        var good = new StagedUpdate
        {
            Version = "0.5.0",
            Tag = "v0.5.0",
            BuildId = TestBundle.DefaultBuildId,
            ManifestSha256 = new string('a', 64),
            Folder = Path.Combine(StageLayout.CacheOf(state), "0123456789ab"),
        };

        Assert.True(good.IsWellFormedFor(state));
        Assert.False((good with { Folder = Path.Combine(_temp.Sub("other"), "cache", "0123456789ab") }).IsWellFormedFor(state));
        Assert.False((good with { Folder = Path.Combine(StageLayout.CacheOf(state), "0123456789ab", "files") }).IsWellFormedFor(state));
        Assert.False((good with { Folder = Path.Combine(StageLayout.CacheOf(state), "..", "0123456789ab") }).IsWellFormedFor(state));
        Assert.False((good with { Folder = Path.Combine(StageLayout.CacheOf(state), "0123456789AB") }).IsWellFormedFor(state));
        Assert.False((good with { Tag = "v0.5.1" }).IsWellFormedFor(state));
        Assert.False((good with { Version = "0.5" }).IsWellFormedFor(state));
        Assert.False((good with { BuildId = "NOT-A-GUID" }).IsWellFormedFor(state));
        Assert.False((good with { ManifestSha256 = new string('A', 64) }).IsWellFormedFor(state));
        Assert.False((good with { ManifestSha256 = "abc" }).IsWellFormedFor(state));
    }

    [Fact]
    public void The_policy_refuses_only_an_Invalid_signature()
    {
        Assert.Null(SignaturePolicy.Check("a.dll", SignatureInfo.Unsigned));
        Assert.Null(SignaturePolicy.Check("a.dll", new SignatureInfo(SignatureKind.Valid, "CN=Microsoft Corporation")));
        Assert.NotNull(SignaturePolicy.Check("a.dll", new SignatureInfo(SignatureKind.Invalid, null)));
        Assert.True(SignaturePolicy.IsProgramFile("A.DLL"));
        Assert.True(SignaturePolicy.IsProgramFile("ja/PcMieru.Core.resources.dll"));
        Assert.True(SignaturePolicy.IsProgramFile("createdump.exe"));
        Assert.False(SignaturePolicy.IsProgramFile("NOTICE.txt"));
        Assert.False(SignaturePolicy.IsProgramFile("PcMieru.deps.json"));
    }

    [Fact]
    public void The_real_inspector_reads_a_runtime_file_of_Microsoft_as_valid_a_changed_copy_as_invalid_and_an_unsigned_file_as_unsigned()
    {
        var inspector = new WindowsSignatureInspector();
        var runtime = Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "System.Runtime.dll");
        Assert.True(File.Exists(runtime), runtime);

        var genuine = inspector.Inspect(runtime);
        Assert.Equal(SignatureKind.Valid, genuine.Kind);
        Assert.False(string.IsNullOrEmpty(genuine.Subject));

        var copy = _temp.Sub("System.Runtime.dll");
        Directory.CreateDirectory(_temp.Path);
        var bytes = File.ReadAllBytes(runtime);
        bytes[bytes.Length / 3] ^= 0x55;
        File.WriteAllBytes(copy, bytes);
        Assert.Equal(SignatureKind.Invalid, inspector.Inspect(copy).Kind);

        var own = _temp.Sub("own.dll");
        File.Copy(typeof(StageCacheTests).Assembly.Location, own);
        Assert.Equal(SignatureKind.Unsigned, inspector.Inspect(own).Kind);
    }

    [Fact]
    public void The_real_inspector_is_the_only_code_that_names_wintrust_and_asks_for_no_revocation_and_no_network()
    {
        var source = File.ReadAllText(FindFile("src/PcMieru.Update/Staging/Authenticode.cs"));

        Assert.Contains("WtdRevokeNone = 0", source, StringComparison.Ordinal);
        Assert.Contains("WtdRevocationCheckNone", source, StringComparison.Ordinal);
        Assert.Contains("WtdCacheOnlyUrlRetrieval", source, StringComparison.Ordinal);
        Assert.Contains("TODO(DESIGN-B 12)", source, StringComparison.Ordinal); // the seam for pinning the signer
    }

    private static string FindFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(relative);
    }
}
