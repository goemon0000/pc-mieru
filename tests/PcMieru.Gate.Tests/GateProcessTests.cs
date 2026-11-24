using System.Security.AccessControl;
using PcMieru.Wire.Gate;

namespace PcMieru.Gate.Tests;

/// <summary>
/// The gate seen from outside, with real child processes of the party stub (the smallest program with the one gate call every exe makes) and of pcmi: where the gate is looked for (only
/// next to the real exe), that a running program holds it for as long as it works, that an alias of the install folder is the same gate, and that a folder nobody can write into
/// (a manual unzip made read-only) still starts normally.
/// </summary>
public sealed class GateProcessTests : IClassFixture<Boxes>
{
    private readonly Boxes _boxes;

    public GateProcessTests(Boxes boxes) => _boxes = boxes;

    // ---- where the gate is looked for ----

    [Theory]
    [InlineData(Exe.Party)]
    [InlineData(Exe.Cli)]
    public void A_gate_file_in_the_working_directory_or_in_the_local_app_data_folder_is_never_the_gate(Exe exe)
    {
        var box = _boxes.Get(exe);
        var decoy = _boxes.Scratch.Sub("decoy-" + exe);
        File.WriteAllBytes(Path.Combine(decoy, GateFile.FileName), GateFile.Bytes());
        Directory.CreateDirectory(Path.Combine(decoy, "PCMieru"));
        File.WriteAllBytes(Path.Combine(decoy, "PCMieru", GateFile.FileName), GateFile.Bytes());
        var environment = new Dictionary<string, string> { ["LOCALAPPDATA"] = decoy, ["APPDATA"] = decoy };

        using var situation = Situation.Apply(_boxes.Scratch, box.ExePath, GateOutcome.MissingGate); // none beside the exe
        var run = Child.Run(box.ExePath, exe == Exe.Cli ? ["version"] : [], _boxes.AppData, workingDirectory: decoy, environment: environment);

        ExeContract.AssertRefused(exe, GateOutcome.MissingGate, run, _boxes.Scratch);
    }

    [Theory]
    [InlineData(Exe.Party)]
    [InlineData(Exe.Cli)]
    public void The_gate_beside_the_exe_is_the_gate_whatever_the_working_directory_is(Exe exe)
    {
        var box = _boxes.Get(exe);
        var elsewhere = _boxes.Scratch.Sub("cwd-" + exe); // no gate file here

        var run = Child.Run(box.ExePath, exe == Exe.Cli ? ["version"] : [], _boxes.AppData, workingDirectory: elsewhere);

        ExeContract.AssertStartedNormally(exe, run, _boxes.Scratch);
    }

    // ---- a running program holds the gate ----

    [Fact]
    public void A_running_program_holds_the_gate_others_of_the_install_coexist_and_nothing_can_take_it_away()
    {
        var box = _boxes.Get(Exe.Party);
        using var holder = Child.Start(box.ExePath, ["--hold"], _boxes.AppData);
        Assert.Equal("party holding", holder.ReadLine(TimeSpan.FromSeconds(60)));

        // Another program of the same install starts (shared with shared).
        var second = Child.Run(box.ExePath, [], _boxes.AppData);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal("party ok\n", second.Out);

        // The swapper (exclusive) cannot have it, and the file cannot be deleted or renamed under the program.
        using (var swapper = InstallGate.EnterExclusive(box.ExePath))
        {
            Assert.Equal(GateOutcome.Busy, swapper.Outcome);
        }

        Assert.ThrowsAny<IOException>(() => File.Delete(box.GatePath));
        Assert.ThrowsAny<IOException>(() => File.Move(box.GatePath, box.GatePath + ".moved"));

        // Its work ends: the gate is free again.
        holder.CloseInput();
        var ran = holder.Wait();
        Assert.Equal(0, ran.ExitCode);
        Assert.Equal("party holding\nparty ok\n", ran.Out);
        using var after = InstallGate.EnterExclusive(box.ExePath);
        Assert.Equal(GateOutcome.Open, after.Outcome);
    }

    [Fact]
    public void A_program_that_is_killed_lets_go_of_the_gate_with_its_process()
    {
        var box = _boxes.Get(Exe.Party);
        var holder = Child.Start(box.ExePath, ["--hold"], _boxes.AppData);
        try
        {
            Assert.Equal("party holding", holder.ReadLine(TimeSpan.FromSeconds(60)));
            using (var swapper = InstallGate.EnterExclusive(box.ExePath))
            {
                Assert.Equal(GateOutcome.Busy, swapper.Outcome);
            }
        }
        finally
        {
            holder.Dispose(); // kills it: a crash holds nothing (that is the reason the gate is a file handle and not a marker file)
        }

        using var after = InstallGate.EnterExclusive(box.ExePath);
        Assert.Equal(GateOutcome.Open, after.Outcome);
    }

    // ---- an alias of the install folder is the same gate ----

    [Fact]
    public void A_program_started_through_a_junction_to_the_install_folder_meets_the_gate_held_through_the_real_path()
    {
        var box = _boxes.Get(Exe.Party);
        var alias = Path.Combine(_boxes.Scratch.Root, "alias-" + Guid.NewGuid().ToString("N")[..6]);
        var undo = _boxes.Scratch.Junction(alias, box.Folder);
        try
        {
            var viaAlias = Path.Combine(alias, Repo.ExeNameOf(Exe.Party));

            using (var swapper = InstallGate.EnterExclusive(box.ExePath))
            {
                Assert.True(swapper.IsOpen, swapper.Detail);
                var refused = Child.Run(viaAlias, [], _boxes.AppData);
                Assert.Equal(1, refused.ExitCode);
                Assert.StartsWith("party " + InstallGate.ReasonOf(GateOutcome.Busy), refused.Err, StringComparison.Ordinal);
            }

            var free = Child.Run(viaAlias, [], _boxes.AppData);
            Assert.Equal(0, free.ExitCode);
        }
        finally
        {
            undo();
        }
    }

    [Fact]
    public void A_program_that_holds_the_gate_through_a_junction_keeps_out_the_swapper_that_comes_by_the_real_path()
    {
        var box = _boxes.Get(Exe.Party);
        var alias = Path.Combine(_boxes.Scratch.Root, "alias-" + Guid.NewGuid().ToString("N")[..6]);
        var undo = _boxes.Scratch.Junction(alias, box.Folder);
        try
        {
            using var holder = Child.Start(Path.Combine(alias, Repo.ExeNameOf(Exe.Party)), ["--hold"], _boxes.AppData);
            Assert.Equal("party holding", holder.ReadLine(TimeSpan.FromSeconds(60)));

            using (var swapper = InstallGate.EnterExclusive(box.ExePath))
            {
                Assert.Equal(GateOutcome.Busy, swapper.Outcome);
            }

            holder.CloseInput();
            Assert.Equal(0, holder.Wait().ExitCode);
        }
        finally
        {
            undo();
        }
    }

    [Fact]
    public void A_program_started_through_a_junction_finds_the_gate_by_the_real_folder_and_says_so()
    {
        var box = _boxes.Get(Exe.Party);
        var alias = Path.Combine(_boxes.Scratch.Root, "alias-" + Guid.NewGuid().ToString("N")[..6]);
        var undo = _boxes.Scratch.Junction(alias, box.Folder);
        try
        {
            // Environment.ProcessPath is the path the program was started by (the alias). The gate is looked for by the FINAL path of that file: the real folder.
            var run = Child.Run(Path.Combine(alias, Repo.ExeNameOf(Exe.Party)), ["--gate-path"], _boxes.AppData);

            Assert.Equal(0, run.ExitCode);
            Assert.Equal("party gate " + box.GatePath + "\nparty ok\n", run.Out);
        }
        finally
        {
            undo();
        }
    }

    [Fact]
    public void A_program_started_through_a_hard_link_of_its_exe_finds_the_gate_beside_the_name_it_was_started_by()
    {
        var box = _boxes.Get(Exe.Party);
        var folder = Path.Combine(_boxes.Scratch.Root, "hardlinked-" + Guid.NewGuid().ToString("N")[..6]);
        Repo.CopyBuild(box.Folder, folder); // everything the exe needs beside it, and a gate file of its own (a separate file: one name)
        var linked = Path.Combine(folder, Repo.ExeNameOf(Exe.Party));
        File.Delete(linked);
        var undo = _boxes.Scratch.Hardlink(linked, box.ExePath); // the exe: one file, two names
        try
        {
            using var holder = Child.Start(linked, ["--gate-path", "--hold"], _boxes.AppData);
            Assert.Equal("party gate " + Path.Combine(folder, GateFile.FileName), holder.ReadLine(TimeSpan.FromSeconds(60)));
            Assert.Equal("party holding", holder.ReadLine(TimeSpan.FromSeconds(60)));

            // The gate it holds is the one beside the link, so that install is kept out of the swapper's way and the other install is not.
            using (var swapper = InstallGate.EnterExclusive(linked))
            {
                Assert.Equal(GateOutcome.Busy, swapper.Outcome);
            }

            using (var other = InstallGate.EnterExclusive(box.ExePath))
            {
                Assert.True(other.IsOpen, "the gate beside the other name of the exe is a different file: " + other.Detail);
            }

            holder.CloseInput();
            Assert.Equal(0, holder.Wait().ExitCode);
        }
        finally
        {
            undo();
        }
    }

    // ---- a folder nobody may write into ----

    [Fact]
    public void A_manual_unzip_that_was_made_read_only_still_starts_normally_the_gate_only_needs_to_be_read()
    {
        var box = _boxes.Get(Exe.Party);
        var cli = _boxes.Get(Exe.Cli);
        var undoParty = _boxes.Scratch.Deny(box.Folder, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles);
        var undoCli = _boxes.Scratch.Deny(cli.Folder, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles);
        try
        {
            ExeContract.AssertStartedNormally(Exe.Party, Child.Run(box.ExePath, [], _boxes.AppData), _boxes.Scratch);
            ExeContract.AssertStartedNormally(Exe.Cli, Child.Run(cli.ExePath, ["version"], _boxes.AppData), _boxes.Scratch);

            // And an install folder whose files are all read-only too (the attribute, as an unzip from a read-only medium leaves it).
            var attributes = File.GetAttributes(box.GatePath);
            File.SetAttributes(box.GatePath, attributes | FileAttributes.ReadOnly);
            try
            {
                ExeContract.AssertStartedNormally(Exe.Party, Child.Run(box.ExePath, [], _boxes.AppData), _boxes.Scratch);
            }
            finally
            {
                File.SetAttributes(box.GatePath, attributes);
            }
        }
        finally
        {
            undoCli();
            undoParty();
        }
    }
}
