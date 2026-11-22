// Provenance: the cases come from the owner's pl-cloud tests/PartsLife.Update.Tests/RulesTests.cs (AppVersionTests, Sha256ListTests; Parts Life, MIT,
// same owner; see NOTICE.txt). New here: the leading-zero rejection, the two-line SHA256.txt of a release (zip and outer manifest) and the English comments.
using PcMieru.Update.Feed;

namespace PcMieru.Update.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("v0.5.0", 0, 5, 0)]
    [InlineData("0.5.0", 0, 5, 0)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("v10.20.30", 10, 20, 30)]
    [InlineData("v0.0.0", 0, 0, 0)]
    public void Reads_three_plain_numbers(string text, int a, int b, int c)
    {
        Assert.True(AppVersion.TryParse(text, out var version));
        Assert.Equal(new AppVersion(a, b, c), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3.4")]
    [InlineData("v1.2.3-beta")]
    [InlineData("v1.2.3-rc.1")]
    [InlineData("v1.2.3+build")]
    [InlineData("v1..3")]
    [InlineData("v-1.2.3")]
    [InlineData("v1.2.x")]
    [InlineData(" v1.2.3")]
    [InlineData("v1.2.3 ")]
    [InlineData("v1234567.0.0")]
    [InlineData("vv1.2.3")]
    public void Rejects_anything_else(string? text) => Assert.False(AppVersion.TryParse(text, out _));

    [Theory]
    [InlineData("v01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("v00.0.0")]
    public void Rejects_a_number_with_a_leading_zero(string text) => Assert.False(AppVersion.TryParse(text, out _));

    [Fact]
    public void Compares_numerically_not_as_text()
    {
        Assert.True(new AppVersion(0, 7, 10) > new AppVersion(0, 7, 9));
        Assert.True(new AppVersion(0, 10, 0) > new AppVersion(0, 9, 99));
        Assert.True(new AppVersion(1, 0, 0) > new AppVersion(0, 99, 99));
        Assert.False(new AppVersion(0, 7, 2) > new AppVersion(0, 7, 2));
        Assert.True(new AppVersion(0, 7, 2) >= new AppVersion(0, 7, 2));
        Assert.True(new AppVersion(0, 7, 2) <= new AppVersion(0, 7, 2));
        Assert.True(new AppVersion(0, 7, 1) < new AppVersion(0, 7, 2));
    }

    [Fact]
    public void Prints_the_two_spellings()
    {
        Assert.Equal("0.7.3", new AppVersion(0, 7, 3).ToString());
        Assert.Equal("v0.7.3", new AppVersion(0, 7, 3).Tag);
    }

    [Fact]
    public void Assembly_version_drops_the_fourth_number() =>
        Assert.Equal(new AppVersion(0, 7, 2), AppVersion.From(new Version(0, 7, 2, 0)));
}

public class Sha256ListTests
{
    private const string A = "72b90b0088c28f8bfeef3b739bfb263d5288674dd384b3f7c141bcbd6d2178ac";
    private const string B = "2c0f3321e02b1487fb08cf02ebcabd6b32292e9318ec02b3be5f266c4bbe88de";

    [Fact]
    public void Reads_the_two_lines_of_a_release()
    {
        // The shape tools/publish.ps1 writes: 64 hex digits, two spaces, the name.
        var list = Sha256List.Parse($"{A}  PCMieru-0.5.0-win-x64.zip\r\n{B}  PCMieru-0.5.0-win-x64.manifest.json\r\n");
        Assert.Equal(A, list.For("PCMieru-0.5.0-win-x64.zip"));
        Assert.Equal(B, list.For("PCMieru-0.5.0-win-x64.manifest.json"));
        Assert.Equal(2, list.Names.Count);
    }

    [Fact]
    public void Tolerates_bom_star_and_upper_case()
    {
        var list = Sha256List.Parse($"﻿{A.ToUpperInvariant()} *PCMieru.zip\n");
        Assert.Equal(A, list.For("PCMieru.zip"));
    }

    [Fact]
    public void Names_must_match_exactly()
    {
        var list = Sha256List.Parse($"{A}  PCMieru.zip\n");
        Assert.Null(list.For("pcmieru.zip"));
        Assert.Null(list.For("PCMieru.zip "));
        Assert.Null(list.For("PCMieru.exe"));
    }

    [Fact]
    public void Conflicting_lines_disable_that_name()
    {
        var list = Sha256List.Parse($"{A}  PCMieru.zip\n{B}  PCMieru.zip\n{B}  PCMieru.manifest.json\n");
        Assert.Null(list.For("PCMieru.zip"));
        Assert.Equal(B, list.For("PCMieru.manifest.json"));

        // The same value twice is not a conflict.
        Assert.Equal(A, Sha256List.Parse($"{A}  PCMieru.zip\n{A}  PCMieru.zip\n").For("PCMieru.zip"));
    }

    [Theory]
    [InlineData("72b90b00  PCMieru.zip")]
    [InlineData(A + " PCMieru.zip")] // one space: the second one would be the first letter of the name
    [InlineData(A + "  ")]
    [InlineData("zz" + "b90b0088c28f8bfeef3b739bfb263d5288674dd384b3f7c141bcbd6d2178ac  PCMieru.zip")]
    public void Malformed_lines_are_ignored(string line) => Assert.Empty(Sha256List.Parse(line).Names);

    [Fact]
    public void Hashes_a_file_and_compares_without_regard_to_case()
    {
        var path = Path.Combine(Path.GetTempPath(), "pcmieru-update-tests-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            var hash = Sha256List.HashFile(path);
            Assert.Equal(64, hash.Length);
            Assert.True(Sha256List.Same(hash, hash.ToUpperInvariant()));
            Assert.False(Sha256List.Same(hash, A));
            Assert.False(Sha256List.Same(null, hash));
            Assert.False(Sha256List.Same("", ""));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
