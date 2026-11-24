namespace PcMieru.Update.Feed;

/// <summary>
/// The two requests the feed makes, with nothing of the HTTP library in their signatures (so that only <c>UpdateHttp.cs</c> names System.Net.Http; a test of
/// the repo keeps it so). A transport throws <see cref="UpdateHttpException"/> for a refusal and <see cref="RateLimitedException"/> for a rate limit.
/// </summary>
public interface IFeedTransport : IDisposable
{
    /// <summary>GET the latest-release address of the API. The text of the answer, at most <see cref="UpdateHttp.MaxJsonBytes"/> bytes.</summary>
    Task<string> GetLatestReleaseJsonAsync(CancellationToken ct);

    /// <summary>
    /// GET a small asset (SHA256.txt or the outer manifest). At most <paramref name="maxBytes"/> bytes, counted on the bytes that arrive; a zip address is refused
    /// before anything is sent. <paramref name="declaredSize"/> is what the release says the file weighs: a larger limit than that is refused early.
    /// </summary>
    Task<byte[]> GetSmallAssetAsync(Uri url, long declaredSize, int maxBytes, CancellationToken ct);

    /// <summary>
    /// GET the zip into <paramref name="destination"/> (the stage of DESIGN-B 3.3; only <see cref="IUpdateService.StageAsync"/> calls it, and only in Automatic mode). The same rules as a small
    /// asset for every address (this repo's download address first, hops only to the release-asset hosts, https only, no credential, at most five redirects), and the limit is counted on the
    /// bytes that really arrive. The bytes are hashed as they are written. A failure of <paramref name="destination"/> (a full disk, no right) is <see cref="UpdateRefusal.StageFailed"/>.
    /// </summary>
    Task<AssetDownload> DownloadAssetAsync(Uri url, long declaredSize, long maxBytes, Stream destination, CancellationToken ct);
}

/// <summary>What <see cref="IFeedTransport.DownloadAssetAsync"/> wrote: how many bytes and their SHA-256 (64 lower-case hex digits).</summary>
public readonly record struct AssetDownload(long Bytes, string Sha256);

/// <summary>The feed was refused or could not get an answer, with the reason.</summary>
public sealed class UpdateHttpException : Exception
{
    public UpdateHttpException(UpdateRefusal reason, string message)
        : base(message) => Reason = reason;

    public UpdateHttpException(UpdateRefusal reason, string message, Exception inner)
        : base(message, inner) => Reason = reason;

    public UpdateRefusal Reason { get; }
}

/// <summary>GitHub said 403 or 429. It carries what the server asked for (Retry-After, x-ratelimit-reset) so the schedule can wait that long.</summary>
public sealed class RateLimitedException : Exception
{
    public RateLimitedException(TimeSpan? retryAfter, DateTimeOffset? reset)
        : base("rate limited")
    {
        RetryAfter = retryAfter;
        Reset = reset;
    }

    public TimeSpan? RetryAfter { get; }

    public DateTimeOffset? Reset { get; }
}

/// <summary>
/// How much the feed has sent in this process: a counter a smoke run or a test can read to show that nothing went out (the smoke of the GUI asserts both are
/// zero). A request is counted when it is handed to the handler, whatever the handler is.
/// </summary>
public static class FeedTraffic
{
    private static long _requests;
    private static long _clients;

    /// <summary>The requests handed to a handler since the process started.</summary>
    public static long Requests => Interlocked.Read(ref _requests);

    /// <summary>The HTTP clients created since the process started (a client is made only by the first check, never by an idle app).</summary>
    public static long ClientsCreated => Interlocked.Read(ref _clients);

    internal static void CountRequest() => Interlocked.Increment(ref _requests);

    internal static void CountClient() => Interlocked.Increment(ref _clients);
}
