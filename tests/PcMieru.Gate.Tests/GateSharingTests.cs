using System.Diagnostics;
using PcMieru.Wire.Gate;

namespace PcMieru.Gate.Tests;

/// <summary>The sharing rules of the gate (DESIGN-B 4): ordinary programs coexist, the swapper (exclusive) and the programs exclude each other, and nobody can delete, rename or write the file while it is open.</summary>
public sealed class GateSharingTests : IDisposable
{
    private readonly Scratch _scratch = new();

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void Two_shared_opens_coexist_and_so_do_many()
    {
        var exe = _scratch.Install();

        using var first = InstallGate.EnterStartup(exe);
        using var second = InstallGate.EnterStartup(exe);
        using var third = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.Open, first.Outcome);
        Assert.Equal(GateOutcome.Open, second.Outcome);
        Assert.Equal(GateOutcome.Open, third.Outcome);
        Assert.True(first.IsOpen && second.IsOpen && third.IsOpen);
        Assert.Equal(Scratch.GateOf(exe), first.GatePath);
    }

    [Fact]
    public void An_exclusive_open_while_a_shared_one_is_open_is_busy_and_it_works_when_the_last_shared_one_is_gone()
    {
        var exe = _scratch.Install();
        var first = InstallGate.EnterStartup(exe);
        var second = InstallGate.EnterStartup(exe);

        using (var swapper = InstallGate.EnterExclusive(exe))
        {
            Assert.Equal(GateOutcome.Busy, swapper.Outcome);
            Assert.False(swapper.IsOpen);
            Assert.Equal(InstallGate.ReasonOf(GateOutcome.Busy), swapper.Reason);
        }

        first.Dispose();
        Assert.Equal(GateOutcome.Busy, InstallGate.EnterExclusive(exe).Outcome); // one shared open is still there
        second.Dispose();

        using var exclusive = InstallGate.EnterExclusive(exe);
        Assert.Equal(GateOutcome.Open, exclusive.Outcome);
    }

    [Fact]
    public void A_shared_open_while_an_exclusive_one_is_open_is_busy_and_it_works_when_the_swapper_is_done()
    {
        var exe = _scratch.Install();
        var swapper = InstallGate.EnterExclusive(exe);
        Assert.True(swapper.IsOpen, swapper.Detail);

        using (var program = InstallGate.EnterStartup(exe))
        {
            Assert.Equal(GateOutcome.Busy, program.Outcome);
            Assert.False(program.IsOpen);
        }

        // Nothing but one swapper at a time.
        Assert.Equal(GateOutcome.Busy, InstallGate.EnterExclusive(exe).Outcome);

        swapper.Dispose();
        using var after = InstallGate.EnterStartup(exe);
        Assert.Equal(GateOutcome.Open, after.Outcome);
    }

    [Fact]
    public void A_refused_entry_holds_nothing_and_disposing_twice_is_safe()
    {
        var exe = _scratch.Install();
        var holder = InstallGate.EnterExclusive(exe);
        var refused = InstallGate.EnterStartup(exe);
        Assert.Equal(GateOutcome.Busy, refused.Outcome);
        refused.Dispose();
        refused.Dispose();

        holder.Dispose();
        holder.Dispose();

        using var again = InstallGate.EnterExclusive(exe);
        Assert.True(again.IsOpen); // neither the refused entry nor the double dispose left a handle behind
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void While_the_gate_is_open_the_file_cannot_be_deleted_renamed_replaced_or_opened_for_writing(bool exclusive)
    {
        var exe = _scratch.Install();
        var gate = Scratch.GateOf(exe);
        var other = Path.Combine(Path.GetDirectoryName(exe)!, "other.txt");
        File.WriteAllText(other, "another file");

        using (var entry = exclusive ? InstallGate.EnterExclusive(exe) : InstallGate.EnterStartup(exe))
        {
            Assert.True(entry.IsOpen);

            Refused(() => File.Delete(gate), "delete");
            Refused(() => File.Move(gate, gate + ".moved"), "rename");
            Refused(() => File.Move(other, gate, overwrite: true), "replace");
            Refused(() => File.WriteAllText(gate, "changed"), "write");
            Refused(() =>
            {
                using var stream = new FileStream(gate, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            }, "open for writing");
            Refused(() =>
            {
                // Even a program that would share everything with us cannot get a Delete handle in: the gate was opened without Delete sharing.
                using var stream = new FileStream(gate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose);
            }, "open for delete");

            if (exclusive)
            {
                Refused(() => File.ReadAllBytes(gate), "read (the swapper has it alone)");
            }
            else
            {
                Assert.Equal(GateFile.Bytes(), File.ReadAllBytes(gate)); // the other programs of the install read it, and nothing changed under them
            }
        }

        // Closed: the file is an ordinary file again (this is what lets the swapper work on the folder once every program has left).
        Assert.Equal(GateFile.Bytes(), File.ReadAllBytes(gate));
        File.Move(gate, gate + ".moved");
        Assert.True(File.Exists(gate + ".moved"));
    }

    [Fact]
    public void Other_readers_that_share_reading_can_still_read_the_file_while_it_is_open()
    {
        var exe = _scratch.Install();
        using var entry = InstallGate.EnterStartup(exe);
        Assert.True(entry.IsOpen);

        using var reader = new FileStream(Scratch.GateOf(exe), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(GateFile.Bytes().Length, reader.Length);
    }

    [Fact]
    public void The_handle_is_not_inherited_by_a_child_program_so_a_child_never_keeps_the_gate_after_the_parent_let_go()
    {
        var exe = _scratch.Install();
        var entry = InstallGate.EnterStartup(exe);
        Assert.True(entry.IsOpen);

        // stdout is redirected, which makes the system start the child with handle inheritance switched on: a handle that was inheritable would be inside it.
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("ping -n 6 127.0.0.1 > nul");
        using var child = Process.Start(info)!;
        try
        {
            entry.Dispose();
            Assert.False(child.HasExited, "the probe child ended too early to prove anything");

            using var swapper = InstallGate.EnterExclusive(exe);
            Assert.Equal(GateOutcome.Open, swapper.Outcome);
        }
        finally
        {
            try
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(10_000);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static void Refused(Action action, string what)
    {
        var ex = Record.Exception(action);
        Assert.True(ex is IOException or UnauthorizedAccessException, what + " must be refused while the gate is open, but it gave: " + (ex?.GetType().Name ?? "no exception"));
    }
}
