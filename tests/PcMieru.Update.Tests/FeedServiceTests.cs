// Provenance: several cases are ported from UpdaterTests of the owner's pl-cloud (Parts Life, MIT, same owner; see NOTICE.txt), with the old name in a comment:
// Sends_only_user_agent_and_accept, Older_release_is_never_offered, Notify_only_downloads_nothing, Draft_is_not_an_update, Hash_mismatch_stages_nothing,
// Missing_sha_line_stages_nothing, Body_longer_than_the_declared_size_is_cut, Oversized_release_is_refused_before_downloading,
// Already_installed_by_another_process_downloads_nothing, Previously_failed_version_is_skipped, Rate_limit_backs_off_for_a_day_and_blocks_manual_checks,
// Retry_after_longer_than_a_day_is_respected, Failure_still_counts_as_todays_check. The rest (every refusal of DESIGN-B 11 with its own reason) is new.
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using PcMieru.Update.Bundle;
using PcMieru.Update.Feed;

namespace PcMieru.Update.Tests;

/// <summary>The check of the feed from the first request to the answer (DESIGN-B 3.1 and 11), with a fake GitHub and a fake clock. Nothing real is asked.</summary>
[Collection(FeedCollection.Name)]
public sealed class FeedServiceTests : IDisposable
{
    private const string Page = "https://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0";

    private readonly FeedRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private string ShaUrl => UpdateEndpoints.AssetUrl("v0.5.0", BundleFormat.HashListName).AbsoluteUri;

    private string ManifestUrl => UpdateEndpoints.AssetUrl("v0.5.0", BundleFormat.OuterManifestName("0.5.0")).AbsoluteUri;

    private string ZipUrl => UpdateEndpoints.AssetUrl("v0.5.0", BundleFormat.ZipName("0.5.0")).AbsoluteUri;

    private static string Hex(byte[] bytes) => FakeRelease.Hex(bytes);

    // ---------------------------------------------------------------------------------------------------------------- the good case

    // Ported from Sends_only_user_agent_and_accept and Notify_only_downloads_nothing.
    [Fact]
    public async Task A_newer_release_is_a_candidate_after_exactly_three_requests_and_never_the_zip()
    {
        var release = _rig.Release();

        var result = await _rig.Check();

        Assert.Equal(UpdateCheckKind.Candidate, result.Kind);
        Assert.Equal(new AppVersion(0, 5, 0), result.Version);
        Assert.Equal(Page, result.PageUrl);
        Assert.Equal(ZipUrl, result.ZipUrl!.AbsoluteUri);
        Assert.Equal(Hex(release.ZipBytes), result.ZipSha256);
        Assert.Equal("0.5.0", result.Manifest!.Version);
        Assert.Equal(release.Bundle.BuildId, result.Manifest.BuildId);
        Assert.True(result.Fetched);

        Assert.Equal([FakeRelease.Api, ShaUrl, ManifestUrl], _rig.Gh.Urls);
        Assert.False(_rig.Gh.AskedForAZip);

        // Nothing but User-Agent and Accept goes out, on every request, and every request is a GET without a body.
        Assert.All(_rig.Gh.Seen, s =>
        {
            Assert.Equal("GET", s.Method);
            Assert.Equal(["Accept", "User-Agent"], s.HeaderNames);
            Assert.Equal("PCMieru/0.4.0", s.UserAgent);
            Assert.False(s.HadBody);
        });
        Assert.Equal("application/vnd.github+json", _rig.Gh.Seen[0].Accept);
        Assert.Equal("application/octet-stream", _rig.Gh.Seen[1].Accept);
    }

    [Fact]
    public async Task The_candidate_is_remembered_in_state_json_and_the_time_of_the_look()
    {
        _rig.Release();

        await _rig.Check();

        var state = _rig.State();
        Assert.Equal(_rig.Clock.Now, state.LastCheckUtc);
        Assert.Equal("0.5.0", state.LatestVersion);
        Assert.Equal(Page, state.LatestPageUrl);
        Assert.Null(state.LastRefusal);
        Assert.Null(state.BackoffUntilUtc);
        var status = _rig.Service().ReadStatus();
        Assert.Equal(new AppVersion(0, 5, 0), status.CandidateVersion);
        Assert.Equal(Page, status.CandidatePageUrl);
        Assert.False(File.Exists(_rig.Store.StatePath + ".tmp"), "no temp file is left behind");
        Assert.True(File.Exists(_rig.Store.LockPath));
    }

    [Fact]
    public async Task Automatic_behaves_like_notify_in_this_step()
    {
        _rig.Release();

        var result = await _rig.Check(UpdateMode.Automatic);

        Assert.Equal(UpdateCheckKind.Candidate, result.Kind);
        Assert.Equal(3, _rig.Gh.Count);
        Assert.False(_rig.Gh.AskedForAZip);
    }

    [Fact]
    public async Task Another_asset_on_the_release_is_ignored()
    {
        _rig.Release(edit: r => r.EditAssets = assets => assets.Add(new FakeAsset("notes.txt", 10, "uploaded", r.AssetUrl("notes.txt"), null)));

        var result = await _rig.Check();

        Assert.Equal(UpdateCheckKind.Candidate, result.Kind);
        Assert.DoesNotContain(_rig.Gh.Urls, u => u.EndsWith("notes.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_release_without_digests_is_judged_by_SHA256_txt_alone()
    {
        _rig.Release(edit: r => r.WithDigests = false);

        Assert.Equal(UpdateCheckKind.Candidate, (await _rig.Check()).Kind);
    }

    [Fact]
    public async Task A_digest_of_another_algorithm_is_not_read_and_does_not_refuse()
    {
        _rig.Release(edit: r => r.EditAssets = assets =>
        {
            for (var i = 0; i < assets.Count; i++)
            {
                assets[i] = assets[i] with { Digest = "sha512:" + new string('a', 128) };
            }
        });

        Assert.Equal(UpdateCheckKind.Candidate, (await _rig.Check()).Kind);
    }

    // ---------------------------------------------------------------------------------------------------------------- newer than both

    // Ported from Older_release_is_never_offered and Already_installed_by_another_process_downloads_nothing.
    [Theory]
    [InlineData("0.4.0", null, "v0.5.0", true)]
    [InlineData("0.4.0", "0.4.0", "v0.5.0", true)]
    [InlineData("0.9.0", null, "v0.10.0", true)]       // numbers, not text
    [InlineData("0.4.0", "0.4.5", "v0.4.6", true)]
    [InlineData("0.5.0", null, "v0.5.0", false)]       // the same
    [InlineData("0.6.0", null, "v0.5.0", false)]       // older than the running one
    [InlineData("0.4.0", "0.6.0", "v0.5.0", false)]    // newer than the running one, older than the installed one
    [InlineData("0.4.0", "0.5.0", "v0.5.0", false)]    // another process already installed it: the same as the installed one
    [InlineData("0.5.0", "0.4.0", "v0.5.0", false)]    // an installed version older than the running one does not lower the floor
    [InlineData("0.4.0", null, "v0.4.9", true)]
    public async Task A_release_is_a_candidate_only_when_newer_than_both_the_running_and_the_installed_version(string running, string? installed, string tag, bool candidate)
    {
        _rig.Running = Parse(running);
        _rig.Installed = installed is null ? null : Parse(installed);
        _rig.Release(tag[1..]);

        var result = await _rig.Check();

        Assert.Equal(candidate ? UpdateCheckKind.Candidate : UpdateCheckKind.NoCandidate, result.Kind);
        Assert.True(result.Fetched);
        Assert.Equal(candidate ? 3 : 1, _rig.Gh.Count); // a release that is not newer costs the one request and nothing more
        Assert.False(_rig.Gh.AskedForAZip);
        Assert.Equal(candidate ? tag[1..] : null, _rig.State().LatestVersion);
    }

    private static AppVersion Parse(string text)
    {
        Assert.True(AppVersion.TryParse(text, out var version));
        return version;
    }

    [Fact]
    public async Task A_candidate_that_is_gone_is_forgotten_by_the_next_check()
    {
        _rig.Release();
        Assert.Equal(UpdateCheckKind.Candidate, (await _rig.Check()).Kind);
        Assert.Equal("0.5.0", _rig.State().LatestVersion);

        _rig.Running = new AppVersion(0, 5, 0); // the app was updated by hand
        _rig.Clock.Advance(TimeSpan.FromHours(25));
        var result = await _rig.Check();

        Assert.Equal(UpdateCheckKind.NoCandidate, result.Kind);
        Assert.Null(_rig.State().LatestVersion);
        Assert.Null(_rig.State().LatestPageUrl);
    }

    // Ported from Previously_failed_version_is_skipped.
    [Fact]
    public async Task A_blocked_tag_is_refused_after_the_one_request_and_costs_nothing_more()
    {
        _rig.Blocked = ["v0.5.0"];
        _rig.Release();

        var result = await _rig.Check();

        Assert.Equal(UpdateRefusal.BlockedTag, result.Refusal);
        Assert.Equal(1, _rig.Gh.Count);
    }

    [Fact]
    public async Task A_blocked_tag_does_not_block_another_tag()
    {
        _rig.Blocked = ["v0.5.0"];
        _rig.Release("0.5.1");

        Assert.Equal(UpdateCheckKind.Candidate, (await _rig.Check()).Kind);
    }

    // ---------------------------------------------------------------------------------------------------------------- the refusals

    private static readonly byte[] Garbage = Encoding.UTF8.GetBytes("this is not a manifest");

    /// <summary>Name: the change to the release, the rule that must hold, and how many requests it takes to find out.</summary>
    private static readonly Dictionary<string, (Action<FakeRelease> Edit, UpdateRefusal Reason, int Requests)> Faults = new()
    {
        // --- the release (one request) ---
        ["draft"] = (r => r.Draft = true, UpdateRefusal.DraftRelease, 1),
        ["prerelease"] = (r => r.Prerelease = true, UpdateRefusal.PrereleaseRelease, 1),
        ["tag-without-v"] = (r => r.Tag = "0.5.0", UpdateRefusal.TagNotStable, 1),
        ["tag-capital-V"] = (r => r.Tag = "V0.5.0", UpdateRefusal.TagNotStable, 1),
        ["tag-with-suffix"] = (r => r.Tag = "v0.5.0-beta", UpdateRefusal.TagNotStable, 1),
        ["tag-with-four-numbers"] = (r => r.Tag = "v0.5.0.1", UpdateRefusal.TagNotStable, 1),
        ["tag-missing"] = (r => r.Tag = null, UpdateRefusal.FeedUnreadable, 1),
        ["page-of-another-repo"] = (r => r.HtmlUrl = "https://github.com/someone/else/releases/tag/v0.5.0", UpdateRefusal.ReleasePageMismatch, 1),
        ["page-of-another-tag"] = (r => r.HtmlUrl = "https://github.com/goemon0000/pc-mieru/releases/tag/v0.4.9", UpdateRefusal.ReleasePageMismatch, 1),
        ["page-on-another-host"] = (r => r.HtmlUrl = "https://evil.example/goemon0000/pc-mieru/releases/tag/v0.5.0", UpdateRefusal.ReleasePageMismatch, 1),
        ["page-over-http"] = (r => r.HtmlUrl = "http://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0", UpdateRefusal.ReleasePageMismatch, 1),

        // --- the three files (one request) ---
        ["zip-missing"] = (r => r.EditAssets = a => a.RemoveAll(x => x.Name == r.ZipName), UpdateRefusal.AssetMissing, 1),
        ["manifest-missing"] = (r => r.EditAssets = a => a.RemoveAll(x => x.Name == r.ManifestName), UpdateRefusal.AssetMissing, 1), // what v0.4.0 looks like
        ["sha256-missing"] = (r => r.EditAssets = a => a.RemoveAll(x => x.Name == BundleFormat.HashListName), UpdateRefusal.AssetMissing, 1),
        ["zip-in-another-case"] = (r => r.EditAssets = a => a[0] = a[0] with { Name = r.ZipName.ToLowerInvariant() }, UpdateRefusal.AssetMissing, 1),
        ["zip-twice"] = (r => r.EditAssets = a => a.Add(a[0]), UpdateRefusal.AssetDuplicate, 1),
        ["manifest-twice"] = (r => r.EditAssets = a => a.Add(a[1]), UpdateRefusal.AssetDuplicate, 1),
        ["sha256-twice"] = (r => r.EditAssets = a => a.Add(a[2]), UpdateRefusal.AssetDuplicate, 1),
        ["manifest-not-uploaded"] = (r => r.EditAssets = a => a[1] = a[1] with { State = "starter" }, UpdateRefusal.AssetNotReady, 1),
        ["sha256-empty"] = (r => r.EditAssets = a => a[2] = a[2] with { Size = 0 }, UpdateRefusal.AssetNotReady, 1),
        ["zip-at-another-repo"] = (r => r.EditAssets = a => a[0] = a[0] with { Url = "https://github.com/someone/else/releases/download/v0.5.0/" + r.ZipName }, UpdateRefusal.AssetUrlMismatch, 1),
        ["manifest-at-another-tag"] = (r => r.EditAssets = a => a[1] = a[1] with { Url = "https://github.com/goemon0000/pc-mieru/releases/download/v0.4.9/" + r.ManifestName }, UpdateRefusal.AssetUrlMismatch, 1),
        ["sha256-on-another-host"] = (r => r.EditAssets = a => a[2] = a[2] with { Url = "https://evil.example/goemon0000/pc-mieru/releases/download/v0.5.0/SHA256.txt" }, UpdateRefusal.AssetUrlMismatch, 1),
        ["sha256-over-http"] = (r => r.EditAssets = a => a[2] = a[2] with { Url = "http://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/SHA256.txt" }, UpdateRefusal.AssetUrlMismatch, 1),
        ["manifest-with-a-query"] = (r => r.EditAssets = a => a[1] = a[1] with { Url = a[1].Url + "?x=1" }, UpdateRefusal.AssetUrlMismatch, 1),
        ["sha256-too-large"] = (r => r.EditAssets = a => a[2] = a[2] with { Size = Sha256List.MaxBytes + 1 }, UpdateRefusal.AssetTooLarge, 1),
        ["manifest-too-large"] = (r => r.EditAssets = a => a[1] = a[1] with { Size = Limits.Default.ManifestBytes + 1 }, UpdateRefusal.AssetTooLarge, 1),
        ["zip-too-large"] = (r => r.EditAssets = a => a[0] = a[0] with { Size = Limits.Default.ZipBytes + 1 }, UpdateRefusal.AssetTooLarge, 1),

        // --- SHA256.txt (two requests: the release and SHA256.txt) ---
        ["sha256-without-the-manifest-line"] = (r => r.ShaText = $"{Hex(r.ZipBytes)}  {r.ZipName}\n", UpdateRefusal.HashListLine, 2),
        ["sha256-without-the-zip-line"] = (r => r.ShaText = $"{Hex(r.ManifestBytes)}  {r.ManifestName}\n", UpdateRefusal.HashListLine, 2),
        ["sha256-two-values-for-the-manifest"] = (r => r.ShaText = $"{Hex(r.ZipBytes)}  {r.ZipName}\n{Hex(r.ManifestBytes)}  {r.ManifestName}\n{new string('0', 64)}  {r.ManifestName}\n", UpdateRefusal.HashListLine, 2),
        ["sha256-lines-in-another-case"] = (r => r.ShaText = $"{Hex(r.ZipBytes)}  {r.ZipName.ToLowerInvariant()}\n{Hex(r.ManifestBytes)}  {r.ManifestName.ToLowerInvariant()}\n", UpdateRefusal.HashListLine, 2),
        ["digest-of-sha256-txt-differs"] = (r => r.EditAssets = a => a[2] = a[2] with { Digest = "sha256:" + new string('0', 64) }, UpdateRefusal.DigestMismatch, 2),
        ["digest-of-the-zip-differs-from-the-list"] = (r => r.EditAssets = a => a[0] = a[0] with { Digest = "sha256:" + new string('0', 64) }, UpdateRefusal.DigestMismatch, 2),
        ["digest-of-the-manifest-differs-from-the-list"] = (r => r.EditAssets = a => a[1] = a[1] with { Digest = "sha256:" + new string('0', 64) }, UpdateRefusal.DigestMismatch, 2),
        ["digest-that-is-not-64-hex-digits"] = (r => r.EditAssets = a => a[0] = a[0] with { Digest = "sha256:xyz" }, UpdateRefusal.DigestMismatch, 2),

        // --- the manifest (three requests: the release, SHA256.txt, the manifest) ---
        ["manifest-not-the-one-the-list-names"] = (r => { r.WithDigests = false; r.ShaText = $"{Hex(r.ZipBytes)}  {r.ZipName}\n{new string('0', 64)}  {r.ManifestName}\n"; }, UpdateRefusal.ManifestHashMismatch, 3),
        ["manifest-is-not-a-manifest"] = (r => r.ManifestBytes = Garbage, UpdateRefusal.ManifestInvalid, 3),
        ["manifest-of-another-version"] = (r => r.ManifestBytes = new TestBundle("0.4.9").Outer().ToUtf8Bytes(), UpdateRefusal.ManifestVersionMismatch, 3),
    };

    public static TheoryData<string> FaultNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (var name in Faults.Keys)
            {
                names.Add(name);
            }

            return names;
        }
    }

    [Theory]
    [MemberData(nameof(FaultNames))]
    public async Task Every_refusal_names_its_rule_stops_at_the_right_request_and_leaves_no_candidate(string name)
    {
        var (edit, reason, requests) = Faults[name];
        _rig.Release(edit: edit);

        var result = await _rig.Check();

        Assert.Equal(UpdateCheckKind.Refused, result.Kind);
        Assert.Equal(reason, result.Refusal);
        Assert.True(result.Fetched);
        Assert.Equal(requests, _rig.Gh.Count);
        Assert.False(_rig.Gh.AskedForAZip);
        Assert.Null(result.Version);
        Assert.Null(result.Manifest);
        var state = _rig.State();
        Assert.Equal(reason.ToString(), state.LastRefusal);
        Assert.Null(state.LatestVersion);
        Assert.Equal(_rig.Clock.Now, state.LastCheckUtc);
        Assert.Equal(reason, _rig.Service().ReadStatus().LastRefusal);
    }

    [Fact]
    public void Every_refusal_of_the_table_is_a_real_member_and_the_table_covers_the_release_rules()
    {
        // The table and the enum agree: no name of the table is a typo that matches nothing, and each reason of the release and the files is exercised.
        var covered = Faults.Values.Select(f => f.Reason).ToHashSet();
        foreach (var reason in new[]
        {
            UpdateRefusal.DraftRelease, UpdateRefusal.PrereleaseRelease, UpdateRefusal.TagNotStable, UpdateRefusal.ReleasePageMismatch, UpdateRefusal.FeedUnreadable,
            UpdateRefusal.AssetMissing, UpdateRefusal.AssetDuplicate, UpdateRefusal.AssetNotReady, UpdateRefusal.AssetUrlMismatch, UpdateRefusal.AssetTooLarge,
            UpdateRefusal.HashListLine, UpdateRefusal.DigestMismatch, UpdateRefusal.ManifestHashMismatch, UpdateRefusal.ManifestInvalid, UpdateRefusal.ManifestVersionMismatch,
        })
        {
            Assert.Contains(reason, covered);
        }
    }

    [Fact]
    public async Task A_manifest_whose_bytes_differ_from_what_SHA256_txt_names_is_refused()
    {
        // The realistic form of "the manifest hash does not match": the release is in order, and the bytes that come for the manifest are not the ones it was made of.
        var release = _rig.Release();
        _rig.Gh.Bytes(ManifestUrl, release.Bundle.Outer().ToUtf8Bytes().Concat("\n "u8.ToArray()).ToArray());

        var result = await _rig.Check();

        Assert.Equal(UpdateRefusal.ManifestHashMismatch, result.Refusal);
        Assert.Equal(3, _rig.Gh.Count);
    }

    [Fact]
    public async Task A_SHA256_txt_whose_bytes_differ_from_the_digest_of_the_release_is_refused()
    {
        var release = _rig.Release();
        _rig.Gh.Bytes(ShaUrl, Encoding.UTF8.GetBytes(release.ShaFile() + "# tampered\n"));

        var result = await _rig.Check();

        Assert.Equal(UpdateRefusal.DigestMismatch, result.Refusal);
        Assert.Equal(2, _rig.Gh.Count);
    }

    // ---------------------------------------------------------------------------------------------------------------- sizes, counted on what arrives

    // Ported from Body_longer_than_the_declared_size_is_cut and Oversized_release_is_refused_before_downloading.
    [Fact]
    public async Task An_API_answer_over_one_MiB_is_refused_whatever_it_says_about_its_length()
    {
        _rig.Gh.BytesWithoutLength(FakeRelease.Api, new byte[UpdateHttp.MaxJsonBytes + 1]);

        var result = await _rig.Check();

        Assert.Equal(UpdateRefusal.BodyTooLarge, result.Refusal);
        Assert.Equal(1, _rig.Gh.Count);
    }

    [Fact]
    public async Task A_SHA256_txt_over_64_KiB_is_refused_though_the_release_said_it_is_small()
    {
        _rig.Release();
        _rig.Gh.BytesWithDeclaredLength(ShaUrl, new byte[Sha256List.MaxBytes + 1], declared: 100);

        var result = await _rig.Check();

        Assert.Equal(UpdateRefusal.BodyTooLarge, result.Refusal);
        Assert.Equal(2, _rig.Gh.Count);
    }

    [Fact]
    public async Task A_manifest_over_its_limit_is_refused_though_the_release_said_it_is_small()
    {
        _rig.Release();
        _rig.Gh.BytesWithoutLength(ManifestUrl, new byte[Limits.Default.ManifestBytes + 1]);

        var result = await _rig.Check();

        Assert.Equal(UpdateRefusal.BodyTooLarge, result.Refusal);
        Assert.Equal(3, _rig.Gh.Count);
        Assert.False(_rig.Gh.AskedForAZip);
    }

    [Fact]
    public async Task An_answer_that_is_not_JSON_is_unreadable()
    {
        _rig.Gh.Bytes(FakeRelease.Api, Encoding.UTF8.GetBytes("<html>rate limit page</html>"));

        var result = await _rig.Check();

        Assert.Equal(UpdateRefusal.FeedUnreadable, result.Refusal);
        Assert.Equal(1, _rig.Gh.Count);
    }

    // ---------------------------------------------------------------------------------------------------------------- the connection

    [Fact]
    public async Task An_error_status_is_a_refusal_with_its_own_reason_and_counts_as_the_days_check()
    {
        // Ported from Failure_still_counts_as_todays_check.
        _rig.Gh.Status(FakeRelease.Api, HttpStatusCode.InternalServerError);
        var first = await _rig.Check();
        Assert.Equal(UpdateRefusal.HttpError, first.Refusal);
        Assert.Equal(1, _rig.Gh.Count);

        _rig.Clock.Advance(TimeSpan.FromHours(2));
        var second = await _rig.Check();

        Assert.Equal(UpdateCheckKind.NoCandidate, second.Kind);
        Assert.False(second.Fetched);
        Assert.Equal(1, _rig.Gh.Count); // not asked again two hours later

        _rig.Clock.Advance(TimeSpan.FromHours(22));
        await _rig.Check();
        Assert.Equal(2, _rig.Gh.Count); // asked again a day after the first look
    }

    [Fact]
    public async Task No_answer_at_all_is_a_network_failure_not_an_exception()
    {
        _rig.Gh.Handle(FakeRelease.Api, _ => throw new HttpRequestException("no route to host"));

        var result = await _rig.Check();

        Assert.Equal(UpdateRefusal.NetworkFailure, result.Refusal);
        Assert.Equal(_rig.Clock.Now, _rig.State().LastCheckUtc);
    }

    [Fact]
    public async Task A_redirect_of_an_asset_to_a_release_host_is_followed_and_one_to_an_unlisted_host_is_a_refusal()
    {
        // Ported from Follows_the_redirect_to_github_release_assets and Never_follows_a_redirect_off_the_allow_list (the hops themselves: FeedHttpTests).
        var release = _rig.Release();
        const string cdn = "https://release-assets.githubusercontent.com/github-production-release-asset/1/abc?sig=x";
        _rig.Gh.Bytes(cdn, release.ShaBytes());
        _rig.Gh.Redirect(ShaUrl, cdn);
        Assert.Equal(UpdateCheckKind.Candidate, (await _rig.Check()).Kind);
        Assert.Contains(cdn, _rig.Gh.Urls);

        _rig.Clock.Advance(TimeSpan.FromDays(2));
        _rig.Gh.Redirect(ShaUrl, "https://evil.example/SHA256.txt");
        _rig.Gh.Bytes("https://evil.example/SHA256.txt", release.ShaBytes());
        var refused = await _rig.Check();
        Assert.Equal(UpdateRefusal.RedirectHostNotAllowed, refused.Refusal);
        Assert.DoesNotContain(_rig.Gh.Urls, u => u.Contains("evil.example", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------------------------- the rate limit

    private void RateLimit(HttpStatusCode code, TimeSpan? retryAfter = null, DateTimeOffset? reset = null, string remaining = "0", string url = FakeRelease.Api) =>
        _rig.Gh.Status(url, code, r =>
        {
            if (retryAfter is { } wait)
            {
                r.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
            }

            if (reset is { } at)
            {
                r.Headers.Add("X-RateLimit-Remaining", remaining);
                r.Headers.Add("X-RateLimit-Reset", at.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        });

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_rate_limit_without_Retry_After_waits_a_day_and_a_manual_check_waits_too(HttpStatusCode code)
    {
        // Ported from Rate_limit_backs_off_for_a_day_and_blocks_manual_checks.
        RateLimit(code);

        var first = await _rig.Check();

        Assert.Equal(UpdateCheckKind.Waiting, first.Kind);
        Assert.Equal(_rig.Clock.Now.AddHours(24), first.WaitUntil);
        Assert.True(first.Fetched);
        Assert.Equal(_rig.Clock.Now.AddHours(24), _rig.State().BackoffUntilUtc);
        Assert.Equal(_rig.Clock.Now.AddHours(24), _rig.Service().ReadStatus().BackoffUntilUtc);

        // Twenty-three hours on: a scheduled and a manual check both wait, and neither asks.
        var count = _rig.Gh.Count;
        _rig.Clock.Advance(TimeSpan.FromHours(23));
        foreach (var manual in new[] { false, true })
        {
            var waiting = await _rig.Check(UpdateMode.Notify, manual);
            Assert.Equal(UpdateCheckKind.Waiting, waiting.Kind);
            Assert.False(waiting.Fetched);
        }

        Assert.Equal(count, _rig.Gh.Count);

        // The wait is over: the next check asks again and the wait is forgotten.
        _rig.Clock.Advance(TimeSpan.FromHours(1));
        _rig.Release();
        var after = await _rig.Check();
        Assert.Equal(UpdateCheckKind.Candidate, after.Kind);
        Assert.Null(_rig.State().BackoffUntilUtc);
    }

    [Theory]
    [InlineData(36, 36)]    // Retry-After longer than a day is respected
    [InlineData(2, 24)]     // shorter than a day: a day
    [InlineData(24 * 365, 24 * 7)] // a year: a week at most
    public async Task Retry_After_is_honoured_between_a_day_and_a_week(int retryHours, int expectedHours)
    {
        // Ported from Retry_after_longer_than_a_day_is_respected.
        RateLimit(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromHours(retryHours));

        var result = await _rig.Check();

        Assert.Equal(UpdateCheckKind.Waiting, result.Kind);
        Assert.Equal(_rig.Clock.Now.AddHours(expectedHours), result.WaitUntil);
    }

    [Fact]
    public async Task A_Retry_After_given_as_a_date_is_read_against_the_real_clock_and_still_waits_at_least_a_day()
    {
        // The date form is turned into a delay by the transport with the real clock (the fake clock is the service's own), so the assertion has room.
        _rig.Gh.Status(FakeRelease.Api, HttpStatusCode.Forbidden, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddHours(48)));

        var result = await _rig.Check();

        var wait = result.WaitUntil!.Value - _rig.Clock.Now;
        Assert.InRange(wait, TimeSpan.FromHours(47), TimeSpan.FromHours(48));
    }

    [Fact]
    public async Task The_reset_time_of_the_rate_limit_is_honoured_when_no_requests_remain()
    {
        RateLimit(HttpStatusCode.Forbidden, reset: _rig.Clock.Now.AddHours(40), remaining: "0");

        var result = await _rig.Check();

        Assert.Equal(_rig.Clock.Now.AddHours(40), result.WaitUntil);
    }

    [Fact]
    public async Task The_reset_time_is_not_read_when_requests_remain_so_a_403_for_another_reason_waits_a_day()
    {
        RateLimit(HttpStatusCode.Forbidden, reset: _rig.Clock.Now.AddHours(40), remaining: "12");

        var result = await _rig.Check();

        Assert.Equal(_rig.Clock.Now.AddHours(24), result.WaitUntil);
    }

    [Fact]
    public async Task A_rate_limit_on_a_later_request_of_the_check_waits_too()
    {
        _rig.Release();
        RateLimit(HttpStatusCode.TooManyRequests, url: ShaUrl);

        var result = await _rig.Check();

        Assert.Equal(UpdateCheckKind.Waiting, result.Kind);
        Assert.Equal(2, _rig.Gh.Count);
        Assert.Equal(_rig.Clock.Now.AddHours(24), _rig.State().BackoffUntilUtc);
    }

    [Fact]
    public async Task A_rate_limit_keeps_the_candidate_that_was_found_before_it()
    {
        _rig.Release();
        await _rig.Check();
        _rig.Clock.Advance(TimeSpan.FromHours(25));
        RateLimit(HttpStatusCode.Forbidden);

        await _rig.Check();

        // The wait is written; what was known of the release is not thrown away by a look that could not look.
        Assert.Equal("0.5.0", _rig.State().LatestVersion);
        Assert.NotNull(_rig.State().BackoffUntilUtc);
    }

    // ---------------------------------------------------------------------------------------------------------------- the state and the lock

    [Fact]
    public async Task The_time_of_the_look_is_written_before_the_first_request()
    {
        // A crash or a hang in the request must not make the next start look again at once.
        _rig.Release();
        DateTimeOffset? seenInTheFile = null;
        _rig.Gh.OnRequest = _ => seenInTheFile ??= _rig.Store.Read().LastCheckUtc;

        await _rig.Check();

        Assert.Equal(_rig.Clock.Now, seenInTheFile);
    }

    [Fact]
    public async Task Two_checks_at_once_make_one_request_set_and_the_second_is_told_another_check_runs()
    {
        _rig.Release();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ok = _rig.Gh.Routes[FakeRelease.Api];
        _rig.Gh.Handle(FakeRelease.Api, async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            return await ok(ct);
        });

        var first = Task.Run(() => _rig.Check(UpdateMode.Notify, manual: true));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var second = await _rig.Check(UpdateMode.Notify, manual: true);
        Assert.Equal(UpdateRefusal.AnotherCheckRunning, second.Refusal);
        Assert.False(second.Fetched);
        Assert.Equal(1, _rig.Gh.Count);

        release.SetResult();
        Assert.Equal(UpdateCheckKind.Candidate, (await first).Kind);
        Assert.Equal(3, _rig.Gh.Count);
    }

    [Fact]
    public async Task The_lock_is_let_go_after_the_check_so_the_next_check_can_take_it()
    {
        _rig.Release();
        await _rig.Check();

        Assert.Equal(LockOutcome.Acquired, _rig.Store.TryLock(out var held));
        held!.Dispose();
    }

    [Fact]
    public async Task Two_installs_do_not_share_a_state_folder_or_a_lock()
    {
        // The id of an install is in the folder name (InstallId); two folders are two states.
        _rig.Release();
        await _rig.Check();
        var other = new UpdateStateStore(Path.Combine(_rig.Temp, "update", "another-install"));

        Assert.Null(other.Read().LastCheckUtc);
        Assert.Equal(LockOutcome.Acquired, other.TryLock(out var held));
        held!.Dispose();
    }

    // ---------------------------------------------------------------------------------------------------------------- cancelling

    [Fact]
    public async Task A_check_that_is_cancelled_while_it_waits_for_an_answer_throws_the_callers_cancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.Gh.Handle(FakeRelease.Api, async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource();

        var check = _rig.Check(UpdateMode.Notify, manual: true, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Equal(LockOutcome.Acquired, _rig.Store.TryLock(out var held)); // the lock is let go on the way out
        held!.Dispose();
    }

    [Fact]
    public async Task The_zip_is_never_asked_for_by_any_check_of_this_class_even_with_the_zip_route_present()
    {
        var release = _rig.Release();
        _rig.Gh.Bytes(ZipUrl, release.ZipBytes);

        await _rig.Check();
        _rig.Clock.Advance(TimeSpan.FromDays(2));
        await _rig.Check(UpdateMode.Automatic, manual: true);

        Assert.False(_rig.Gh.AskedForAZip);
        Assert.DoesNotContain(_rig.Gh.Urls, u => u.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }
}
