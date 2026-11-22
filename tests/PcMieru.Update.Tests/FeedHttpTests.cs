// Provenance: Follows_the_redirect_to_github_release_assets, Never_follows_a_redirect_off_the_allow_list, Never_follows_a_downgrade_to_http, Redirect_loops_stop,
// Sends_only_user_agent_and_accept and Body_longer_than_the_declared_size_is_cut are ported from UpdaterTests of the owner's pl-cloud (Parts Life, MIT, same owner;
// see NOTICE.txt). The rest is new for this repo: hop by hop refusals with their own reasons, the five-hop limit, user names in a Location, caps on the bytes that
// really arrive, a zip address that is refused before anything is sent, Brotli never asked for.
using System.Net;
using System.Net.Http;
using PcMieru.Update.Bundle;
using PcMieru.Update.Feed;

namespace PcMieru.Update.Tests;

/// <summary>The HTTP client of the feed on its own (UpdateHttp), against a fake that remembers every request. Nothing real is asked.</summary>
[Collection(FeedCollection.Name)]
public sealed class FeedHttpTests
{
    private const string Sha = "https://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/SHA256.txt";
    private const string ZipAddress = "https://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/PCMieru-0.5.0-win-x64.zip";
    private static readonly byte[] Body = "0123456789abcdef"u8.ToArray();

    private readonly FakeGitHub _gh = new();

    private UpdateHttp Client(TimeSpan? api = null, TimeSpan? asset = null) => new("0.4.0", _gh, api, asset);

    private static Task<byte[]> GetAsset(UpdateHttp client, string url = Sha, int max = 4096, long declared = 100, CancellationToken ct = default) =>
        client.GetSmallAssetAsync(new Uri(url), declared, max, ct);

    private static HttpResponseMessage Moved(string to, HttpStatusCode code = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(code);
        response.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static string Hop(int i) => $"https://release-assets.githubusercontent.com/github-production-release-asset/1/hop{i}?sig=x";

    /// <summary>SHA256.txt moves through <paramref name="redirects"/> redirects to a release-asset host, and the last address answers.</summary>
    private void Chain(int redirects)
    {
        _gh.Redirect(Sha, Hop(1));
        for (var i = 1; i < redirects; i++)
        {
            _gh.Redirect(Hop(i), Hop(i + 1));
        }

        _gh.Bytes(Hop(redirects), Body);
    }

    private static async Task<UpdateHttpException> Refused(Task task)
    {
        var ex = await Assert.ThrowsAsync<UpdateHttpException>(() => task);
        return ex;
    }

    // ---------------------------------------------------------------------------------------------------------------- the handler of a real run

    [Fact]
    public void The_default_handler_follows_nothing_keeps_no_cookie_and_never_decompresses()
    {
        using var handler = UpdateHttp.CreateDefaultHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.PreAuthenticate);
        Assert.Null(handler.Credentials);
        Assert.Null(handler.DefaultProxyCredentials);

        // Brotli is never enabled: the audit advisory GHSA-p3jp-9v83-76w9 is suppressed in Directory.Build.props on exactly that promise.
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression & DecompressionMethods.Brotli);
    }

    [Fact]
    public void The_timeouts_are_bounded()
    {
        Assert.InRange(UpdateHttp.ApiTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2));
        Assert.InRange(UpdateHttp.AssetTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
        Assert.Equal(5, UpdateHttp.MaxRedirects);
        Assert.Equal(1024 * 1024, UpdateHttp.MaxJsonBytes);
    }

    [Fact]
    public async Task The_counters_move_with_a_client_and_with_each_request()
    {
        var clients = FeedTraffic.ClientsCreated;
        var requests = FeedTraffic.Requests;
        _gh.Bytes(FakeRelease.Api, "{}"u8.ToArray());
        _gh.Bytes(Sha, Body);

        using var client = Client();
        Assert.Equal(clients + 1, FeedTraffic.ClientsCreated);
        Assert.Equal(requests, FeedTraffic.Requests);

        await client.GetLatestReleaseJsonAsync(default);
        await GetAsset(client);

        Assert.Equal(requests + 2, FeedTraffic.Requests);
        Assert.Equal(2, _gh.Count);
    }

    // ---------------------------------------------------------------------------------------------------------------- what is sent

    // Ported from Sends_only_user_agent_and_accept.
    [Fact]
    public async Task Every_request_is_a_GET_with_only_a_user_agent_and_an_accept_and_no_body()
    {
        _gh.Bytes(FakeRelease.Api, "{}"u8.ToArray());
        _gh.Bytes(Sha, Body);
        using var client = new UpdateHttp("9.9.9", _gh);

        await client.GetLatestReleaseJsonAsync(default);
        await GetAsset(client);

        Assert.Equal(2, _gh.Count);
        Assert.All(_gh.Seen, s =>
        {
            Assert.Equal("GET", s.Method);
            Assert.Equal(new[] { "Accept", "User-Agent" }, s.HeaderNames);
            Assert.Equal("PCMieru/9.9.9", s.UserAgent);
            Assert.False(s.HadBody);
        });
        Assert.Equal("application/vnd.github+json", _gh.Seen[0].Accept);
        Assert.Equal("application/octet-stream", _gh.Seen[1].Accept);
    }

    [Fact]
    public async Task A_cookie_set_by_a_redirect_is_not_sent_back_and_no_other_header_appears_on_any_hop()
    {
        _gh.Respond(Sha, () =>
        {
            var response = Moved(Hop(1));
            response.Headers.TryAddWithoutValidation("Set-Cookie", "session=abc; Path=/; Secure");
            response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Basic realm=\"x\"");
            return response;
        });
        _gh.Bytes(Hop(1), Body);
        using var client = Client();

        await GetAsset(client);

        Assert.Equal(2, _gh.Count);
        Assert.All(_gh.Seen, s => Assert.Equal(new[] { "Accept", "User-Agent" }, s.HeaderNames));
    }

    [Fact]
    public async Task The_API_text_comes_back_as_it_was_sent()
    {
        _gh.Bytes(FakeRelease.Api, "{\"tag_name\":\"v0.5.0\"}"u8.ToArray());
        using var client = Client();

        Assert.Equal("{\"tag_name\":\"v0.5.0\"}", await client.GetLatestReleaseJsonAsync(default));
    }

    // ---------------------------------------------------------------------------------------------------------------- the API redirects to itself or nowhere

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task The_API_may_send_the_client_back_to_its_own_address_whatever_the_redirect_code(int code)
    {
        var calls = 0;
        _gh.Respond(FakeRelease.Api, () => ++calls == 1
            ? Moved(FakeRelease.Api, (HttpStatusCode)code)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        using var client = Client();

        Assert.Equal("{}", await client.GetLatestReleaseJsonAsync(default));
        Assert.Equal(2, _gh.Count);
    }

    [Fact]
    public async Task An_upper_case_spelling_of_the_API_host_is_the_same_host_and_passes()
    {
        // M11: host names are compared in lower case (Uri makes them so), so this one is not a look-alike.
        var calls = 0;
        _gh.Respond(FakeRelease.Api, () => ++calls == 1
            ? Moved("https://API.GITHUB.COM/repos/goemon0000/pc-mieru/releases/latest")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        using var client = Client();

        Assert.Equal("{}", await client.GetLatestReleaseJsonAsync(default));
        Assert.Equal(2, _gh.Count);
    }

    [Fact]
    public async Task A_relative_redirect_of_the_API_is_resolved_against_it_and_only_its_own_address_passes()
    {
        var calls = 0;
        _gh.Respond(FakeRelease.Api, () => ++calls == 1
            ? Moved("/repos/goemon0000/pc-mieru/releases/latest")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        using var client = Client();
        Assert.Equal("{}", await client.GetLatestReleaseJsonAsync(default));

        var other = new FakeGitHub();
        other.Respond(FakeRelease.Api, () => Moved("/repos/goemon0000/pc-mieru/releases"));
        using var second = new UpdateHttp("0.4.0", other);
        var ex = await Refused(second.GetLatestReleaseJsonAsync(default));
        Assert.Equal(UpdateRefusal.RedirectHostNotAllowed, ex.Reason);
        Assert.Equal(1, other.Count);
    }

    [Theory]
    [InlineData("https://api.github.com/repos/goemon0000/pc-mieru/releases", UpdateRefusal.RedirectHostNotAllowed)]
    [InlineData("https://api.github.com/repos/goemon0000/pc-mieru/releases/latest?x=1", UpdateRefusal.RedirectHostNotAllowed)]
    [InlineData("https://api.github.com/repos/someone/else/releases/latest", UpdateRefusal.RedirectHostNotAllowed)]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/latest", UpdateRefusal.RedirectHostNotAllowed)]
    [InlineData("https://release-assets.githubusercontent.com/x", UpdateRefusal.RedirectHostNotAllowed)]
    [InlineData("https://evil.example/repos/goemon0000/pc-mieru/releases/latest", UpdateRefusal.RedirectHostNotAllowed)]
    [InlineData("https://api.github.com:8443/repos/goemon0000/pc-mieru/releases/latest", UpdateRefusal.RedirectHostNotAllowed)]
    [InlineData("http://api.github.com/repos/goemon0000/pc-mieru/releases/latest", UpdateRefusal.RedirectDowngrade)]
    [InlineData("https://user:secret@api.github.com/repos/goemon0000/pc-mieru/releases/latest", UpdateRefusal.RedirectCredentials)]
    // M11 (review round): spellings that look like the host and are not it.
    [InlineData("https://api.github.com./repos/goemon0000/pc-mieru/releases/latest", UpdateRefusal.RedirectHostNotAllowed)] // a trailing dot is another name to the allow list
    [InlineData("https://аpi.github.com/repos/goemon0000/pc-mieru/releases/latest", UpdateRefusal.RedirectHostNotAllowed)] // a Cyrillic "a"
    [InlineData("https://xn--pi-ghb.github.com/repos/goemon0000/pc-mieru/releases/latest", UpdateRefusal.RedirectHostNotAllowed)] // the punycode of such a name
    [InlineData("//evil.example/x", UpdateRefusal.RedirectHostNotAllowed)] // a protocol-relative address is a host of its own, not a path on the API
    public async Task The_API_may_not_be_sent_anywhere_but_to_its_own_address(string to, UpdateRefusal reason)
    {
        _gh.Redirect(FakeRelease.Api, to);
        _gh.Bytes(to, "{\"tag_name\":\"v9.9.9\"}"u8.ToArray()); // would answer if it were asked
        using var client = Client();

        var ex = await Refused(client.GetLatestReleaseJsonAsync(default));

        Assert.Equal(reason, ex.Reason);
        Assert.Equal(new[] { FakeRelease.Api }, _gh.Urls); // the other address was never requested
    }

    // ---------------------------------------------------------------------------------------------------------------- an asset may be handed to a release host

    // Ported from Follows_the_redirect_to_github_release_assets.
    [Theory]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/abc?sig=x&se=2026")]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset-2e65be/1/abc?X-Amz-Signature=1")]
    [InlineData("https://RELEASE-ASSETS.githubusercontent.com/x")]
    public async Task An_asset_may_be_handed_to_a_release_host(string to)
    {
        _gh.Redirect(Sha, to);
        _gh.Bytes(to, Body);
        using var client = Client();

        Assert.Equal(Body, await GetAsset(client));
        Assert.Equal(2, _gh.Count);
    }

    // Ported from Never_follows_a_redirect_off_the_allow_list and Never_follows_a_downgrade_to_http.
    [Theory]
    [InlineData("https://evil.example/SHA256.txt", UpdateRefusal.RedirectHostNotAllowed)]
    [InlineData("https://githubusercontent.com/x", UpdateRefusal.RedirectHostNotAllowed)]
    [InlineData("https://raw.githubusercontent.com/goemon0000/pc-mieru/main/SHA256.txt", UpdateRefusal.RedirectHostNotAllowed)]
    [InlineData("https://release-assets.githubusercontent.com.evil.example/x", UpdateRefusal.RedirectHostNotAllowed)]
    [InlineData("https://evilrelease-assets.githubusercontent.com/x", UpdateRefusal.RedirectHostNotAllowed)]
    [InlineData("https://github.com/someone/else/releases/download/v0.5.0/SHA256.txt", UpdateRefusal.RedirectHostNotAllowed)]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/download/v0.4.9/SHA256.txt", UpdateRefusal.RedirectHostNotAllowed)]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/SHA256.txt?x=1", UpdateRefusal.RedirectHostNotAllowed)]
    [InlineData("https://api.github.com/repos/goemon0000/pc-mieru/releases/latest", UpdateRefusal.RedirectHostNotAllowed)]
    [InlineData("https://release-assets.githubusercontent.com:8443/x", UpdateRefusal.RedirectHostNotAllowed)]
    [InlineData("http://release-assets.githubusercontent.com/x", UpdateRefusal.RedirectDowngrade)]
    [InlineData("http://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/SHA256.txt", UpdateRefusal.RedirectDowngrade)]
    [InlineData("https://user:secret@release-assets.githubusercontent.com/x", UpdateRefusal.RedirectCredentials)]
    [InlineData("https://token@objects.githubusercontent.com/x", UpdateRefusal.RedirectCredentials)]
    // M11 (review round): spellings that look like an allowed host and are not it.
    [InlineData("https://release-assets.githubusercontent.com./x", UpdateRefusal.RedirectHostNotAllowed)] // a trailing dot
    [InlineData("https://github.com./goemon0000/pc-mieru/releases/download/v0.5.0/SHA256.txt", UpdateRefusal.RedirectHostNotAllowed)]
    [InlineData("https://release-assets.githubusercontent.cоm/x", UpdateRefusal.RedirectHostNotAllowed)] // a Cyrillic "o" in "com"
    [InlineData("https://xn--elease-assets-d3a.githubusercontent.com/x", UpdateRefusal.RedirectHostNotAllowed)] // punycode of a look-alike label
    [InlineData("//evil.example/x", UpdateRefusal.RedirectHostNotAllowed)] // a protocol-relative address
    public async Task An_asset_may_not_be_handed_to_any_other_address_and_the_other_address_is_never_asked(string to, UpdateRefusal reason)
    {
        _gh.Redirect(Sha, to);
        _gh.Bytes(to, Body);
        using var client = Client();

        var ex = await Refused(GetAsset(client));

        Assert.Equal(reason, ex.Reason);
        Assert.Equal(new[] { Sha }, _gh.Urls);
    }

    [Fact]
    public async Task A_second_hop_is_judged_as_closely_as_the_first()
    {
        _gh.Redirect(Sha, Hop(1));
        _gh.Redirect(Hop(1), "https://evil.example/second");
        _gh.Bytes("https://evil.example/second", Body);
        using var client = Client();

        var ex = await Refused(GetAsset(client));

        Assert.Equal(UpdateRefusal.RedirectHostNotAllowed, ex.Reason);
        Assert.Equal(new[] { Sha, Hop(1) }, _gh.Urls);
    }

    [Fact]
    public async Task A_redirect_without_a_place_to_go_is_refused()
    {
        _gh.Status(Sha, HttpStatusCode.Found);
        using var client = Client();

        Assert.Equal(UpdateRefusal.RedirectMissingLocation, (await Refused(GetAsset(client))).Reason);
    }

    [Fact]
    public async Task A_relative_redirect_of_an_asset_is_resolved_against_the_asset_and_has_to_pass_too()
    {
        _gh.Redirect(Sha, "/goemon0000/pc-mieru/releases/download/v0.4.9/SHA256.txt");
        using var client = Client();

        var ex = await Refused(GetAsset(client));

        Assert.Equal(UpdateRefusal.RedirectHostNotAllowed, ex.Reason);
        Assert.Equal(1, _gh.Count);
    }

    // ---------------------------------------------------------------------------------------------------------------- the number of hops

    [Fact]
    public async Task Five_redirects_are_followed()
    {
        Chain(UpdateHttp.MaxRedirects);
        using var client = Client();

        Assert.Equal(Body, await GetAsset(client));
        Assert.Equal(UpdateHttp.MaxRedirects + 1, _gh.Count);
    }

    [Fact]
    public async Task A_sixth_redirect_is_refused_and_its_address_is_not_asked()
    {
        Chain(UpdateHttp.MaxRedirects + 1);
        using var client = Client();

        var ex = await Refused(GetAsset(client));

        Assert.Equal(UpdateRefusal.RedirectTooMany, ex.Reason);
        Assert.Equal(UpdateHttp.MaxRedirects + 1, _gh.Count);
        Assert.DoesNotContain(Hop(UpdateHttp.MaxRedirects + 1), _gh.Urls);
    }

    // Ported from Redirect_loops_stop.
    [Fact]
    public async Task A_loop_between_allowed_addresses_stops_at_the_limit()
    {
        _gh.Redirect(Sha, Hop(1));
        _gh.Redirect(Hop(1), Hop(1));
        using var client = Client();

        Assert.Equal(UpdateRefusal.RedirectTooMany, (await Refused(GetAsset(client))).Reason);
        Assert.Equal(UpdateHttp.MaxRedirects + 1, _gh.Count);
    }

    [Fact]
    public async Task A_loop_of_the_API_stops_at_the_limit()
    {
        _gh.Redirect(FakeRelease.Api, FakeRelease.Api);
        using var client = Client();

        Assert.Equal(UpdateRefusal.RedirectTooMany, (await Refused(client.GetLatestReleaseJsonAsync(default))).Reason);
        Assert.Equal(UpdateHttp.MaxRedirects + 1, _gh.Count);
    }

    // ---------------------------------------------------------------------------------------------------------------- the zip is never asked for, and only this repo's address first

    [Theory]
    [InlineData(ZipAddress)]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/PCMieru-0.5.0-win-x64.ZIP")]
    [InlineData("https://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/anything.Zip?download=1")]
    [InlineData("https://release-assets.githubusercontent.com/x/PCMieru.zip")]
    public async Task A_zip_address_is_refused_before_anything_is_sent(string url)
    {
        _gh.Bytes(url, Body);
        var requests = FeedTraffic.Requests;
        using var client = Client();

        await Assert.ThrowsAsync<InvalidOperationException>(() => GetAsset(client, url));

        Assert.Equal(0, _gh.Count);
        Assert.Equal(requests, FeedTraffic.Requests);
    }

    [Fact]
    public async Task A_download_of_a_small_asset_asks_for_the_asset_and_its_hops_and_never_for_a_zip()
    {
        // The guard is on the address the caller gives; a hop is judged by its host. "Never a zip" over a whole check is asserted in FeedServiceTests.
        _gh.Redirect(Sha, Hop(1));
        _gh.Bytes(Hop(1), Body);
        using var client = Client();

        await GetAsset(client);

        Assert.DoesNotContain(_gh.Seen, s => s.IsZip);
    }

    [Theory]
    [InlineData("https://evil.example/SHA256.txt")]
    [InlineData("https://release-assets.githubusercontent.com/SHA256.txt")]
    [InlineData("https://github.com/someone/else/releases/download/v0.5.0/SHA256.txt")]
    [InlineData("https://github.com/goemon0000/pc-mieru/raw/main/SHA256.txt")]
    [InlineData("https://api.github.com/repos/goemon0000/pc-mieru/releases/latest")]
    public async Task An_address_that_is_not_this_repos_download_address_is_not_asked_even_at_the_first_request(string url)
    {
        _gh.Bytes(url, Body);
        using var client = Client();

        var ex = await Refused(GetAsset(client, url));

        Assert.Equal(UpdateRefusal.RedirectHostNotAllowed, ex.Reason);
        Assert.Equal(0, _gh.Count);
    }

    [Theory]
    [InlineData("http://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/SHA256.txt", UpdateRefusal.RedirectDowngrade)]
    [InlineData("https://user:pw@github.com/goemon0000/pc-mieru/releases/download/v0.5.0/SHA256.txt", UpdateRefusal.RedirectCredentials)]
    [InlineData("https://github.com:8443/goemon0000/pc-mieru/releases/download/v0.5.0/SHA256.txt", UpdateRefusal.RedirectHostNotAllowed)]
    public async Task A_first_address_over_http_with_a_user_name_or_on_another_port_is_not_asked(string url, UpdateRefusal reason)
    {
        using var client = Client();

        Assert.Equal(reason, (await Refused(GetAsset(client, url))).Reason);
        Assert.Equal(0, _gh.Count);
    }

    [Fact]
    public async Task A_size_the_release_gives_over_the_limit_is_refused_before_any_request()
    {
        using var client = Client();

        var ex = await Refused(GetAsset(client, declared: 4097, max: 4096));

        Assert.Equal(UpdateRefusal.AssetTooLarge, ex.Reason);
        Assert.Equal(0, _gh.Count);
    }

    // ---------------------------------------------------------------------------------------------------------------- the size is counted on what arrives

    // Ported from Body_longer_than_the_declared_size_is_cut.
    [Theory]
    [InlineData(Sha256List.MaxBytes)]
    [InlineData(1024)]
    public async Task A_body_of_exactly_the_cap_is_accepted_with_and_without_a_length(int cap)
    {
        _gh.Bytes(Sha, new byte[cap]);
        using var client = Client();
        Assert.Equal(cap, (await GetAsset(client, max: cap)).Length);

        _gh.BytesWithoutLength(Sha, new byte[cap]);
        Assert.Equal(cap, (await GetAsset(client, max: cap)).Length);

        _gh.BytesWithDeclaredLength(Sha, new byte[cap], declared: 10); // a length smaller than the truth, but the bytes are within the cap
        Assert.Equal(cap, (await GetAsset(client, max: cap)).Length);
    }

    [Theory]
    [InlineData(Sha256List.MaxBytes)]
    [InlineData(1024)]
    public async Task A_body_one_byte_over_the_cap_is_refused_whatever_it_says_about_its_length(int cap)
    {
        using var client = Client();

        _gh.Bytes(Sha, new byte[cap + 1]); // the length is given and over
        Assert.Equal(UpdateRefusal.BodyTooLarge, (await Refused(GetAsset(client, max: cap))).Reason);

        _gh.BytesWithoutLength(Sha, new byte[cap + 1]); // no length at all
        Assert.Equal(UpdateRefusal.BodyTooLarge, (await Refused(GetAsset(client, max: cap))).Reason);

        _gh.BytesWithDeclaredLength(Sha, new byte[cap + 1], declared: 10); // a length that lies
        Assert.Equal(UpdateRefusal.BodyTooLarge, (await Refused(GetAsset(client, max: cap))).Reason);
    }

    [Fact]
    public async Task The_manifest_cap_is_the_limit_of_the_bundle_format()
    {
        var cap = Limits.Default.ManifestBytes;
        _gh.Bytes(Sha, new byte[cap]);
        using var client = Client();
        Assert.Equal(cap, (await GetAsset(client, max: cap)).Length);

        _gh.BytesWithoutLength(Sha, new byte[cap + 1]);
        Assert.Equal(UpdateRefusal.BodyTooLarge, (await Refused(GetAsset(client, max: cap))).Reason);
    }

    [Fact]
    public async Task The_API_answer_is_capped_at_one_MiB_on_the_bytes_that_arrive()
    {
        using var client = Client();

        _gh.BytesWithoutLength(FakeRelease.Api, new byte[UpdateHttp.MaxJsonBytes]);
        Assert.Equal(UpdateHttp.MaxJsonBytes, (await client.GetLatestReleaseJsonAsync(default)).Length);

        _gh.BytesWithoutLength(FakeRelease.Api, new byte[UpdateHttp.MaxJsonBytes + 1]);
        Assert.Equal(UpdateRefusal.BodyTooLarge, (await Refused(client.GetLatestReleaseJsonAsync(default))).Reason);

        _gh.BytesWithDeclaredLength(FakeRelease.Api, new byte[UpdateHttp.MaxJsonBytes + 1], declared: 10);
        Assert.Equal(UpdateRefusal.BodyTooLarge, (await Refused(client.GetLatestReleaseJsonAsync(default))).Reason);

        _gh.Bytes(FakeRelease.Api, new byte[UpdateHttp.MaxJsonBytes + 1]);
        Assert.Equal(UpdateRefusal.BodyTooLarge, (await Refused(client.GetLatestReleaseJsonAsync(default))).Reason);
    }

    // ---------------------------------------------------------------------------------------------------------------- statuses

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.MultipleChoices)]
    [InlineData(HttpStatusCode.NotModified)]
    public async Task Any_other_status_is_an_error_not_a_redirect_and_not_a_rate_limit(HttpStatusCode code)
    {
        _gh.Status(FakeRelease.Api, code);
        _gh.Status(Sha, code);
        using var client = Client();

        Assert.Equal(UpdateRefusal.HttpError, (await Refused(client.GetLatestReleaseJsonAsync(default))).Reason);
        Assert.Equal(UpdateRefusal.HttpError, (await Refused(GetAsset(client))).Reason);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_rate_limit_carries_nothing_when_the_server_asks_for_nothing(HttpStatusCode code)
    {
        _gh.Status(FakeRelease.Api, code);
        _gh.Status(Sha, code);
        using var client = Client();

        var onApi = await Assert.ThrowsAsync<RateLimitedException>(() => client.GetLatestReleaseJsonAsync(default));
        var onAsset = await Assert.ThrowsAsync<RateLimitedException>(() => GetAsset(client));

        Assert.All(new[] { onApi, onAsset }, ex =>
        {
            Assert.Null(ex.RetryAfter);
            Assert.Null(ex.Reset);
        });
    }

    [Fact]
    public async Task Retry_After_is_read_as_a_delay_and_as_a_date()
    {
        _gh.Status(FakeRelease.Api, HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90)));
        using var client = Client();
        var delay = await Assert.ThrowsAsync<RateLimitedException>(() => client.GetLatestReleaseJsonAsync(default));
        Assert.Equal(TimeSpan.FromSeconds(90), delay.RetryAfter);

        _gh.Status(FakeRelease.Api, HttpStatusCode.Forbidden, r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddHours(3)));
        var date = await Assert.ThrowsAsync<RateLimitedException>(() => client.GetLatestReleaseJsonAsync(default));
        Assert.InRange(date.RetryAfter!.Value, TimeSpan.FromHours(3) - TimeSpan.FromMinutes(5), TimeSpan.FromHours(3));
    }

    [Theory]
    [InlineData("0", "1795000000", true)]       // none left: the reset time is the one to wait for
    [InlineData("5", "1795000000", false)]      // some left: a 403 for another reason, the reset time says nothing
    [InlineData(null, "1795000000", false)]
    [InlineData("0", null, false)]
    [InlineData("0", "abc", false)]
    [InlineData("0", "-5", false)]
    [InlineData("0", "0", false)]
    [InlineData("0", "99999999999999", false)]
    [InlineData("0", "1.5e9", false)]
    public async Task The_reset_time_is_read_only_when_no_requests_remain_and_only_when_it_is_a_sane_number(string? remaining, string? reset, bool read)
    {
        _gh.Status(FakeRelease.Api, HttpStatusCode.Forbidden, r =>
        {
            if (remaining is not null)
            {
                r.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", remaining);
            }

            if (reset is not null)
            {
                r.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset);
            }
        });
        using var client = Client();

        var ex = await Assert.ThrowsAsync<RateLimitedException>(() => client.GetLatestReleaseJsonAsync(default));

        Assert.Equal(read ? DateTimeOffset.FromUnixTimeSeconds(1795000000) : null, ex.Reset);
    }

    // ---------------------------------------------------------------------------------------------------------------- failures of the connection

    [Fact]
    public async Task A_handler_that_throws_is_a_network_failure_not_an_exception_of_the_library()
    {
        _gh.Handle(FakeRelease.Api, _ => throw new HttpRequestException("no route to host"));
        _gh.Handle(Sha, _ => throw new HttpRequestException("connection reset"));
        using var client = Client();

        Assert.Equal(UpdateRefusal.NetworkFailure, (await Refused(client.GetLatestReleaseJsonAsync(default))).Reason);
        Assert.Equal(UpdateRefusal.NetworkFailure, (await Refused(GetAsset(client))).Reason);
    }

    [Fact]
    public async Task A_body_that_breaks_half_way_is_a_network_failure()
    {
        _gh.Respond(Sha, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) });
        using var client = Client();

        Assert.Equal(UpdateRefusal.NetworkFailure, (await Refused(GetAsset(client))).Reason);
    }

    [Fact]
    public async Task No_answer_in_time_is_a_network_failure_for_the_API_and_for_an_asset()
    {
        // The route never answers; only the client's own timeout can end the request (deterministic: without it the test would hang, not flake).
        _gh.Handle(FakeRelease.Api, async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        _gh.Handle(Sha, async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = Client(api: TimeSpan.FromMilliseconds(30), asset: TimeSpan.FromMilliseconds(30));

        var onApi = await Refused(client.GetLatestReleaseJsonAsync(default));
        var onAsset = await Refused(GetAsset(client));

        Assert.Equal(UpdateRefusal.NetworkFailure, onApi.Reason);
        Assert.Equal(UpdateRefusal.NetworkFailure, onAsset.Reason);
    }

    [Fact]
    public async Task The_callers_cancellation_is_not_turned_into_a_failure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gh.Handle(FakeRelease.Api, async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = Client();
        using var cts = new CancellationTokenSource();

        var task = client.GetLatestReleaseJsonAsync(cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task A_token_cancelled_before_the_call_throws_the_cancellation()
    {
        _gh.Bytes(Sha, Body);
        using var client = Client();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GetAsset(client, ct: cts.Token));
    }

    /// <summary>A body that fails on the first read, like a connection reset in the middle of an answer.</summary>
    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset by peer");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
