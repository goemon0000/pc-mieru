using PcMieru.BundleCheck;
using PcMieru.Update.Bundle;

namespace PcMieru.Update.Tests;

/// <summary>
/// tools/BundleCheck as publish.ps1 and CI call it. The floor is the one argument that must never be left to a default: a default that is the partial
/// floor makes every release pass without the Swap.exe it is missing, and a default that is the full one makes the trial of this step fail for no reason
/// the person reading the log can see. So the caller says which, and a missing or misspelled value is an error of the command line (exit 2), not a verdict.
/// </summary>
public sealed class BundleCheckCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pcmieru-update-tests-cli-" + Guid.NewGuid().ToString("N"));

    public BundleCheckCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A scratch folder in the temp folder: a leftover is harmless.
        }
    }

    private sealed record Ran(int Exit, string Output, string Error);

    private static Ran Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = Cli.Run(args, output, error);
        return new Ran(exit, output.ToString(), error.ToString());
    }

    /// <summary>Writes the zip, its outer manifest and the files of the bundle as a folder; returns the three paths.</summary>
    private (string Zip, string Manifest, string Folder) Lay(TestBundle bundle, byte[]? zipBytes = null)
    {
        var zip = Path.Combine(_dir, BundleFormat.ZipName(bundle.Version));
        var manifest = Path.Combine(_dir, BundleFormat.OuterManifestName(bundle.Version));
        var folder = Path.Combine(_dir, "folder");
        File.WriteAllBytes(zip, zipBytes ?? bundle.Zip());
        File.WriteAllBytes(manifest, bundle.Outer().ToUtf8Bytes());
        foreach (var (name, data) in bundle.Files)
        {
            var path = Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }

        File.WriteAllBytes(Path.Combine(folder, BundleFormat.InnerManifestName), bundle.InnerBytes());
        return (zip, manifest, folder);
    }

    // ---------------------------------------------------------------------------------------------------------------- the floor is asked for

    [Fact]
    public void A_zip_check_with_no_floor_is_a_mistake_of_the_command_line()
    {
        var (zip, manifest, _) = Lay(new TestBundle());
        var ran = Run("zip", zip, manifest, "0.5.0");
        Assert.Equal(2, ran.Exit);
        Assert.Contains("--floor", ran.Error);
        Assert.DoesNotContain("OK", ran.Output);
    }

    [Fact]
    public void A_folder_check_with_no_floor_is_a_mistake_of_the_command_line()
    {
        var (_, _, folder) = Lay(new TestBundle());
        var ran = Run("folder", folder, "0.5.0");
        Assert.Equal(2, ran.Exit);
        Assert.Contains("--floor", ran.Error);
    }

    [Theory]
    [InlineData("build")] // the spelling of before: the value says what is meant now
    [InlineData("partial")]
    [InlineData("")]
    [InlineData("FULL")]
    [InlineData("this_build")]
    public void A_floor_that_is_not_one_of_the_two_names_is_a_mistake_of_the_command_line(string value)
    {
        var (zip, manifest, folder) = Lay(new TestBundle());
        Assert.Equal(2, Run("zip", zip, manifest, "0.5.0", "--floor", value).Exit);
        Assert.Equal(2, Run("folder", folder, "0.5.0", "--floor", value).Exit);
    }

    [Fact]
    public void A_floor_with_no_value_is_a_mistake_of_the_command_line()
    {
        var (zip, manifest, _) = Lay(new TestBundle());
        Assert.Equal(2, Run("zip", zip, manifest, "0.5.0", "--floor").Exit);
    }

    [Fact]
    public void The_floor_of_this_build_passes_a_bundle_of_this_build_in_a_zip_and_in_a_folder()
    {
        var bundle = new TestBundle();
        var (zip, manifest, folder) = Lay(bundle);

        var zipRan = Run("zip", zip, manifest, "0.5.0", "--floor", "this-build");
        Assert.Equal(0, zipRan.Exit);
        Assert.StartsWith("OK: ", zipRan.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, zipRan.Error);

        var folderRan = Run("folder", folder, "0.5.0", "--outer", manifest, "--floor", "this-build");
        Assert.Equal(0, folderRan.Exit);
        Assert.StartsWith("OK: ", folderRan.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_full_floor_refuses_a_bundle_without_the_swapper_and_names_what_is_missing()
    {
        var withoutSwapper = new TestBundle();
        withoutSwapper.Files.Remove("PCMieru.Swap.exe");
        var (zip, manifest, folder) = Lay(withoutSwapper);

        var zipRan = Run("zip", zip, manifest, "0.5.0", "--floor", "full");
        Assert.Equal(1, zipRan.Exit);
        Assert.StartsWith("FAIL FloorMissing: ", zipRan.Error, StringComparison.Ordinal);
        Assert.Contains("PCMieru.Swap.exe", zipRan.Error);

        var folderRan = Run("folder", folder, "0.5.0", "--floor", "full");
        Assert.Equal(1, folderRan.Exit);
        Assert.StartsWith("FAIL FloorMissing: ", folderRan.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_full_floor_passes_a_bundle_of_this_build()
    {
        var bundle = new TestBundle();
        foreach (var name in BundleFloor.NotYetBuilt)
        {
            bundle.Files[name] = System.Text.Encoding.ASCII.GetBytes("later " + name);
        }

        var (zip, manifest, folder) = Lay(bundle);
        Assert.Equal(0, Run("zip", zip, manifest, "0.5.0", "--floor", "full").Exit);
        Assert.Equal(0, Run("folder", folder, "0.5.0", "--floor", "full").Exit);
    }

    // ---------------------------------------------------------------------------------------------------------------------------- --list

    [Fact]
    public void List_prints_the_central_directory_names_one_per_line_in_ordinal_order()
    {
        var bundle = new TestBundle();
        var zip = bundle.Zip(e =>
        {
            e.Reverse(); // not in order in the file
            e.Insert(0, new RawEntry(bundle.Root + "/ja/", []));
        });
        var (path, _, _) = Lay(bundle, zip);

        var ran = Run("--list", path);
        Assert.Equal(0, ran.Exit);
        Assert.Equal(string.Empty, ran.Error);

        var expected = bundle.Entries().Select(e => e.Name).Append(bundle.Root + "/ja/").OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var printed = ran.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expected, printed);
        Assert.DoesNotContain('\r', ran.Output);
    }

    [Fact]
    public void List_does_not_judge_the_names_so_two_zips_can_be_compared_even_when_one_is_bad()
    {
        // A zip with backslash names (what 5.1's CreateFromDirectory makes) is refused by the validator, and this is exactly the difference CI wants to see.
        var bundle = new TestBundle();
        var (path, _, _) = Lay(bundle, bundle.FrameworkStyleZip());

        var ran = Run("--list", path);
        Assert.Equal(0, ran.Exit);
        Assert.All(ran.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries), n => Assert.Contains('\\', n));
    }

    [Fact]
    public void List_of_a_file_that_is_not_a_zip_or_is_not_there_fails_with_a_line_and_no_exception()
    {
        var notZip = Path.Combine(_dir, "not-a-zip.zip");
        File.WriteAllBytes(notZip, new byte[100]);
        var bad = Run("--list", notZip);
        Assert.Equal(1, bad.Exit);
        Assert.StartsWith("FAIL NotZip: ", bad.Error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, bad.Output);

        var missing = Run("--list", Path.Combine(_dir, "nothing.zip"));
        Assert.Equal(1, missing.Exit);
        Assert.StartsWith("FAIL ReadFailed: ", missing.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void List_refuses_a_zip_that_hides_a_second_end_record()
    {
        // The list must be the list the validator would use: a zip with two readings has no list.
        var bundle = new TestBundle();
        var real = bundle.Zip();
        var (_, records) = ZipSurgery.Split(real);
        var (path, _, _) = Lay(bundle, ZipSurgery.WithPlantedEnd(real, records, 0));
        var ran = Run("--list", path);
        Assert.Equal(1, ran.Exit);
        Assert.StartsWith("FAIL ZipAmbiguous: ", ran.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void List_without_a_path_or_with_more_than_one_is_a_mistake_of_the_command_line()
    {
        Assert.Equal(2, Run("--list").Exit);
        Assert.Equal(2, Run("--list", "a.zip", "b.zip").Exit);
    }
}
