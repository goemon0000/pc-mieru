// Provenance: ported from RulesTests of the owner's pl-cloud (Parts Life, MIT, same owner; see NOTICE.txt), with the old name in a comment: Reads_the_recorded_shape,
// Drafts_and_prereleases_are_not_updates, Odd_tag_or_foreign_page_is_rejected, Assets_pointing_elsewhere_or_unfinished_are_dropped, An_asset_url_for_another_tag_is_dropped,
// Garbage_is_not_an_update, Digest_is_read_only_when_well_formed, Api_is_the_one_url, Download_hops_are_allow_listed, Release_page_must_be_this_repo, At_most_once_a_day,
// A_clock_set_backwards_does_not_block_checks, Rate_limit_backs_off_a_day_or_longer_if_told, Backoff_blocks_even_manual_checks_until_it_ends, Rate_limit_statuses,
// Mode_names, Mode_names_round_trip. What is new: nothing is dropped quietly (each broken rule has its own refusal), the repo is goemon0000/pc-mieru with three files
// per release, and there is no loopback test feed.
using System.Net;
using PcMieru.Update.Bundle;
using PcMieru.Update.Feed;

namespace PcMieru.Update.Tests;

/// <summary>The API's answer and the three files, read by <see cref="ReleaseParser"/>. Pure: no network, no file.</summary>
public sealed class ReleaseParserTests
{
    private static string Zero(char c) => new(c, 64);

    /// <summary>The shape GitHub's "releases/latest" answers with (more fields than are read), for this repo and a made-up v0.5.0.</summary>
    private static string Recorded() => $$"""
        {
          "url": "https://api.github.com/repos/goemon0000/pc-mieru/releases/409306903",
          "assets_url": "https://api.github.com/repos/goemon0000/pc-mieru/releases/409306903/assets",
          "html_url": "https://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0",
          "id": 409306903,
          "node_id": "RE_kwDOExample",
          "tag_name": "v0.5.0",
          "target_commitish": "main",
          "name": "v0.5.0",
          "draft": false,
          "immutable": false,
          "prerelease": false,
          "created_at": "2026-11-20T00:58:46Z",
          "published_at": "2026-11-20T01:01:25Z",
          "author": { "login": "goemon0000", "id": 1, "type": "User" },
          "assets": [
            {
              "url": "https://api.github.com/repos/goemon0000/pc-mieru/releases/assets/1",
              "id": 1,
              "name": "PCMieru-0.5.0-win-x64.zip",
              "label": "",
              "content_type": "application/zip",
              "state": "uploaded",
              "size": 62367210,
              "digest": "sha256:{{Zero('2')}}",
              "download_count": 0,
              "browser_download_url": "https://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/PCMieru-0.5.0-win-x64.zip",
              "uploader": { "login": "github-actions[bot]", "id": 41898282, "type": "Bot" }
            },
            {
              "id": 2,
              "name": "PCMieru-0.5.0-win-x64.manifest.json",
              "state": "uploaded",
              "size": 2048,
              "digest": "sha256:{{Zero('3')}}",
              "browser_download_url": "https://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/PCMieru-0.5.0-win-x64.manifest.json"
            },
            {
              "id": 3,
              "name": "SHA256.txt",
              "state": "uploaded",
              "size": 162,
              "digest": "sha256:{{Zero('4')}}",
              "browser_download_url": "https://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/SHA256.txt"
            }
          ]
        }
        """;

    private static ReleaseInfo Parse(string json)
    {
        Assert.True(ReleaseParser.TryParse(json, out var release, out var refusal, out var detail), $"{refusal}: {detail}");
        Assert.Equal(UpdateRefusal.None, refusal);
        return release!;
    }

    private static UpdateRefusal ParseRefusal(string json)
    {
        Assert.False(ReleaseParser.TryParse(json, out var release, out var refusal, out var detail));
        Assert.Null(release);
        Assert.NotEqual(UpdateRefusal.None, refusal);
        Assert.NotEmpty(detail);
        return refusal;
    }

    // Ported from Reads_the_recorded_shape.
    [Fact]
    public void The_recorded_shape_is_read_with_its_extra_fields_ignored()
    {
        var release = Parse(Recorded());

        Assert.Equal(new AppVersion(0, 5, 0), release.Version);
        Assert.Equal("v0.5.0", release.Tag);
        Assert.Equal("https://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0", release.PageUrl);
        Assert.Equal(["PCMieru-0.5.0-win-x64.manifest.json", "PCMieru-0.5.0-win-x64.zip", "SHA256.txt"], release.Assets.Select(a => a.Name).Order(StringComparer.Ordinal).ToArray());
        var zip = release.Assets.Single(a => a.Name.EndsWith(".zip", StringComparison.Ordinal));
        Assert.Equal("https://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/PCMieru-0.5.0-win-x64.zip", zip.Url);
        Assert.Equal("sha256:" + Zero('2'), zip.Digest);
        Assert.Equal(62367210, zip.Size);
        Assert.Equal("uploaded", zip.State);

        Assert.True(ReleaseParser.TrySelect(release, out var assets, out var refusal, out _));
        Assert.Equal(UpdateRefusal.None, refusal);
        Assert.Equal("SHA256.txt", assets!.HashList.Name);
        Assert.Equal("PCMieru-0.5.0-win-x64.manifest.json", assets.Manifest.Name);
        Assert.Equal("PCMieru-0.5.0-win-x64.zip", assets.Zip.Name);
    }

    // Ported from Drafts_and_prereleases_are_not_updates.
    [Fact]
    public void Drafts_and_prereleases_each_have_their_own_reason()
    {
        Assert.Equal(UpdateRefusal.DraftRelease, ParseRefusal(Recorded().Replace("\"draft\": false", "\"draft\": true")));
        Assert.Equal(UpdateRefusal.PrereleaseRelease, ParseRefusal(Recorded().Replace("\"prerelease\": false", "\"prerelease\": true")));
    }

    // Ported from Odd_tag_or_foreign_page_is_rejected.
    [Theory]
    [InlineData("\"tag_name\": \"v0.5.0\"", "\"tag_name\": \"v0.5.0-rc1\"", UpdateRefusal.TagNotStable)]
    [InlineData("\"tag_name\": \"v0.5.0\"", "\"tag_name\": \"v0.5.0+build\"", UpdateRefusal.TagNotStable)]
    [InlineData("\"tag_name\": \"v0.5.0\"", "\"tag_name\": \"0.5.0\"", UpdateRefusal.TagNotStable)]
    [InlineData("\"tag_name\": \"v0.5.0\"", "\"tag_name\": \"V0.5.0\"", UpdateRefusal.TagNotStable)]
    [InlineData("\"tag_name\": \"v0.5.0\"", "\"tag_name\": \"v0.05.0\"", UpdateRefusal.TagNotStable)]
    [InlineData("\"tag_name\": \"v0.5.0\"", "\"tag_name\": \"v0.5\"", UpdateRefusal.TagNotStable)]
    [InlineData("\"tag_name\": \"v0.5.0\"", "\"tag_name\": \"latest\"", UpdateRefusal.TagNotStable)]
    [InlineData("\"tag_name\": \"v0.5.0\"", "\"tag_name\": null", UpdateRefusal.FeedUnreadable)]
    [InlineData("\"tag_name\": \"v0.5.0\"", "\"tag_name\": \"\"", UpdateRefusal.FeedUnreadable)]
    [InlineData("\"html_url\": \"https://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0\"", "\"html_url\": \"https://github.com/someone-else/pc-mieru/releases/tag/v0.5.0\"", UpdateRefusal.ReleasePageMismatch)]
    [InlineData("\"html_url\": \"https://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0\"", "\"html_url\": \"https://github.com/goemon0000/pc-mieru/releases/tag/v0.4.9\"", UpdateRefusal.ReleasePageMismatch)]
    [InlineData("\"html_url\": \"https://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0\"", "\"html_url\": \"https://evil.example/goemon0000/pc-mieru/releases/tag/v0.5.0\"", UpdateRefusal.ReleasePageMismatch)]
    [InlineData("\"html_url\": \"https://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0\"", "\"html_url\": \"https://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0?x=1\"", UpdateRefusal.ReleasePageMismatch)]
    [InlineData("\"html_url\": \"https://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0\"", "\"html_url\": null", UpdateRefusal.ReleasePageMismatch)]
    public void An_odd_tag_or_a_foreign_page_is_refused_with_its_reason(string from, string to, UpdateRefusal reason)
    {
        var json = Recorded().Replace(from, to);
        Assert.NotEqual(Recorded(), json);

        Assert.Equal(reason, ParseRefusal(json));
    }

    // Ported from Assets_pointing_elsewhere_or_unfinished_are_dropped and An_asset_url_for_another_tag_is_dropped; nothing is dropped quietly here, each is a reason.
    [Theory]
    [InlineData("releases/download/v0.5.0/PCMieru-0.5.0-win-x64.zip", "releases/download/v0.4.9/PCMieru-0.5.0-win-x64.zip", UpdateRefusal.AssetUrlMismatch)]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/SHA256.txt", "https://evil.example/SHA256.txt", UpdateRefusal.AssetUrlMismatch)]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/SHA256.txt", "http://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/SHA256.txt", UpdateRefusal.AssetUrlMismatch)]
    [InlineData("\"size\": 162", "\"size\": 0", UpdateRefusal.AssetNotReady)]
    [InlineData("\"name\": \"SHA256.txt\"", "\"name\": \"sha256.txt\"", UpdateRefusal.AssetMissing)]
    [InlineData("\"name\": \"SHA256.txt\"", "\"name\": null", UpdateRefusal.AssetMissing)]
    [InlineData("\"name\": \"PCMieru-0.5.0-win-x64.manifest.json\"", "\"name\": \"SHA256.txt\"", UpdateRefusal.AssetDuplicate)]
    public void A_file_that_breaks_a_rule_is_a_refusal_of_its_own_and_not_dropped_quietly(string from, string to, UpdateRefusal reason)
    {
        var json = Recorded().Replace(from, to);
        Assert.NotEqual(Recorded(), json);
        var release = Parse(json);

        Assert.False(ReleaseParser.TrySelect(release, out var assets, out var refusal, out var detail));
        Assert.Null(assets);
        Assert.Equal(reason, refusal);
        Assert.NotEmpty(detail);
    }

    [Theory]
    [InlineData("starter")]
    [InlineData("open")]
    [InlineData("Uploaded")]
    [InlineData("")]
    public void A_file_that_is_not_finished_uploading_is_not_ready(string state)
    {
        var release = Parse(Recorded());
        var unfinished = new ReleaseInfo(release.Version, release.PageUrl,
            [.. release.Assets.Select(a => a.Name == "SHA256.txt" ? a with { State = state } : a)]);

        Assert.False(ReleaseParser.TrySelect(unfinished, out _, out var refusal, out _));
        Assert.Equal(UpdateRefusal.AssetNotReady, refusal);
    }

    [Fact]
    public void A_release_without_a_list_of_files_has_none_of_the_three()
    {
        var release = Parse(Recorded().Replace("\"assets\": [", "\"assets_gone\": ["));

        Assert.Empty(release.Assets);
        Assert.False(ReleaseParser.TrySelect(release, out _, out var refusal, out _));
        Assert.Equal(UpdateRefusal.AssetMissing, refusal);
    }

    [Fact]
    public void The_sizes_have_their_limits_by_file()
    {
        var release = Parse(Recorded());
        Assert.Equal(Sha256List.MaxBytes, 64 * 1024);

        ReleaseInfo WithSize(string name, long size) => new(release.Version, release.PageUrl,
            [.. release.Assets.Select(a => a.Name == name ? a with { Size = size } : a)]);

        Assert.True(ReleaseParser.TrySelect(WithSize("SHA256.txt", Sha256List.MaxBytes), out _, out _, out _));
        Assert.False(ReleaseParser.TrySelect(WithSize("SHA256.txt", Sha256List.MaxBytes + 1), out _, out var a, out _));
        Assert.Equal(UpdateRefusal.AssetTooLarge, a);
        Assert.True(ReleaseParser.TrySelect(WithSize("PCMieru-0.5.0-win-x64.manifest.json", Limits.Default.ManifestBytes), out _, out _, out _));
        Assert.False(ReleaseParser.TrySelect(WithSize("PCMieru-0.5.0-win-x64.manifest.json", Limits.Default.ManifestBytes + 1), out _, out var b, out _));
        Assert.Equal(UpdateRefusal.AssetTooLarge, b);
        Assert.True(ReleaseParser.TrySelect(WithSize("PCMieru-0.5.0-win-x64.zip", Limits.Default.ZipBytes), out _, out _, out _));
        Assert.False(ReleaseParser.TrySelect(WithSize("PCMieru-0.5.0-win-x64.zip", Limits.Default.ZipBytes + 1), out _, out var c, out _));
        Assert.Equal(UpdateRefusal.AssetTooLarge, c);
    }

    // Ported from Garbage_is_not_an_update.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"v0.5.0\"")]
    [InlineData("{")]
    [InlineData("<html>rate limited</html>")]
    [InlineData("{\"tag_name\":42}")]
    [InlineData("{\"tag_name\":\"v0.5.0\",\"draft\":\"yes\"}")]
    [InlineData("{}")]
    public void Garbage_is_unreadable_not_an_update_and_not_an_exception(string json)
    {
        Assert.Equal(UpdateRefusal.FeedUnreadable, ParseRefusal(json));
    }

    // Ported from Digest_is_read_only_when_well_formed.
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("sha512:abcdef", true)]                  // another algorithm is not read
    [InlineData("md5:00", true)]
    [InlineData("sha256:SAME", true)]
    [InlineData("SHA256:SAME", true)]
    [InlineData("sha256:SAMEUP", true)]
    [InlineData("sha256:OTHER", false)]
    [InlineData("sha256:xyz", false)]
    [InlineData("sha256:", false)]
    [InlineData("sha256:SHORT", false)]
    [InlineData("sha256:LONG", false)]
    public void A_digest_is_compared_only_when_it_says_sha256_and_then_it_must_be_well_formed_and_equal(string? digest, bool agrees)
    {
        var hash = Zero('a');
        digest = digest switch
        {
            "sha256:SAME" => "sha256:" + hash,
            "SHA256:SAME" => "SHA256:" + hash,
            "sha256:SAMEUP" => "sha256:" + hash.ToUpperInvariant(),
            "sha256:OTHER" => "sha256:" + Zero('b'),
            "sha256:SHORT" => "sha256:" + hash[1..],
            "sha256:LONG" => "sha256:" + hash + "a",
            _ => digest,
        };

        Assert.Equal(agrees, ReleaseParser.DigestAgrees(digest, hash));
    }
}

/// <summary>The addresses the feed may talk to. Pure.</summary>
public sealed class EndpointTests
{
    [Fact]
    public void The_constants_name_this_repo_and_nothing_else()
    {
        Assert.Equal("goemon0000", UpdateEndpoints.Owner);
        Assert.Equal("pc-mieru", UpdateEndpoints.Repo);
        Assert.Equal("https://api.github.com/repos/goemon0000/pc-mieru/releases/latest", UpdateEndpoints.LatestRelease.AbsoluteUri);
        Assert.Equal("https://github.com/goemon0000/pc-mieru/releases/download/", UpdateEndpoints.DownloadPrefix);
        Assert.Equal("https://github.com/goemon0000/pc-mieru/releases/tag/", UpdateEndpoints.ReleasePagePrefix);
        Assert.Equal("https://github.com/goemon0000/pc-mieru/releases", UpdateEndpoints.ReleasesPage);
        Assert.Equal(["release-assets.githubusercontent.com", "objects.githubusercontent.com"], UpdateEndpoints.RedirectHosts.ToArray());
    }

    // Ported from Api_is_the_one_url.
    [Theory]
    [InlineData("https://api.github.com/repos/goemon0000/pc-mieru/releases/latest", true)]
    [InlineData("https://API.GITHUB.COM/repos/goemon0000/pc-mieru/releases/latest", true)]
    [InlineData("https://api.github.com/repos/goemon0000/pc-mieru/releases/latest?x=1", false)]
    [InlineData("https://api.github.com/repos/goemon0000/pc-mieru/releases/latest#x", false)]
    [InlineData("https://api.github.com/repos/goemon0000/pc-mieru/releases", false)]
    [InlineData("https://api.github.com/repos/goemon0000/parts-life/releases/latest", false)]
    [InlineData("http://api.github.com/repos/goemon0000/pc-mieru/releases/latest", false)]
    [InlineData("https://api.github.com:8443/repos/goemon0000/pc-mieru/releases/latest", false)]
    [InlineData("https://user@api.github.com/repos/goemon0000/pc-mieru/releases/latest", false)]
    [InlineData("https://github.com/repos/goemon0000/pc-mieru/releases/latest", false)]
    public void The_API_is_the_one_address(string url, bool allowed)
    {
        Assert.Equal(allowed, UpdateEndpoints.IsAllowedApi(new Uri(url)));
    }

    [Fact]
    public void A_missing_or_relative_address_is_not_plain()
    {
        Assert.False(UpdateEndpoints.IsPlain(null));
        Assert.False(UpdateEndpoints.IsPlain(new Uri("/releases", UriKind.Relative)));
        Assert.False(UpdateEndpoints.IsAllowedApi(null));
        Assert.False(UpdateEndpoints.IsAllowedDownloadHop(null, new Uri("https://github.com/x")));
        Assert.True(UpdateEndpoints.SameHost(new Uri("https://GitHub.com/a"), new Uri("https://github.com/b")));
    }

    [Fact]
    public void An_asset_address_is_this_repos_download_prefix_the_tag_and_the_name()
    {
        Assert.Equal(
            "https://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/PCMieru-0.5.0-win-x64.zip",
            UpdateEndpoints.AssetUrl("v0.5.0", BundleFormat.ZipName("0.5.0")).AbsoluteUri);
        Assert.Equal(
            "https://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/SHA256.txt",
            UpdateEndpoints.AssetUrl("v0.5.0", BundleFormat.HashListName).AbsoluteUri);

        // A tag or a name that tries to be more than one path segment is one segment.
        var tricky = UpdateEndpoints.AssetUrl("v0.5.0/../../x", "a/b?c#d").AbsoluteUri;
        Assert.StartsWith(UpdateEndpoints.DownloadPrefix, tricky, StringComparison.Ordinal);
        Assert.DoesNotContain("?", tricky, StringComparison.Ordinal);
        Assert.DoesNotContain("#", tricky, StringComparison.Ordinal);
        Assert.Equal(1, tricky[UpdateEndpoints.DownloadPrefix.Length..].Count(c => c == '/')); // the one slash between the tag and the name
    }

    // Ported from Download_hops_are_allow_listed.
    [Theory]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/SHA256.txt", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/abc?sig=x", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset-2e65be/1/abc", true)]
    [InlineData("https://RELEASE-ASSETS.githubusercontent.com/x", true)]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/other.txt", false)]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/download/v0.4.9/SHA256.txt", false)]
    [InlineData("https://github.com/someone/else/releases/download/v0.5.0/SHA256.txt", false)]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/SHA256.txt?x=1", false)]
    [InlineData("http://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/SHA256.txt", false)]
    [InlineData("http://release-assets.githubusercontent.com/x", false)]
    [InlineData("https://release-assets.githubusercontent.com:8443/x", false)]
    [InlineData("https://user:pw@release-assets.githubusercontent.com/x", false)]
    [InlineData("https://githubusercontent.com/x", false)]
    [InlineData("https://raw.githubusercontent.com/x", false)]
    [InlineData("https://release-assets.githubusercontent.com.evil.example/x", false)]
    [InlineData("https://evil.example/SHA256.txt", false)]
    [InlineData("https://api.github.com/repos/goemon0000/pc-mieru/releases/latest", false)]
    public void A_download_hop_is_the_assets_own_address_or_a_release_host(string url, bool allowed)
    {
        var asset = new Uri("https://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/SHA256.txt");

        Assert.Equal(allowed, UpdateEndpoints.IsAllowedDownloadHop(new Uri(url), asset));
    }

    // Ported from Release_page_must_be_this_repo.
    [Theory]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0", true)]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/tag/v10.20.30", true)]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/tag/", false)]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0/", false)]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0?x=1", false)]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0#x", false)]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0 x", false)]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0\\x", false)]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/tag/a/b", false)]
    [InlineData("http://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0", false)]
    [InlineData("https://github.com/someone/else/releases/tag/v0.5.0", false)]
    [InlineData("https://github.com:8443/goemon0000/pc-mieru/releases/tag/v0.5.0", false)]
    [InlineData("https://evil.example/goemon0000/pc-mieru/releases/tag/v0.5.0", false)]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_release_page_is_this_repos_prefix_and_one_segment_and_nothing_after(string? url, bool ok)
    {
        Assert.Equal(ok, UpdateEndpoints.IsReleasePage(url));
    }

    [Fact]
    public void The_page_of_a_tag_is_the_prefix_and_the_tag()
    {
        Assert.Equal("https://github.com/goemon0000/pc-mieru/releases/tag/v0.5.0", UpdateEndpoints.ReleasePageUrl("v0.5.0"));
        Assert.True(UpdateEndpoints.IsReleasePage(UpdateEndpoints.ReleasePageUrl("v0.5.0")));
    }
}

/// <summary>When the feed may be asked, as pure functions of the state and a time.</summary>
public sealed class ScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_intervals_are_those_of_the_design()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), UpdateSchedule.FirstDelay);
        Assert.Equal(TimeSpan.FromHours(24), UpdateSchedule.Interval);
        Assert.Equal(TimeSpan.FromHours(1), UpdateSchedule.Poll);
        Assert.Equal(TimeSpan.FromHours(24), UpdateSchedule.RateLimitBackoff);
        Assert.Equal(TimeSpan.FromDays(7), UpdateSchedule.MaxBackoff);
    }

    // Ported from At_most_once_a_day.
    [Fact]
    public void A_scheduled_check_is_due_on_the_first_run_and_then_a_day_after_the_last()
    {
        Assert.True(UpdateSchedule.IsDue(new UpdateState(), Now));
        Assert.False(UpdateSchedule.IsDue(new UpdateState { LastCheckUtc = Now }, Now));
        Assert.False(UpdateSchedule.IsDue(new UpdateState { LastCheckUtc = Now.AddHours(-23).AddMinutes(-59) }, Now));
        Assert.True(UpdateSchedule.IsDue(new UpdateState { LastCheckUtc = Now.AddHours(-24) }, Now));
        Assert.True(UpdateSchedule.IsDue(new UpdateState { LastCheckUtc = Now.AddDays(-30) }, Now));
    }

    // Ported from A_clock_set_backwards_does_not_block_checks.
    [Fact]
    public void A_last_check_in_the_future_does_not_block_checks()
    {
        Assert.True(UpdateSchedule.IsDue(new UpdateState { LastCheckUtc = Now.AddDays(5) }, Now));
        Assert.Null(UpdateSchedule.NextDue(new UpdateState { LastCheckUtc = Now.AddDays(5) }, Now));
    }

    // Ported from Rate_limit_backs_off_a_day_or_longer_if_told.
    [Fact]
    public void A_rate_limit_waits_a_day_or_what_the_server_asks_and_never_more_than_a_week()
    {
        Assert.Equal(Now.AddHours(24), UpdateSchedule.BackoffUntil(Now, null, null));
        Assert.Equal(Now.AddHours(24), UpdateSchedule.BackoffUntil(Now, TimeSpan.FromHours(2), null));
        Assert.Equal(Now.AddHours(24), UpdateSchedule.BackoffUntil(Now, TimeSpan.FromSeconds(-30), null));
        Assert.Equal(Now.AddHours(36), UpdateSchedule.BackoffUntil(Now, TimeSpan.FromHours(36), null));
        Assert.Equal(Now.AddHours(40), UpdateSchedule.BackoffUntil(Now, null, Now.AddHours(40)));
        Assert.Equal(Now.AddHours(24), UpdateSchedule.BackoffUntil(Now, null, Now.AddHours(3)));
        Assert.Equal(Now.AddHours(40), UpdateSchedule.BackoffUntil(Now, TimeSpan.FromHours(36), Now.AddHours(40)));
        Assert.Equal(Now.AddHours(48), UpdateSchedule.BackoffUntil(Now, TimeSpan.FromHours(48), Now.AddHours(40)));
        Assert.Equal(Now.AddDays(7), UpdateSchedule.BackoffUntil(Now, TimeSpan.FromDays(365), null));
        Assert.Equal(Now.AddDays(7), UpdateSchedule.BackoffUntil(Now, null, Now.AddDays(365)));
    }

    // Ported from Backoff_blocks_even_manual_checks_until_it_ends.
    [Fact]
    public void A_back_off_blocks_scheduled_and_manual_checks_until_it_ends_and_a_far_future_is_not_believed()
    {
        var waiting = new UpdateState { LastCheckUtc = Now.AddDays(-9), BackoffUntilUtc = Now.AddHours(5) };
        Assert.True(UpdateSchedule.InBackoff(waiting, Now));
        Assert.False(UpdateSchedule.IsDue(waiting, Now));
        Assert.False(UpdateSchedule.CanCheckNow(waiting, Now));

        // The instant it ends, and after it, both are free again.
        Assert.False(UpdateSchedule.InBackoff(waiting, Now.AddHours(5)));
        Assert.True(UpdateSchedule.IsDue(waiting, Now.AddHours(5)));
        Assert.True(UpdateSchedule.CanCheckNow(waiting, Now.AddHours(5)));

        // Exactly a week away is believed; a second more is not (a damaged file must not switch the feed off for years).
        Assert.True(UpdateSchedule.InBackoff(new UpdateState { BackoffUntilUtc = Now.AddDays(7) }, Now));
        Assert.False(UpdateSchedule.InBackoff(new UpdateState { BackoffUntilUtc = Now.AddDays(7).AddSeconds(1) }, Now));
        Assert.True(UpdateSchedule.CanCheckNow(new UpdateState { BackoffUntilUtc = Now.AddYears(30) }, Now));
        Assert.False(UpdateSchedule.InBackoff(new UpdateState(), Now));
    }

    [Fact]
    public void The_next_due_time_is_a_day_after_the_last_check_or_the_end_of_a_longer_wait()
    {
        Assert.Null(UpdateSchedule.NextDue(new UpdateState(), Now));
        Assert.Equal(Now.AddHours(24), UpdateSchedule.NextDue(new UpdateState { LastCheckUtc = Now }, Now));
        Assert.Equal(Now.AddHours(-1), UpdateSchedule.NextDue(new UpdateState { LastCheckUtc = Now.AddHours(-25) }, Now));
        Assert.Equal(Now.AddHours(40), UpdateSchedule.NextDue(new UpdateState { LastCheckUtc = Now, BackoffUntilUtc = Now.AddHours(40) }, Now));
        Assert.Equal(Now.AddHours(24), UpdateSchedule.NextDue(new UpdateState { LastCheckUtc = Now, BackoffUntilUtc = Now.AddHours(3) }, Now));
    }

    // Ported from Rate_limit_statuses.
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public void Only_403_and_429_are_a_rate_limit(HttpStatusCode code, bool limited)
    {
        Assert.Equal(limited, UpdateSchedule.IsRateLimited((int)code));
    }
}

/// <summary>The three words of gui.ini and the mode they name. Pure.</summary>
public sealed class ModeTests
{
    // Ported from Mode_names.
    [Theory]
    [InlineData("auto", UpdateMode.Automatic)]
    [InlineData("notify", UpdateMode.Notify)]
    [InlineData("off", UpdateMode.Off)]
    [InlineData("NOTIFY", UpdateMode.Notify)]
    [InlineData("Auto", UpdateMode.Automatic)]
    [InlineData("  off  ", UpdateMode.Off)]
    [InlineData("ask", UpdateMode.Ask)]
    [InlineData("automatic", UpdateMode.Ask)]
    [InlineData("on", UpdateMode.Ask)]
    [InlineData("yes", UpdateMode.Ask)]
    [InlineData("1", UpdateMode.Ask)]
    [InlineData("", UpdateMode.Ask)]
    [InlineData("   ", UpdateMode.Ask)]
    [InlineData(null, UpdateMode.Ask)]
    [InlineData("notify;off", UpdateMode.Ask)]
    public void A_word_that_is_not_one_of_the_three_is_ask(string? text, UpdateMode expected)
    {
        Assert.Equal(expected, UpdateModes.FromIni(text));
    }

    // Ported from Mode_names_round_trip.
    [Fact]
    public void The_three_modes_round_trip_and_ask_is_the_absence_of_a_word()
    {
        Assert.Equal("update.mode", UpdateModes.IniKey);
        Assert.Equal("auto", UpdateModes.ToIni(UpdateMode.Automatic));
        Assert.Equal("notify", UpdateModes.ToIni(UpdateMode.Notify));
        Assert.Equal("off", UpdateModes.ToIni(UpdateMode.Off));
        Assert.Null(UpdateModes.ToIni(UpdateMode.Ask));
        foreach (var mode in new[] { UpdateMode.Automatic, UpdateMode.Notify, UpdateMode.Off })
        {
            Assert.Equal(mode, UpdateModes.FromIni(UpdateModes.ToIni(mode)));
        }
    }

    [Fact]
    public void Ask_is_the_default_so_that_a_value_nobody_set_asks()
    {
        Assert.Equal(UpdateMode.Ask, default(UpdateMode));
        Assert.Equal(0, (int)UpdateMode.Ask);
    }
}

/// <summary>The hourly look of a running app, driven with a clock of the test's own: a day apart whatever the polling, and the first look at once.</summary>
[Collection(FeedCollection.Name)]
public sealed class ScheduleSimulationTests : IDisposable
{
    private readonly FeedRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task Polled_every_hour_for_three_days_the_feed_is_asked_once_a_day()
    {
        _rig.Release("0.4.0"); // nothing newer: each real check is the one API request and nothing more

        for (var hour = 0; hour <= 72; hour++)
        {
            var result = await _rig.Check(UpdateMode.Notify, manual: false);
            Assert.Equal(hour % 24 == 0, result.Fetched);
            _rig.Clock.Advance(UpdateSchedule.Poll);
        }

        Assert.Equal(4, _rig.Gh.Count); // at hours 0, 24, 48 and 72
        Assert.All(_rig.Gh.Seen, s => Assert.Equal(FakeRelease.Api, s.Url));
    }

    [Fact]
    public async Task A_clock_set_back_does_not_stop_the_feed_for_days()
    {
        _rig.Release("0.4.0");
        await _rig.Check();
        Assert.Equal(1, _rig.Gh.Count);

        _rig.Clock.Advance(TimeSpan.FromDays(-3)); // the last look is now three days in the future
        await _rig.Check();

        Assert.Equal(2, _rig.Gh.Count);
    }

    [Fact]
    public async Task A_manual_check_ignores_the_day_but_a_scheduled_one_after_it_does_not_ask_again_at_once()
    {
        _rig.Release("0.4.0");
        await _rig.Check();

        _rig.Clock.Advance(TimeSpan.FromHours(2));
        await _rig.Check(UpdateMode.Notify, manual: true);
        Assert.Equal(2, _rig.Gh.Count);

        _rig.Clock.Advance(TimeSpan.FromHours(2));
        var scheduled = await _rig.Check(UpdateMode.Notify, manual: false);
        Assert.False(scheduled.Fetched);
        Assert.Equal(2, _rig.Gh.Count); // the manual look counted as the day's look
    }
}
