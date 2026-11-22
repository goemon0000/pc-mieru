// Provenance: ported from the owner's pl-cloud src/PartsLife.Update/UpdateEndpoints.cs (Parts Life, MIT, same owner; see NOTICE.txt).
// What is new here: this repo (goemon0000/pc-mieru); the API may redirect to nothing but its own URL; a download hop is the asset's own URL or a release-asset host;
// the loopback test feed is gone (tests give the client a fake handler instead); the comments are in English.
namespace PcMieru.Update.Feed;

/// <summary>
/// The only addresses the update feed talks to (DESIGN-B 3.1): the GitHub API for the latest release of this repo, the Release page of a tag, and the
/// download address of an asset of that tag (with the two hosts GitHub hands real downloads to). Everything is HTTPS on port 443, with no user name.
/// </summary>
public static class UpdateEndpoints
{
    public const string Owner = "goemon0000";
    public const string Repo = "pc-mieru";

    /// <summary>The one API request of a check.</summary>
    public static Uri LatestRelease { get; } = new($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");

    /// <summary>The start of every asset address: ".../releases/download/vX.Y.Z/name".</summary>
    public const string DownloadPrefix = $"https://github.com/{Owner}/{Repo}/releases/download/";

    /// <summary>The start of every Release page address: ".../releases/tag/vX.Y.Z".</summary>
    public const string ReleasePagePrefix = $"https://github.com/{Owner}/{Repo}/releases/tag/";

    /// <summary>The page that lists all releases (the fallback for "open the release notes").</summary>
    public const string ReleasesPage = $"https://github.com/{Owner}/{Repo}/releases";

    /// <summary>The hosts a download may be handed to. GitHub redirects "github.com/.../download/..." to one of them.</summary>
    public static IReadOnlyList<string> RedirectHosts { get; } = ["release-assets.githubusercontent.com", "objects.githubusercontent.com"];

    /// <summary>An absolute https address on the default port with no user name or password.</summary>
    public static bool IsPlain(Uri? u) =>
        u is { IsAbsoluteUri: true } && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && u.UserInfo.Length == 0;

    public static bool SameHost(Uri a, Uri b) => string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>The API request, and every hop it may be redirected through: this one address (query and fragment included) and nothing else.</summary>
    public static bool IsAllowedApi(Uri? u) =>
        IsPlain(u) && string.Equals(u!.AbsoluteUri, LatestRelease.AbsoluteUri, StringComparison.Ordinal);

    /// <summary>"https://github.com/goemon0000/pc-mieru/releases/download/vX.Y.Z/name". The tag and the name are escaped as one path segment each.</summary>
    public static Uri AssetUrl(string tag, string name) =>
        new(DownloadPrefix + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name));

    /// <summary>
    /// A hop of an asset download: the asset's own address (the first request), or one of <see cref="RedirectHosts"/> (where GitHub sends the real
    /// download). Never another repo, another tag, another host, a downgrade to http, another port or an address with a user name.
    /// </summary>
    public static bool IsAllowedDownloadHop(Uri? u, Uri asset)
    {
        if (!IsPlain(u))
        {
            return false;
        }

        if (string.Equals(u!.AbsoluteUri, asset.AbsoluteUri, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var host in RedirectHosts)
        {
            if (string.Equals(u.IdnHost, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The Release page of a tag, as GitHub's "html_url" must spell it.</summary>
    public static string ReleasePageUrl(string tag) => ReleasePagePrefix + Uri.EscapeDataString(tag);

    /// <summary>
    /// A Release page of this repo: the prefix and one path segment, nothing after (no query, no fragment). The GUI checks this again before it opens an
    /// address it read from state.json, which the user can edit.
    /// </summary>
    public static bool IsReleasePage(string? url)
    {
        if (url is null || !url.StartsWith(ReleasePagePrefix, StringComparison.Ordinal) || url.Length == ReleasePagePrefix.Length)
        {
            return false;
        }

        var rest = url[ReleasePagePrefix.Length..];
        if (rest.IndexOfAny(['/', '?', '#', '\\', ' ']) >= 0)
        {
            return false;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var u) && IsPlain(u)
            && string.Equals(u.IdnHost, "github.com", StringComparison.OrdinalIgnoreCase);
    }
}
