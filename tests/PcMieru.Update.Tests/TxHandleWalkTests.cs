using System.Text;
using PcMieru.Update.Transaction;

namespace PcMieru.Update.Tests;

/// <summary>
/// The tree walk of a cleaning is done ON HANDLES (review of step 4, M1 residual) and the format of the install's volume is read from a handle to the folder (S5 gap). A folder that is swapped for a junction to somebody's
/// own folder between "look" and "act" - at the top, one level down, two levels down - is unlinked and never entered: every planted file behind the link is still there, byte for byte. A folder that is held open by the
/// walk cannot be swapped at all. A path that is a link when the caller looked and a real folder when it is opened is left alone. The by-name cases (a link there to begin with) are in <see cref="TxLinkSafetyTests"/>.
/// </summary>
public sealed class TxHandleWalkTests
{
    private const string Tx = "abcdef012345";

    private sealed class Folder : IDisposable
    {
        public string Root { get; } = Path.GetFullPath(Scratch.NewFolder("hw")); // fully expanded: the walk names its nodes with GetFullPath, and the hook compares names

        public void Dispose() => Scratch.Delete(Root);
    }

    private static string PlantVictim(string root)
    {
        var victim = Path.Combine(root, "victim");
        Directory.CreateDirectory(Path.Combine(victim, "keep", "deeper"));
        File.WriteAllText(Path.Combine(victim, "loose.txt"), "somebody's own file\n", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(victim, "keep", "planted.bin"), SyntheticSet.Bytes("hw-victim", 500));
        File.WriteAllBytes(Path.Combine(victim, "keep", "deeper", "planted2.bin"), SyntheticSet.Bytes("hw-victim2", 500));
        return victim;
    }

    /// <summary>work\&lt;tx&gt;\a\b\c with a file at every level.</summary>
    private static string PlantTree(string root)
    {
        var top = Path.Combine(root, "update", "work", Tx);
        foreach (var level in new[] { "", "a", Path.Combine("a", "b"), Path.Combine("a", "b", "c") })
        {
            Directory.CreateDirectory(Path.Combine(top, level));
            File.WriteAllText(Path.Combine(top, level, "file.txt"), "ours\n");
        }

        return top;
    }

    private static void Swap(string folder, string aside, string victim)
    {
        Directory.Move(folder, aside);
        Rig.Junction(folder, victim);
    }

    [Theory]
    [InlineData("top")]
    [InlineData("a")]
    [InlineData("a\\b")]
    [InlineData("a\\b\\c")]
    public void A_folder_swapped_for_a_junction_just_before_it_is_opened_is_unlinked_and_what_it_points_to_survives(string depth)
    {
        using var scratch = new Folder();
        var victim = PlantVictim(scratch.Root);
        var before = Rig.Snapshot(victim, includeUpdate: true);
        var top = PlantTree(scratch.Root);
        var target = depth == "top" ? top : Path.Combine(top, depth);
        var swapped = false;
        var io = new TxIo(NoFaults.Instance)
        {
            TreeHook = (stage, path) =>
            {
                if (stage == "open" && !swapped && string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
                {
                    swapped = true; // the walk has looked at the listing (or the caller at the name) and the name is changed right now
                    Swap(target, Path.Combine(scratch.Root, "aside"), victim);
                }
            },
        };

        io.DeleteTree(TxSteps.StaleClean, top);

        Assert.True(swapped, "the hook must have run");
        var difference = Rig.Diff(before, Rig.Snapshot(victim, includeUpdate: true));
        Assert.True(difference.Length == 0, "what the junction pointed to changed: " + difference);
        Assert.False(Directory.Exists(top), "the whole tree is gone, the junction with it");
        Assert.True(Directory.Exists(Path.Combine(scratch.Root, "aside")), "what was moved aside is not the walk's to delete");
    }

    [Theory]
    [InlineData("top")]
    [InlineData("a")]
    [InlineData("a\\b")]
    public void A_folder_the_walk_holds_cannot_be_swapped_for_a_junction_and_its_content_is_removed_with_it(string depth)
    {
        using var scratch = new Folder();
        var victim = PlantVictim(scratch.Root);
        var before = Rig.Snapshot(victim, includeUpdate: true);
        var top = PlantTree(scratch.Root);
        var target = depth == "top" ? top : Path.Combine(top, depth);
        var attempted = false;
        Exception? blocked = null;
        var io = new TxIo(NoFaults.Instance)
        {
            TreeHook = (stage, path) =>
            {
                if (stage == "opened" && !attempted && string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
                {
                    attempted = true; // between "looked at" and "listed": the folder is held, so renaming it away is refused
                    try
                    {
                        Swap(target, Path.Combine(scratch.Root, "aside"), victim);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        blocked = ex;
                    }
                }
            },
        };

        io.DeleteTree(TxSteps.StaleClean, top);

        Assert.True(attempted, "the hook must have run");
        Assert.NotNull(blocked);
        Assert.False(Directory.Exists(Path.Combine(scratch.Root, "aside")), "nothing was moved");
        Assert.False(Directory.Exists(top), "the tree is gone");
        var difference = Rig.Diff(before, Rig.Snapshot(victim, includeUpdate: true));
        Assert.True(difference.Length == 0, "the victim changed: " + difference);
    }

    [Fact]
    public void A_container_that_was_a_link_when_it_was_looked_at_and_is_a_real_folder_when_it_is_opened_is_left_alone()
    {
        using var scratch = new Folder();
        var victim = PlantVictim(scratch.Root);
        var update = Path.Combine(scratch.Root, "update");
        Directory.CreateDirectory(update);
        var work = Path.Combine(update, "work");
        Rig.Junction(work, victim);
        var changed = false;
        var io = new TxIo(NoFaults.Instance)
        {
            TreeHook = (stage, path) =>
            {
                if (stage == "open" && !changed && string.Equals(path, work, StringComparison.OrdinalIgnoreCase))
                {
                    changed = true; // the junction is replaced by a real folder with somebody's file in it, after DeleteTree decided it was a link
                    Directory.Delete(work, recursive: false);
                    Directory.CreateDirectory(Path.Combine(work, "precious"));
                    File.WriteAllText(Path.Combine(work, "precious", "file.txt"), "not ours\n");
                }
            },
        };

        io.DeleteTree(TxSteps.StaleClean, Path.Combine(work, Tx));

        Assert.True(changed, "the hook must have run");
        Assert.True(File.Exists(Path.Combine(work, "precious", "file.txt")), "a real folder is not removed by the unlinking of a link");
        Assert.True(File.Exists(Path.Combine(victim, "keep", "planted.bin")));
    }

    [Fact]
    public void A_plain_file_where_a_folder_is_expected_is_left_alone_and_a_folder_that_is_not_there_is_not_an_error()
    {
        using var scratch = new Folder();
        var file = Path.Combine(scratch.Root, "update", "work", Tx);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "a file where a folder should be\n");
        var io = new TxIo(NoFaults.Instance);

        io.DeleteTree(TxSteps.StaleClean, file);
        io.DeleteTree(TxSteps.StaleClean, Path.Combine(scratch.Root, "update", "work", "fedcba987654"));

        Assert.True(File.Exists(file));
    }

    [Fact]
    public void A_deep_tree_with_many_entries_is_removed_completely()
    {
        using var scratch = new Folder();
        var top = Path.Combine(scratch.Root, "update", "work", Tx);
        for (var i = 0; i < 40; i++)
        {
            var folder = Path.Combine(top, "d" + i, "inner");
            Directory.CreateDirectory(folder);
            for (var j = 0; j < 30; j++)
            {
                File.WriteAllText(Path.Combine(folder, "f" + j + ".txt"), "x");
            }
        }

        new TxIo(NoFaults.Instance).DeleteTree(TxSteps.StaleClean, top);

        Assert.False(Directory.Exists(top));
        Assert.False(Directory.Exists(Path.Combine(scratch.Root, "update", "work")), "an empty work/ goes with its last transaction folder");
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the real volume (S5)

    [Fact]
    public void The_format_comes_from_what_the_volume_says_about_the_folder_and_not_from_the_root_of_its_path()
    {
        Assert.Equal("FAT32", new SystemVolumeFormat(_ => new VolumeFacts("FAT32", 1, false)).FormatOf(@"C:\anything"));
        Assert.False(SystemVolumeFormat.IsSupported(new SystemVolumeFormat(_ => new VolumeFacts("exFAT", 1, false)).FormatOf(@"C:\anything")));
        Assert.True(SystemVolumeFormat.IsSupported(new SystemVolumeFormat(_ => new VolumeFacts("ReFS", 1, false)).FormatOf(@"C:\anything")));
        Assert.Null(new SystemVolumeFormat(_ => new VolumeFacts("NTFS", 1, IsNetwork: true)).FormatOf(@"C:\anything"));
        Assert.Null(new SystemVolumeFormat(_ => null).FormatOf(@"C:\anything"));
        Assert.Null(new SystemVolumeFormat(_ => throw new UnauthorizedAccessException()).FormatOf(@"C:\anything"));
    }

    [Fact]
    public void A_folder_reached_through_a_junction_answers_for_the_volume_it_is_on_and_a_missing_folder_for_the_one_above_it()
    {
        using var scratch = new Folder();
        var here = TxNative.ReadVolume(scratch.Root);
        Assert.NotNull(here);
        Assert.True(SystemVolumeFormat.IsSupported(here.Value.Format), "the scratch volume is NTFS or ReFS: " + here.Value.Format);
        Assert.False(here.Value.IsNetwork);

        var missing = TxNative.ReadVolume(Path.Combine(scratch.Root, "not", "there", "yet"));
        Assert.Equal(here, missing);

        // A junction from the scratch volume to ANOTHER fixed volume's root (read only: nothing is written there). The drive letter of the junction's path is the scratch drive's; the volume is the other one's.
        var other = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => (Drive: d, Facts: TxNative.ReadVolume(d.RootDirectory.FullName)))
            .Where(x => x.Facts is not null && x.Facts.Value.Serial != here.Value.Serial)
            .OrderByDescending(x => !string.Equals(x.Facts!.Value.Format, here.Value.Format, StringComparison.OrdinalIgnoreCase)) // a volume of ANOTHER format first (a FAT32 or exFAT disk, where there is one): that is what the old reading got wrong
            .FirstOrDefault();
        if (other.Facts is null)
        {
            return; // only one volume on this machine: the seam test above is what there is
        }

        var link = Path.Combine(scratch.Root, "to-other-volume");
        Rig.Junction(link, other.Drive.RootDirectory.FullName);
        try
        {
            var through = TxNative.ReadVolume(link);
            Assert.NotNull(through);
            Assert.Equal(other.Facts.Value.Serial, through.Value.Serial);
            Assert.NotEqual(here.Value.Serial, through.Value.Serial);
            Assert.Equal(other.Facts.Value.Format, SystemVolumeFormat.Instance.FormatOf(link));
            Assert.Equal(other.Facts.Value.Format, new DriveInfo(other.Drive.RootDirectory.FullName).DriveFormat); // what the drive letter of the target says is what the junction must answer too
        }
        finally
        {
            Directory.Delete(link, recursive: false);
        }
    }

    [Fact]
    public void A_network_path_and_a_path_with_no_volume_answer_nothing()
    {
        Assert.Null(SystemVolumeFormat.Instance.FormatOf(@"\\no-such-server\share\folder"));
        Assert.False(SystemVolumeFormat.IsSupported(SystemVolumeFormat.Instance.FormatOf(@"\\no-such-server\share\folder")));
    }
}
