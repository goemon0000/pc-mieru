// Provenance: ported from the owner's pl-cloud src/PartsLife.Update/ReleaseInfo.cs (Parts Life, MIT, same owner; see NOTICE.txt).
// What is new here: nothing is dropped quietly. The old parser dropped an asset that was not at the right address and the check then said "missing"; this one keeps every
// asset of the answer and says which rule an asset broke (missing, twice, not finished, at another address, too big). The three files are the ones of DESIGN-B 3.1, a
// draft, a pre-release or a tag that is not a plain vX.Y.Z is a refusal with its own reason, and a "sha256:" digest that is not 64 hex digits is a mismatch.
using System.Text.Json;
using System.Text.Json.Serialization;
using PcMieru.Update.Bundle;

namespace PcMieru.Update.Feed;

/// <summary>One file of a release as the API lists it.</summary>
public sealed record ReleaseAsset(string Name, long Size, string State, string Url, string? Digest);

/// <summary>A release the API answered with: a stable tag, this repo's page for it, and all its files. Not yet checked for the three files.</summary>
public sealed class ReleaseInfo
{
    public ReleaseInfo(AppVersion version, string pageUrl, IReadOnlyList<ReleaseAsset> assets)
    {
        Version = version;
        PageUrl = pageUrl;
        Assets = assets;
    }

    public AppVersion Version { get; }

    public string Tag => Version.Tag;

    /// <summary>"https://github.com/goemon0000/pc-mieru/releases/tag/vX.Y.Z".</summary>
    public string PageUrl { get; }

    public IReadOnlyList<ReleaseAsset> Assets { get; }
}

/// <summary>The three files a release must carry, once each, at their own addresses (DESIGN-B 3.1).</summary>
public sealed record RequiredAssets(ReleaseAsset Zip, ReleaseAsset Manifest, ReleaseAsset HashList);

internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAssetDto>? Assets { get; set; }
}

internal sealed class GitHubAssetDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(GitHubReleaseDto))]
internal sealed partial class GitHubJson : JsonSerializerContext;

/// <summary>Reads the API's answer, and picks the three files out of it.</summary>
public static class ReleaseParser
{
    /// <summary>The answer of "releases/latest". True and a release when it is a stable release of this repo; otherwise false and the rule that held.</summary>
    public static bool TryParse(string json, out ReleaseInfo? release, out UpdateRefusal refusal, out string detail)
    {
        release = null;
        GitHubReleaseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, GitHubJson.Default.GitHubReleaseDto);
        }
        catch (JsonException)
        {
            return Fail(UpdateRefusal.FeedUnreadable, "the answer is not JSON of the expected shape", out refusal, out detail);
        }

        if (dto is null || string.IsNullOrEmpty(dto.TagName))
        {
            return Fail(UpdateRefusal.FeedUnreadable, "the answer has no tag", out refusal, out detail);
        }

        if (dto.Draft)
        {
            return Fail(UpdateRefusal.DraftRelease, "a draft release", out refusal, out detail);
        }

        if (dto.Prerelease)
        {
            return Fail(UpdateRefusal.PrereleaseRelease, "a pre-release", out refusal, out detail);
        }

        // One spelling: "v1.2.3" and nothing else ("1.2.3", "V1.2.3", "v1.2.3-beta" are not).
        if (!AppVersion.TryParse(dto.TagName, out var version) || dto.TagName != version.Tag)
        {
            return Fail(UpdateRefusal.TagNotStable, "the tag is not a plain vX.Y.Z", out refusal, out detail);
        }

        if (dto.HtmlUrl != UpdateEndpoints.ReleasePageUrl(version.Tag))
        {
            return Fail(UpdateRefusal.ReleasePageMismatch, "the release page is not this repo's page of that tag", out refusal, out detail);
        }

        var assets = new List<ReleaseAsset>();
        foreach (var a in dto.Assets ?? [])
        {
            if (a.Name is { Length: > 0 } name)
            {
                assets.Add(new ReleaseAsset(name, a.Size, a.State ?? string.Empty, a.BrowserDownloadUrl ?? string.Empty, a.Digest));
            }
        }

        release = new ReleaseInfo(version, dto.HtmlUrl, assets);
        refusal = UpdateRefusal.None;
        detail = string.Empty;
        return true;
    }

    /// <summary>
    /// The zip, the outer manifest and SHA256.txt of this release: exactly one of each (by exact name), finished, at exactly this repo's address of that tag
    /// and name, and no larger than their limits say. Otherwise false and the rule that held.
    /// </summary>
    public static bool TrySelect(ReleaseInfo release, out RequiredAssets? assets, out UpdateRefusal refusal, out string detail)
    {
        assets = null;
        var version = release.Version.ToString();
        if (!TryOne(release, BundleFormat.HashListName, Sha256List.MaxBytes, out var list, out refusal, out detail)
            || !TryOne(release, BundleFormat.OuterManifestName(version), Limits.Default.ManifestBytes, out var manifest, out refusal, out detail)
            || !TryOne(release, BundleFormat.ZipName(version), Limits.Default.ZipBytes, out var zip, out refusal, out detail))
        {
            return false;
        }

        assets = new RequiredAssets(zip!, manifest!, list!);
        return true;
    }

    private static bool TryOne(ReleaseInfo release, string name, long maxBytes, out ReleaseAsset? asset, out UpdateRefusal refusal, out string detail)
    {
        asset = null;
        var same = release.Assets.Where(a => string.Equals(a.Name, name, StringComparison.Ordinal)).ToArray();
        if (same.Length == 0)
        {
            return Fail(UpdateRefusal.AssetMissing, $"{name} is not on the release", out refusal, out detail);
        }

        if (same.Length > 1)
        {
            return Fail(UpdateRefusal.AssetDuplicate, $"{name} is on the release {same.Length} times", out refusal, out detail);
        }

        var one = same[0];
        if (!string.Equals(one.State, "uploaded", StringComparison.Ordinal) || one.Size <= 0)
        {
            return Fail(UpdateRefusal.AssetNotReady, $"{name} is not finished uploading, or is empty", out refusal, out detail);
        }

        if (!string.Equals(one.Url, UpdateEndpoints.AssetUrl(release.Tag, name).AbsoluteUri, StringComparison.Ordinal))
        {
            return Fail(UpdateRefusal.AssetUrlMismatch, $"{name} is not at this repo's address of {release.Tag}", out refusal, out detail);
        }

        if (one.Size > maxBytes)
        {
            return Fail(UpdateRefusal.AssetTooLarge, $"{name} is {one.Size} bytes, over {maxBytes}", out refusal, out detail);
        }

        asset = one;
        refusal = UpdateRefusal.None;
        detail = string.Empty;
        return true;
    }

    /// <summary>
    /// The API's "digest" of an asset against a hash: true when there is none, when it names another algorithm (not read), or when it is "sha256:" plus the
    /// same 64 digits. A "sha256:" that is not 64 hex digits is not the same as anything.
    /// </summary>
    public static bool DigestAgrees(string? digest, string sha256Hex)
    {
        if (string.IsNullOrEmpty(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hex = digest["sha256:".Length..];
        return Sha256List.IsHex64(hex) && string.Equals(hex, sha256Hex, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Fail(UpdateRefusal reason, string text, out UpdateRefusal refusal, out string detail)
    {
        refusal = reason;
        detail = text;
        return false;
    }
}
