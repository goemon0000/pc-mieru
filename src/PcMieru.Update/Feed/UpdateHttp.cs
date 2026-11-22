// Provenance: ported from the owner's pl-cloud src/PartsLife.Update/UpdateHttp.cs (Parts Life, MIT, same owner; see NOTICE.txt).
// What is new here: this file is the only one of the repo that names System.Net.Http (NoNetworkTests keeps it so); redirects are refused with their own reasons
// (another host, a downgrade, a user name, too many); a zip address is refused before anything is sent; every failure of the library is turned into an
// UpdateHttpException so that the caller never sees an HTTP type; the comments are in English.
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace PcMieru.Update.Feed;

/// <summary>
/// The HTTP client of the feed (DESIGN-B 3.1): GET only, to the addresses of <see cref="UpdateEndpoints"/> only. Redirects are followed by hand, at most
/// <see cref="MaxRedirects"/>, each hop checked against the list before it is requested. No cookie, no credential, nothing but User-Agent and Accept goes
/// out. Every body is read with a limit and the limit is counted on the bytes that arrive, not on what the server says.
/// </summary>
public sealed class UpdateHttp : IFeedTransport
{
    /// <summary>The API's JSON (a release with a few assets is a few KB; the old client's cap).</summary>
    public const int MaxJsonBytes = 1024 * 1024;

    /// <summary>The most redirects followed for one request.</summary>
    public const int MaxRedirects = 5;

    /// <summary>How long the API request may take, all hops together.</summary>
    public static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long one small asset may take, all hops together.</summary>
    public static readonly TimeSpan AssetTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _client;
    private readonly string _userAgent;
    private readonly TimeSpan _apiTimeout;
    private readonly TimeSpan _assetTimeout;

    /// <param name="version">The running version ("0.5.0"): the User-Agent is "PCMieru/0.5.0".</param>
    /// <param name="handler">A handler of the caller's own (a test's fake); null makes <see cref="CreateDefaultHandler"/>. It is disposed with this object.</param>
    public UpdateHttp(string version, HttpMessageHandler? handler = null, TimeSpan? apiTimeout = null, TimeSpan? assetTimeout = null)
    {
        // The timeouts are per request, kept by this class (a linked token); the client's own timeout is off so that a body being read is not cut by it.
        _client = new HttpClient(handler ?? CreateDefaultHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _userAgent = "PCMieru/" + version;
        _apiTimeout = apiTimeout ?? ApiTimeout;
        _assetTimeout = assetTimeout ?? AssetTimeout;
        FeedTraffic.CountClient();
    }

    /// <summary>
    /// The handler of a real run. No redirect by itself (the hops are checked here), no cookie jar, no credential, no proxy login, and **no automatic
    /// decompression**: AutomaticDecompression stays None, so Brotli is never asked for and never decoded. Directory.Build.props suppresses the audit advisory
    /// GHSA-p3jp-9v83-76w9 (the Brotli decoder of the 10.0.9 runtime) on exactly that promise; a test of the repo reads this line and the props.
    /// </summary>
    public static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        PreAuthenticate = false,
        Credentials = null,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    public async Task<string> GetLatestReleaseJsonAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_apiTimeout);
        try
        {
            var url = UpdateEndpoints.LatestRelease;
            using var response = await SendFollowingAsync(url, "application/vnd.github+json", u => UpdateEndpoints.IsAllowedApi(u), cts.Token).ConfigureAwait(false);
            var bytes = await ReadCappedAsync(response, MaxJsonBytes, cts.Token).ConfigureAwait(false);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (Translate(ex, ct) is { } translated)
        {
            throw translated;
        }
    }

    public async Task<byte[]> GetSmallAssetAsync(Uri url, long declaredSize, int maxBytes, CancellationToken ct)
    {
        // The zip is the one file this client must never ask for (a later step downloads it, with a client of its own rules).
        if (url.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("the feed client does not download a zip");
        }

        // The first address is this repo's download address too (the caller builds it from the tag and the name, but this class does not trust that):
        // only the hops after it may go to the release-asset hosts.
        if (!UpdateEndpoints.IsPlain(url) || !url.AbsoluteUri.StartsWith(UpdateEndpoints.DownloadPrefix, StringComparison.Ordinal))
        {
            throw Refusal(url);
        }

        if (declaredSize > maxBytes)
        {
            throw new UpdateHttpException(UpdateRefusal.AssetTooLarge, $"the release says the file is {declaredSize} bytes, over {maxBytes}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_assetTimeout);
        try
        {
            using var response = await SendFollowingAsync(url, "application/octet-stream", u => UpdateEndpoints.IsAllowedDownloadHop(u, url), cts.Token).ConfigureAwait(false);
            return await ReadCappedAsync(response, maxBytes, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (Translate(ex, ct) is { } translated)
        {
            throw translated;
        }
    }

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// GET with the redirects followed by hand. Each address, the first and every hop, must pass <paramref name="allowed"/> before it is requested.
    /// The request carries User-Agent and Accept and nothing else.
    /// </summary>
    private async Task<HttpResponseMessage> SendFollowingAsync(Uri url, string accept, Func<Uri, bool> allowed, CancellationToken ct)
    {
        for (var hop = 0; ; hop++)
        {
            if (!allowed(url))
            {
                throw Refusal(url);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(_userAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

            FeedTraffic.CountRequest();
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            try
            {
                var code = (int)response.StatusCode;
                if (code is 301 or 302 or 303 or 307 or 308)
                {
                    if (response.Headers.Location is not { } location)
                    {
                        throw new UpdateHttpException(UpdateRefusal.RedirectMissingLocation, "a redirect without a Location");
                    }

                    if (hop >= MaxRedirects)
                    {
                        throw new UpdateHttpException(UpdateRefusal.RedirectTooMany, $"more than {MaxRedirects} redirects");
                    }

                    url = location.IsAbsoluteUri ? location : new Uri(url, location);
                    response.Dispose();
                    continue;
                }

                if (UpdateSchedule.IsRateLimited(code))
                {
                    throw new RateLimitedException(RetryAfterOf(response), ResetOf(response));
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new UpdateHttpException(UpdateRefusal.HttpError, $"HTTP {code}");
                }

                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    /// <summary>The reason a hop to <paramref name="url"/> is refused: which rule it broke.</summary>
    private static UpdateHttpException Refusal(Uri url)
    {
        if (url.IsAbsoluteUri && url.UserInfo.Length > 0)
        {
            return new UpdateHttpException(UpdateRefusal.RedirectCredentials, "an address with a user name or password");
        }

        if (url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttp)
        {
            return new UpdateHttpException(UpdateRefusal.RedirectDowngrade, "a redirect from https to http");
        }

        return new UpdateHttpException(UpdateRefusal.RedirectHostNotAllowed, "an address that is not on the list: " + (url.IsAbsoluteUri ? url.IdnHost : url.OriginalString));
    }

    /// <summary>
    /// Reads the body, at most <paramref name="max"/> bytes. A longer Content-Length is refused before a byte is read, and the bytes that really arrive are counted
    /// (a server that lies about, or leaves out, the length gets no more room).
    /// </summary>
    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, int max, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > max)
        {
            throw new UpdateHttpException(UpdateRefusal.BodyTooLarge, $"the answer says it is {declared} bytes, over {max}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > max)
            {
                throw new UpdateHttpException(UpdateRefusal.BodyTooLarge, $"the answer is longer than {max} bytes");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Retry-After as the library reads it: a delay, or a date minus now.</summary>
    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var after = response.Headers.RetryAfter;
        if (after is null)
        {
            return null;
        }

        if (after.Delta is { } delta)
        {
            return delta;
        }

        return after.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }

    /// <summary>x-ratelimit-reset (Unix seconds), only when x-ratelimit-remaining says 0.</summary>
    private static DateTimeOffset? ResetOf(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) || remaining.FirstOrDefault()?.Trim() != "0")
        {
            return null;
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0 && seconds < 253402300799L)
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return null;
    }

    /// <summary>
    /// Turns what the library throws into what this class promises: an <see cref="UpdateHttpException"/> (or the caller's own cancellation, left alone). Null
    /// means "not mine, let it pass" (our own exceptions, and the caller's cancellation).
    /// </summary>
    private static Exception? Translate(Exception ex, CancellationToken caller)
    {
        switch (ex)
        {
            case UpdateHttpException or RateLimitedException or InvalidOperationException:
                return null;
            case OperationCanceledException when caller.IsCancellationRequested:
                return null;
            case OperationCanceledException:
                return new UpdateHttpException(UpdateRefusal.NetworkFailure, "no answer in time", ex);
            case HttpRequestException or IOException:
                return new UpdateHttpException(UpdateRefusal.NetworkFailure, "no answer: " + ex.Message, ex);
            default:
                return null;
        }
    }
}
