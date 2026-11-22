using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using PcMieru.Update.Bundle;
using PcMieru.Update.Feed;

namespace PcMieru.Update.Tests;

/// <summary>
/// Stage B step 2, review round: what a check remembers (S3), how soon a failure is asked again (S3), the wait for check.lock (S4/M7), the probe's stale files (M6) and
/// the clock of Retry-After (M1). Nothing real is asked. The tests that need a member that did not exist before the round are between <c>#if !BEFORE_TREE</c> and
/// <c>#endif</c>, so that the same file can be built against the earlier code to see the others fail there.
/// </summary>
[Collection(FeedCollection.Name)]
public sealed class FeedReviewFixTests : IDisposable
{
    private readonly FeedRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private string ShaUrl(string tag = "v0.5.0") => UpdateEndpoints.AssetUrl(tag, BundleFormat.HashListName).AbsoluteUri;

    /// <summary>0.5.0 is found and on file; a day has passed, so the next scheduled look is due.</summary>
    private async Task A_candidate_is_on_file()
    {
        _rig.Release();
        var found = await _rig.Check();
        Assert.Equal(UpdateCheckKind.Candidate, found.Kind);
        Assert.Equal("0.5.0", _rig.State().LatestVersion);
        Assert.Equal(UpdateEndpoints.ReleasePageUrl("v0.5.0"), _rig.State().LatestPageUrl);
        _rig.Clock.Advance(TimeSpan.FromHours(25));
    }

    // ---------------------------------------------------------------------------------------------------------------- S3: what is remembered

    public static TheoryData<string, UpdateRefusal> FailuresThatJudgedNoRelease => new()
    {
        { "no-answer", UpdateRefusal.NetworkFailure },
        { "error-status", UpdateRefusal.HttpError },
        { "not-json", UpdateRefusal.FeedUnreadable },
        { "answer-too-large", UpdateRefusal.BodyTooLarge },
        { "api-sent-elsewhere", UpdateRefusal.RedirectHostNotAllowed },
        { "hash-list-sent-elsewhere", UpdateRefusal.RedirectHostNotAllowed },
        { "hash-list-error-status", UpdateRefusal.HttpError },
    };

    private void Break(string how)
    {
        switch (how)
        {
            case "no-answer":
                _rig.Gh.Handle(FakeRelease.Api, _ => throw new HttpRequestException("no route to host"));
                break;
            case "error-status":
                _rig.Gh.Status(FakeRelease.Api, HttpStatusCode.InternalServerError);
                break;
            case "not-json":
                _rig.Gh.Bytes(FakeRelease.Api, Encoding.UTF8.GetBytes("<html>maintenance</html>"));
                break;
            case "answer-too-large":
                _rig.Gh.BytesWithoutLength(FakeRelease.Api, new byte[UpdateHttp.MaxJsonBytes + 1]);
                break;
            case "api-sent-elsewhere":
                _rig.Gh.Redirect(FakeRelease.Api, "https://evil.example/x");
                break;
            case "hash-list-sent-elsewhere":
                _rig.Release("0.6.0");
                _rig.Gh.Redirect(ShaUrl("v0.6.0"), "https://evil.example/SHA256.txt");
                break;
            case "hash-list-error-status":
                _rig.Release("0.6.0");
                _rig.Gh.Status(ShaUrl("v0.6.0"), HttpStatusCode.BadGateway);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(how));
        }
    }

    [Theory]
    [MemberData(nameof(FailuresThatJudgedNoRelease))]
    public async Task A_failure_that_judged_no_release_keeps_the_candidate_an_earlier_check_found(string how, UpdateRefusal reason)
    {
        await A_candidate_is_on_file();
        Break(how);

        var result = await _rig.Check();

        Assert.Equal(UpdateCheckKind.Refused, result.Kind);
        Assert.Equal(reason, result.Refusal);
        var state = _rig.State();
        Assert.Equal("0.5.0", state.LatestVersion);
        Assert.Equal(UpdateEndpoints.ReleasePageUrl("v0.5.0"), state.LatestPageUrl);
        Assert.Equal(reason.ToString(), state.LastRefusal); // and what the look said is on file next to it
        Assert.Equal(new AppVersion(0, 5, 0), _rig.Service().ReadStatus().CandidateVersion);
    }

    [Fact]
    public async Task A_failure_with_no_earlier_candidate_leaves_none()
    {
        _rig.Gh.Handle(FakeRelease.Api, _ => throw new HttpRequestException("no route to host"));

        await _rig.Check();

        Assert.Null(_rig.State().LatestVersion);
        Assert.Null(_rig.State().LatestPageUrl);
    }

    public static TheoryData<string> RulesOfTheRelease => ["draft", "pre-release", "manifest-hash-in-the-list", "manifest-of-another-version"];

    [Theory]
    [MemberData(nameof(RulesOfTheRelease))]
    public async Task A_release_that_breaks_a_rule_of_its_own_clears_the_candidate(string rule)
    {
        await A_candidate_is_on_file();
        _rig.Release("0.6.0", r =>
        {
            switch (rule)
            {
                case "draft":
                    r.Draft = true;
                    break;
                case "pre-release":
                    r.Prerelease = true;
                    break;
                case "manifest-hash-in-the-list":
                    r.WithDigests = false;
                    r.ShaText = $"{FakeRelease.Hex(r.ZipBytes)}  {r.ZipName}\n{new string('0', 64)}  {r.ManifestName}\n";
                    break;
                default:
                    r.ManifestBytes = new TestBundle("0.4.9").Outer().ToUtf8Bytes();
                    break;
            }
        });

        var result = await _rig.Check();

        Assert.Equal(UpdateCheckKind.Refused, result.Kind);
        Assert.Null(_rig.State().LatestVersion);
        Assert.Null(_rig.State().LatestPageUrl);
        Assert.Null(_rig.Service().ReadStatus().CandidateVersion);
    }

    [Fact]
    public async Task Nothing_newer_after_the_answer_was_read_clears_the_candidate()
    {
        await A_candidate_is_on_file();
        _rig.Release("0.4.0"); // the feed's latest is the running version: nothing to offer

        var result = await _rig.Check();

        Assert.Equal(UpdateCheckKind.NoCandidate, result.Kind);
        Assert.Null(_rig.State().LatestVersion);
        Assert.Null(_rig.State().LatestPageUrl);
    }

    [Fact]
    public async Task A_newer_candidate_replaces_the_one_on_file()
    {
        await A_candidate_is_on_file();
        _rig.Release("0.6.0");

        await _rig.Check();

        Assert.Equal("0.6.0", _rig.State().LatestVersion);
        Assert.Equal(UpdateEndpoints.ReleasePageUrl("v0.6.0"), _rig.State().LatestPageUrl);
    }

    // ---------------------------------------------------------------------------------------------------------------- S3: asked again after two hours

    public static TheoryData<string> FailuresOfTheNetwork => ["no-answer", "error-status"];

    [Theory]
    [MemberData(nameof(FailuresOfTheNetwork))]
    public async Task A_look_that_got_no_answer_is_asked_again_after_two_hours_not_after_a_day(string how)
    {
        Break(how);
        await _rig.Check();
        Assert.Equal(1, _rig.Gh.Count);

        _rig.Clock.Advance(TimeSpan.FromMinutes(119));
        var early = await _rig.Check();
        Assert.False(early.Fetched);
        Assert.Equal(1, _rig.Gh.Count); // 1 h 59 min: not yet

        _rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await _rig.Check();
        Assert.Equal(2, _rig.Gh.Count); // 2 h: asked again, not a day later
    }

    [Fact]
    public async Task A_look_that_refused_a_release_for_its_rules_or_found_one_is_still_asked_again_after_a_day()
    {
        _rig.Release("0.5.0", r => r.Draft = true);
        await _rig.Check();
        var asked = _rig.Gh.Count;

        _rig.Clock.Advance(TimeSpan.FromHours(23));
        await _rig.Check();
        Assert.Equal(asked, _rig.Gh.Count);

        _rig.Clock.Advance(TimeSpan.FromHours(1));
        await _rig.Check();
        Assert.True(_rig.Gh.Count > asked);
    }

    [Fact]
    public void A_back_off_is_never_cut_short_by_the_retry_of_a_failure_and_the_next_look_is_never_earlier_than_it()
    {
        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var state = new UpdateState { LastCheckUtc = now.AddHours(-3), LastRefusal = nameof(UpdateRefusal.NetworkFailure), BackoffUntilUtc = now.AddHours(20) };

        Assert.False(UpdateSchedule.IsDue(state, now));
        Assert.Equal(now.AddHours(20), UpdateSchedule.NextDue(state, now)); // the back-off is later than last + 2 h

        var failedOnly = new UpdateState { LastCheckUtc = now.AddHours(-1), LastRefusal = nameof(UpdateRefusal.HttpError) };
        Assert.Equal(now.AddHours(1), UpdateSchedule.NextDue(failedOnly, now)); // last + 2 h
        Assert.False(UpdateSchedule.IsDue(failedOnly, now));
        Assert.True(UpdateSchedule.IsDue(failedOnly, now.AddHours(1)));
    }

    // ---------------------------------------------------------------------------------------------------------------- S4 / M7: the wait for check.lock

    [Fact]
    public async Task A_manual_check_waits_for_the_lock_of_a_check_that_is_still_letting_go_and_then_runs()
    {
        // Off, then Notify, then "check now" in quick succession: the cancelled check has not released check.lock yet.
        _rig.Release();
        Assert.Equal(LockOutcome.Acquired, _rig.Store.TryLock(out var held));
        var check = _rig.Check(UpdateMode.Notify, manual: true);
        await Task.Delay(300);
        Assert.False(check.IsCompleted); // it is waiting, not refused
        held!.Dispose();

        var result = await check.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(UpdateCheckKind.Candidate, result.Kind);
        Assert.Equal(3, _rig.Gh.Count);
    }

    [Fact]
    public async Task A_scheduled_tick_never_waits_for_the_lock()
    {
        _rig.Release();
        Assert.Equal(LockOutcome.Acquired, _rig.Store.TryLock(out var held));
        using var _ = held;
        var started = Environment.TickCount64;

        var result = await _rig.Check(UpdateMode.Notify, manual: false);

        Assert.Equal(UpdateRefusal.AnotherCheckRunning, result.Refusal);
        Assert.False(result.Fetched);
        Assert.InRange(Environment.TickCount64 - started, 0, 1500);
        Assert.Equal(0, _rig.Gh.Count);
    }

#if !BEFORE_TREE
    [Fact]
    public async Task A_manual_check_gives_up_on_a_lock_that_stays_held_after_its_wait_and_says_another_check_runs()
    {
        using var temp = new TempFolder();
        var store = new UpdateStateStore(temp.Sub("state"));
        var service = new UpdateService(new UpdateServiceOptions
        {
            Running = new AppVersion(0, 4, 0),
            StateFolder = temp.Sub("state"),
            Clock = _rig.Clock,
            ManualLockWait = TimeSpan.FromMilliseconds(200),
            TransportFactory = () => new UpdateHttp("0.4.0", _rig.Gh, clock: _rig.Clock),
        });
        Assert.Equal(LockOutcome.Acquired, store.TryLock(out var held));
        using var _ = held;
        var started = Environment.TickCount64;

        var result = await service.CheckAsync(UpdateMode.Notify, manual: true, default);

        Assert.Equal(UpdateRefusal.AnotherCheckRunning, result.Refusal);
        Assert.InRange(Environment.TickCount64 - started, 150, 3000);
        Assert.Equal(0, _rig.Gh.Count);
    }

    [Fact]
    public async Task A_wait_for_the_lock_ends_when_the_check_is_cancelled()
    {
        using var temp = new TempFolder();
        var store = new UpdateStateStore(temp.Sub("state"));
        Assert.Equal(LockOutcome.Acquired, store.TryLock(out var held));
        using var _ = held;
        using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.TryLockAsync(TimeSpan.FromSeconds(30), source.Token));
    }
#endif

    // ---------------------------------------------------------------------------------------------------------------- M6: stale probe files

    [Fact]
    public void The_probe_sweeps_the_probe_files_a_dead_probe_left_that_are_older_than_an_hour_and_nothing_else()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.Path);
        var stale = Path.Combine(temp.Path, ".pcm-probe-aaaa.tmp");
        var staleMoved = Path.Combine(temp.Path, ".pcm-probe-bbbb.tmp.moved");
        var fresh = Path.Combine(temp.Path, ".pcm-probe-cccc.tmp");
        var other = Path.Combine(temp.Path, "notes-from-the-owner.tmp");
        foreach (var path in new[] { stale, staleMoved, fresh, other })
        {
            File.WriteAllText(path, "x");
        }

        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(stale, now.AddHours(-2));
        File.SetLastWriteTimeUtc(staleMoved, now.AddHours(-5));
        File.SetLastWriteTimeUtc(fresh, now.AddMinutes(-10)); // may be another window's probe running right now
        File.SetLastWriteTimeUtc(other, now.AddDays(-30));     // not a probe file: never touched

        var placement = InstallPlacement.Check(temp.Path, () => false, _ => DriveType.Fixed);

        Assert.Equal(PlacementKind.CanSwap, placement.Kind);
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(staleMoved));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(other));
        Assert.Equal(2, Directory.GetFiles(temp.Path).Length); // and the probe itself left nothing
    }

    // ---------------------------------------------------------------------------------------------------------------- M1: the date form of Retry-After

#if !BEFORE_TREE
    [Fact]
    public async Task Retry_After_as_a_date_is_read_against_the_clock_the_client_was_given_exactly()
    {
        var gh = new FakeGitHub();
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
        gh.Status(FakeRelease.Api, HttpStatusCode.Forbidden, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(clock.Now.AddHours(3)));
        using var client = new UpdateHttp("0.4.0", gh, clock: clock);

        var limited = await Assert.ThrowsAsync<RateLimitedException>(() => client.GetLatestReleaseJsonAsync(default));

        Assert.Equal(TimeSpan.FromHours(3), limited.RetryAfter);
    }

    [Fact]
    public async Task The_service_hands_its_clock_to_the_client_it_makes_so_the_wait_after_a_rate_limit_is_exact()
    {
        _rig.Gh.Status(FakeRelease.Api, HttpStatusCode.Forbidden, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(_rig.Clock.Now.AddHours(48)));

        var result = await _rig.Check();

        Assert.Equal(_rig.Clock.Now.AddHours(48), result.WaitUntil);
    }

    [Fact]
    public void Every_refusal_is_on_exactly_one_side_and_the_two_sides_are_the_ones_the_review_decided()
    {
        UpdateRefusal[] operational =
        [
            UpdateRefusal.FeedUnreadable, UpdateRefusal.BodyTooLarge,
            UpdateRefusal.RedirectHostNotAllowed, UpdateRefusal.RedirectDowngrade, UpdateRefusal.RedirectTooMany, UpdateRefusal.RedirectCredentials, UpdateRefusal.RedirectMissingLocation,
            UpdateRefusal.HttpError, UpdateRefusal.NetworkFailure,
            UpdateRefusal.StateUnavailable, UpdateRefusal.AnotherCheckRunning, UpdateRefusal.OwnVersionUnknown,
        ];
        UpdateRefusal[] byTheReleasesRules =
        [
            UpdateRefusal.DraftRelease, UpdateRefusal.PrereleaseRelease, UpdateRefusal.TagNotStable, UpdateRefusal.ReleasePageMismatch, UpdateRefusal.BlockedTag,
            UpdateRefusal.AssetMissing, UpdateRefusal.AssetDuplicate, UpdateRefusal.AssetNotReady, UpdateRefusal.AssetUrlMismatch, UpdateRefusal.AssetTooLarge,
            UpdateRefusal.HashListLine, UpdateRefusal.DigestMismatch, UpdateRefusal.ManifestHashMismatch, UpdateRefusal.ManifestInvalid, UpdateRefusal.ManifestVersionMismatch,
        ];

        // A new member of the enum fails here until somebody puts it on a side.
        foreach (var reason in Enum.GetValues<UpdateRefusal>().Where(r => r != UpdateRefusal.None))
        {
            Assert.True(
                UpdateRefusals.IsOperational(reason) ^ UpdateRefusals.JudgedARelease(reason),
                reason + " must be on exactly one side");
        }

        Assert.False(UpdateRefusals.IsOperational(UpdateRefusal.None));
        Assert.False(UpdateRefusals.JudgedARelease(UpdateRefusal.None));
        Assert.True(Enum.GetValues<UpdateRefusal>().Where(UpdateRefusals.IsOperational).ToHashSet().SetEquals(operational));
        Assert.True(Enum.GetValues<UpdateRefusal>().Where(UpdateRefusals.JudgedARelease).ToHashSet().SetEquals(byTheReleasesRules));
        Assert.True(Enum.GetValues<UpdateRefusal>().Where(UpdateRefusals.IsTransient).ToHashSet().SetEquals([UpdateRefusal.NetworkFailure, UpdateRefusal.HttpError]));
    }
#endif
}
