// Provenance: the idea (a fake GitHub that remembers every request and answers by address, and a fake release that is built from a table of files with knobs to
// break it) is that of FakeGitHub and FakeRelease in the owner's pl-cloud tests/PartsLife.Update.Tests/UpdaterTests.cs (Parts Life, MIT, same owner; see NOTICE.txt).
// The code is new for this repo's three files (zip, outer manifest, SHA256.txt) and the refusals of DESIGN-B 11. **Nothing here talks to a network**: the handler
// answers from memory, and every test that reads the process-wide counters of FeedTraffic lives in the one "feed" collection, so that no other test's client
// moves them while it counts.
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PcMieru.Update.Bundle;
using PcMieru.Update.Feed;

namespace PcMieru.Update.Tests;

/// <summary>The tests that make an <see cref="UpdateHttp"/> (the counters of <see cref="FeedTraffic"/> are the process's, so these run one after the other).</summary>
[CollectionDefinition(Name)]
public sealed class FeedCollection
{
    public const string Name = "feed";
}

/// <summary>What the fake saw of one request. Taken when it arrived: the client disposes its request message afterwards.</summary>
internal sealed record SeenRequest(string Url, string Method, string[] HeaderNames, string UserAgent, string Accept, bool HadBody)
{
    public bool IsZip => Url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A stream with no length and no seeking, so that the content has no Content-Length (a server that does not say how long the body is).</summary>
internal sealed class NoLengthStream(byte[] data) : Stream
{
    private readonly MemoryStream _inner = new(data);

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

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A fake GitHub: a handler that remembers every request and answers by address. A route is made fresh for each request that reaches it.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<SeenRequest> _seen = [];

    public Dictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>> Routes { get; } = new(StringComparer.Ordinal);

    /// <summary>Called when a request arrives, before it is answered (a test looks at the world at that moment).</summary>
    public Action<SeenRequest>? OnRequest { get; set; }

    public IReadOnlyList<SeenRequest> Seen
    {
        get
        {
            lock (_gate)
            {
                return [.. _seen];
            }
        }
    }

    public int Count => Seen.Count;

    public string[] Urls => [.. Seen.Select(s => s.Url)];

    public bool AskedForAZip => Seen.Any(s => s.IsZip);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var seen = new SeenRequest(
            request.RequestUri!.AbsoluteUri,
            request.Method.Method,
            [.. request.Headers.Select(h => h.Key).Order(StringComparer.Ordinal)],
            request.Headers.UserAgent.ToString(),
            request.Headers.Accept.ToString(),
            request.Content is not null);
        lock (_gate)
        {
            _seen.Add(seen);
        }

        OnRequest?.Invoke(seen);
        var response = Routes.TryGetValue(seen.Url, out var make)
            ? await make(cancellationToken).ConfigureAwait(false)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        response.RequestMessage = request;
        return response;
    }

    /// <summary>One fake is shared by the several checks of a test; the client of each check disposes its handler, which must not break the next one.</summary>
    protected override void Dispose(bool disposing)
    {
    }

    /// <summary>The request arrives as Uri.AbsoluteUri (host in lower case), so a route written with another spelling of the same address is keyed the same way.</summary>
    private static string Key(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : url;

    public void Handle(string url, Func<CancellationToken, Task<HttpResponseMessage>> make) => Routes[Key(url)] = make;

    public void Respond(string url, Func<HttpResponseMessage> make) => Routes[Key(url)] = _ => Task.FromResult(make());

    public void Bytes(string url, byte[] body) => Respond(url, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });

    /// <summary>A body that carries no Content-Length.</summary>
    public void BytesWithoutLength(string url, byte[] body) =>
        Respond(url, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NoLengthStream(body)) });

    /// <summary>A body that says it is <paramref name="declared"/> bytes long and is <c>body.Length</c>.</summary>
    public void BytesWithDeclaredLength(string url, byte[] body, long declared) => Respond(url, () =>
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentLength = declared;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    });

    public void Redirect(string url, string to, HttpStatusCode code = HttpStatusCode.Found) => Respond(url, () =>
    {
        var response = new HttpResponseMessage(code);
        response.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return response;
    });

    public void Status(string url, HttpStatusCode code, Action<HttpResponseMessage>? decorate = null) => Respond(url, () =>
    {
        var response = new HttpResponseMessage(code);
        decorate?.Invoke(response);
        return response;
    });
}

/// <summary>One file of a fake release as the API lists it.</summary>
internal sealed record FakeAsset(string Name, long Size, string State, string Url, string? Digest);

/// <summary>
/// A release of this repo in memory: a real small zip, its outer manifest, SHA256.txt and the API's JSON. A test changes one thing (a field, or the list of
/// assets through <see cref="EditAssets"/>) and says which rule must hold.
/// </summary>
internal sealed class FakeRelease
{
    public const string Api = "https://api.github.com/repos/goemon0000/pc-mieru/releases/latest";

    public FakeRelease(string version = "0.5.0")
    {
        Version = version;
        Tag = "v" + version;
        Bundle = new TestBundle(version);
        ZipBytes = Bundle.Zip();
        ManifestBytes = Bundle.Outer().ToUtf8Bytes();
    }

    public string Version { get; }

    public TestBundle Bundle { get; }

    public string? Tag { get; set; }

    public bool Draft { get; set; }

    public bool Prerelease { get; set; }

    /// <summary>The Release page the API names; null: this repo's page of the tag.</summary>
    public string? HtmlUrl { get; set; }

    public byte[] ZipBytes { get; set; }

    public byte[] ManifestBytes { get; set; }

    /// <summary>SHA256.txt as text; null: the two correct lines.</summary>
    public string? ShaText { get; set; }

    /// <summary>When false the API gives no "digest" for the assets (the old API did not).</summary>
    public bool WithDigests { get; set; } = true;

    /// <summary>A last change to the list of assets, after it was made from the fields above.</summary>
    public Action<List<FakeAsset>>? EditAssets { get; set; }

    public string ZipName => BundleFormat.ZipName(Version);

    public string ManifestName => BundleFormat.OuterManifestName(Version);

    public static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public string ShaFile() => ShaText ?? $"{Hex(ZipBytes)}  {ZipName}\n{Hex(ManifestBytes)}  {ManifestName}\n";

    public byte[] ShaBytes() => Encoding.UTF8.GetBytes(ShaFile());

    public string AssetUrl(string name) => UpdateEndpoints.AssetUrl(Tag ?? "v" + Version, name).AbsoluteUri;

    public List<FakeAsset> Assets()
    {
        FakeAsset Make(string name, byte[] body) =>
            new(name, body.Length, "uploaded", AssetUrl(name), WithDigests ? "sha256:" + Hex(body) : null);

        var assets = new List<FakeAsset> { Make(ZipName, ZipBytes), Make(ManifestName, ManifestBytes), Make(BundleFormat.HashListName, ShaBytes()) };
        EditAssets?.Invoke(assets);
        return assets;
    }

    public string Json()
    {
        var assets = new JsonArray();
        foreach (var a in Assets())
        {
            assets.Add(new JsonObject
            {
                ["name"] = a.Name,
                ["size"] = a.Size,
                ["state"] = a.State,
                ["browser_download_url"] = a.Url,
                ["digest"] = a.Digest,
            });
        }

        return new JsonObject
        {
            ["tag_name"] = Tag,
            ["html_url"] = HtmlUrl ?? (Tag is null ? null : UpdateEndpoints.ReleasePageUrl(Tag)),
            ["draft"] = Draft,
            ["prerelease"] = Prerelease,
            ["assets"] = assets,
        }.ToJsonString();
    }

    /// <summary>Answers the API, SHA256.txt, the manifest and the zip (a test that must never see the zip asked for checks the list of requests).</summary>
    public FakeRelease PublishTo(FakeGitHub gh)
    {
        gh.Bytes(Api, Encoding.UTF8.GetBytes(Json()));
        gh.Bytes(AssetUrl(BundleFormat.HashListName), ShaBytes());
        gh.Bytes(AssetUrl(ManifestName), ManifestBytes);
        gh.Bytes(AssetUrl(ZipName), ZipBytes);
        return this;
    }
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>A temporary folder that is removed after the test.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcmieru-update-tests-feed-" + Guid.NewGuid().ToString("N"));

    public string Path { get; }

    public string Sub(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a locked file in a temp folder is not the test's failure
        }
    }
}

/// <summary>
/// A service wired to a fake GitHub, a fake clock and a state folder of its own. <see cref="Running"/> is 0.4.0 and the default release is 0.5.0, so by
/// default there is something newer to find.
/// </summary>
internal sealed class FeedRig : IDisposable
{
    private readonly TempFolder _temp = new();

    public FeedRig()
    {
        Clock = new FakeClock(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
        StateFolder = _temp.Sub("update-state");
        Store = new UpdateStateStore(StateFolder);
    }

    public FakeGitHub Gh { get; } = new();

    public FakeClock Clock { get; }

    public string StateFolder { get; }

    public UpdateStateStore Store { get; }

    public AppVersion Running { get; set; } = new(0, 4, 0);

    public AppVersion? Installed { get; set; }

    public string[] Blocked { get; set; } = [];

    /// <summary>How many transports (HTTP clients) the service made.</summary>
    public int ClientsMade { get; private set; }

    public string Temp => _temp.Path;

    public UpdateService Service() => new(new UpdateServiceOptions
    {
        Running = Running,
        Installed = Installed,
        StateFolder = StateFolder,
        BlockedTags = Blocked,
        Clock = Clock,
        TransportFactory = () =>
        {
            ClientsMade++;
            return new UpdateHttp(Running.ToString(), Gh);
        },
    });

    public Task<UpdateCheck> Check(UpdateMode mode = UpdateMode.Notify, bool manual = false, CancellationToken ct = default) =>
        Service().CheckAsync(mode, manual, ct);

    public UpdateState State() => Store.Read();

    public FakeRelease Release(string version = "0.5.0", Action<FakeRelease>? edit = null)
    {
        var release = new FakeRelease(version);
        edit?.Invoke(release);
        return release.PublishTo(Gh);
    }

    public void Dispose() => _temp.Dispose();
}
