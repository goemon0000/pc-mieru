using System.Globalization;
using System.Text.RegularExpressions;
using PcMieru.Update.Bundle;

namespace PcMieru.Update.Tests;

/// <summary>
/// The validator against REAL release zips (CI leaves these out: the zips are made on a workstation).
/// <list type="number">
/// <item>The v0.2.0 release zip was made by Windows PowerShell 5.1's CreateFromDirectory: every entry name has backslashes. The client must refuse it
/// (DESIGN-B 13 item 1), which is the whole reason publish.ps1 now creates the entries itself.</item>
/// <item>The zip that tools/publish.ps1 made (the trial of this step): it must pass, and the inner manifest it carries must be byte for byte what the
/// C# writer makes of the same content: publish.ps1 writes that JSON by hand, and this is where the two are proved to agree.</item>
/// </list>
/// A zip that is not there is a failure, not a skip (a skipped real-machine test proves nothing). Both are only read.
/// </summary>
[Trait("Category", "RealMachine")]
public sealed partial class RealMachineTests
{
    private const string V020Variable = "PCMIERU_V020_ZIP";
    private const string TrialVariable = "PCMIERU_TRIAL_ZIP";

    private static string V020Path() =>
        Environment.GetEnvironmentVariable(V020Variable) is { Length: > 0 } fromEnv
            ? fromEnv
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "dev", "pc-mieru", "dist", "PCMieru-0.2.0-win-x64.zip");

    private static FileStream OpenRead(string path)
    {
        Assert.True(File.Exists(path), $"the zip '{path}' is not there");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
    }

    [Fact]
    public void The_v0_2_0_zip_made_by_CreateFromDirectory_is_refused_for_its_backslashes()
    {
        using var stream = OpenRead(V020Path());
        var outer = new BundleManifest("0.2.0", TestBundle.DefaultBuildId, []);

        var result = BundleZip.Validate(stream, "0.2.0", outer, Limits.Default);

        Assert.False(result.Ok, "the v0.2.0 zip must not be accepted");
        Assert.Equal(BundleFault.Backslash, result.Fault);
    }

    [Fact]
    public void The_zip_that_publish_made_passes_and_its_inner_manifest_is_what_the_writer_makes()
    {
        var zipPath = Environment.GetEnvironmentVariable(TrialVariable) ?? string.Empty;
        Assert.False(zipPath.Length == 0, $"set {TrialVariable} to the zip that tools/publish.ps1 made");
        Assert.EndsWith(".zip", zipPath, StringComparison.Ordinal);

        var name = Path.GetFileName(zipPath);
        var match = ZipName().Match(name);
        Assert.True(match.Success, $"'{name}' is not PCMieru-X.Y.Z-win-x64.zip");
        var version = match.Groups["v"].Value;

        // The outer manifest sits beside the zip: PCMieru-X.Y.Z-win-x64.manifest.json.
        var manifestPath = zipPath[..^".zip".Length] + ".manifest.json";
        Assert.True(File.Exists(manifestPath), $"the manifest '{manifestPath}' is not beside the zip");
        var manifestBytes = File.ReadAllBytes(manifestPath);
        Assert.True(BundleManifest.TryParse(manifestBytes, out var outer, out var verdict), verdict.ToString());
        Assert.Equal(version, outer.Version);

        // What the publish of this build offers: the whole floor (the swapper ships since step 4, so this build's floor is the full one).
        using (var stream = OpenRead(zipPath))
        {
            var result = BundleZip.Validate(stream, version, outer, Limits.Default, BundleFloor.ForThisBuild);
            Assert.True(result.Ok, result.ToString());
            Assert.Equal(outer.Files.Count, result.FileCount);
            Assert.Equal(outer.Files.Sum(f => f.Bytes), result.TotalBytes);
        }

        // The outer manifest is what the writer makes of its own content (publish.ps1 wrote it by hand).
        Assert.Equal(manifestBytes, outer.ToUtf8Bytes());

        // The inner manifest in the zip is the same (read through ZipArchive, which does not mind the validator).
        using var again = OpenRead(zipPath);
        using var archive = new System.IO.Compression.ZipArchive(again, System.IO.Compression.ZipArchiveMode.Read);
        var inner = archive.GetEntry(BundleFormat.RootFolder(version) + "/" + BundleFormat.InnerManifestName);
        Assert.NotNull(inner);
        using var innerStream = inner.Open();
        var innerBytes = new MemoryStream();
        innerStream.CopyTo(innerBytes);
        Assert.True(BundleManifest.TryParse(innerBytes.ToArray(), out var innerManifest, out var innerVerdict), innerVerdict.ToString());
        Assert.Equal(innerBytes.ToArray(), innerManifest.ToUtf8Bytes());

        // And the hand-written forms differ from the writer's by nothing: no BOM, no CR, a final LF is part of the writer's shape already.
        Assert.DoesNotContain((byte)'\r', innerBytes.ToArray());
        Assert.DoesNotContain((byte)'\r', manifestBytes);
        Assert.NotEqual((byte)0xEF, manifestBytes[0]);
        Assert.Equal(outer.BuildId, innerManifest.BuildId);
        Assert.True(Guid.TryParseExact(outer.BuildId, "D", out _), "the build id is a GUID");
        Assert.Equal(outer.BuildId, outer.BuildId.ToLower(CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"^PCMieru-(?<v>\d+\.\d+\.\d+)-win-x64\.zip$")]
    private static partial Regex ZipName();
}
