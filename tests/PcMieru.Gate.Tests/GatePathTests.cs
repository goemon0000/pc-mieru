using System.Security.AccessControl;
using PcMieru.Wire.Gate;

namespace PcMieru.Gate.Tests;

/// <summary>
/// Which gate is "the same install" is the file system's answer (volume serial and file id), not a path's, and what is refused is a link where the files are (DESIGN-B 4):
/// case spellings, a junction to the install folder and 8.3 names reach the same file; the gate itself, the update folder and the journal files being links are refused.
/// </summary>
public sealed class GatePathTests : IDisposable
{
    private readonly Scratch _scratch = new();
    private readonly ITestOutputHelper _output;

    public GatePathTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _scratch.Dispose();

    // ---- one file, however it is spelled ----

    [Fact]
    public void Another_spelling_of_the_case_of_the_path_is_the_same_gate()
    {
        var exe = _scratch.Install("Case Folder");
        using var holder = InstallGate.EnterStartup(exe);
        Assert.True(holder.IsOpen);

        var upper = exe.ToUpperInvariant();
        var lower = exe.ToLowerInvariant();
        Assert.NotEqual(exe, upper);

        Assert.Equal(GateOutcome.Busy, InstallGate.EnterExclusive(upper).Outcome);
        Assert.Equal(GateOutcome.Busy, InstallGate.EnterExclusive(lower).Outcome);
        using var shared = InstallGate.EnterStartup(upper);
        Assert.Equal(GateOutcome.Open, shared.Outcome);

        // and the other way round: the swapper under another spelling keeps the programs out
        holder.Dispose();
        shared.Dispose();
        using var swapper = InstallGate.EnterExclusive(lower);
        Assert.True(swapper.IsOpen, swapper.Detail);
        Assert.Equal(GateOutcome.Busy, InstallGate.EnterStartup(exe).Outcome);
        Assert.Equal(GateOutcome.Busy, InstallGate.EnterStartup(upper).Outcome);
    }

    [Fact]
    public void A_junction_to_the_install_folder_reaches_the_same_gate_in_both_directions()
    {
        var exe = _scratch.Install("real");
        var link = Path.Combine(_scratch.Root, "alias");
        _scratch.Junction(link, Path.GetDirectoryName(exe)!);
        var viaLink = Path.Combine(link, "PCMieru.exe");

        // a program started through the alias holds the real gate: the swapper (under the real name) is kept out
        using (var program = InstallGate.EnterStartup(viaLink))
        {
            Assert.Equal(GateOutcome.Open, program.Outcome);
            Assert.Equal(GateOutcome.Busy, InstallGate.EnterExclusive(exe).Outcome);
            Assert.Equal(GateOutcome.Busy, InstallGate.EnterExclusive(viaLink).Outcome);
            using var other = InstallGate.EnterStartup(exe);
            Assert.Equal(GateOutcome.Open, other.Outcome); // shared with shared
        }

        // a swapper that came in through the alias keeps the programs out, whichever name they use
        using (var swapper = InstallGate.EnterExclusive(viaLink))
        {
            Assert.Equal(GateOutcome.Open, swapper.Outcome);
            Assert.Equal(GateOutcome.Busy, InstallGate.EnterStartup(exe).Outcome);
            Assert.Equal(GateOutcome.Busy, InstallGate.EnterStartup(viaLink).Outcome);
        }

        using var free = InstallGate.EnterExclusive(exe);
        Assert.Equal(GateOutcome.Open, free.Outcome);
    }

    [ShortNameFact]
    public void The_8dot3_name_of_the_install_folder_reaches_the_same_gate()
    {
        var exe = _scratch.Install("a long folder name for the short one");
        var shortFolder = Probes.ShortPathOf(Path.GetDirectoryName(exe)!);
        Assert.NotNull(shortFolder);
        var viaShort = Path.Combine(shortFolder, "PCMieru.exe");
        _output.WriteLine("8.3 form used: " + viaShort);
        Assert.NotEqual(Path.GetDirectoryName(exe), shortFolder);

        using var holder = InstallGate.EnterStartup(viaShort);
        Assert.True(holder.IsOpen, holder.Detail);
        Assert.Equal(GateOutcome.Busy, InstallGate.EnterExclusive(exe).Outcome);
    }

    [Fact]
    public void A_path_that_is_not_fully_qualified_is_refused_it_is_never_completed_with_the_current_directory()
    {
        Assert.Throws<ArgumentException>(() => InstallGate.EnterStartup("PCMieru.exe"));
        Assert.Throws<ArgumentException>(() => InstallGate.EnterStartup(@"app\PCMieru.exe"));
        Assert.Throws<ArgumentException>(() => InstallGate.EnterExclusive("PCMieru.exe"));
        Assert.Throws<ArgumentException>(() => InstallGate.GatePathOf("PCMieru.exe"));
    }

    // ---- not there, and not readable: told apart ----

    [Fact]
    public void A_folder_without_the_gate_file_is_a_missing_gate_and_nothing_is_made_there()
    {
        var exe = _scratch.Install("nogate", gate: false);

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.MissingGate, entry.Outcome);
        Assert.False(File.Exists(Scratch.GateOf(exe)));
        Assert.False(Directory.Exists(Scratch.UpdateOf(exe)));
    }

    [Fact]
    public void A_folder_that_does_not_exist_is_a_missing_gate()
    {
        using var entry = InstallGate.EnterStartup(Path.Combine(_scratch.Root, "no such folder", "PCMieru.exe"));

        Assert.Equal(GateOutcome.MissingGate, entry.Outcome);
    }

    [Fact]
    public void A_gate_that_is_a_folder_is_a_missing_gate()
    {
        var exe = _scratch.Install("folder-gate", gate: false);
        Directory.CreateDirectory(Scratch.GateOf(exe));

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.MissingGate, entry.Outcome);
    }

    [Fact]
    public void A_gate_without_an_update_folder_opens()
    {
        var exe = _scratch.Install();

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.Open, entry.Outcome);
        Assert.Null(entry.RecoveryNotePath);
    }

    [Fact]
    public void An_update_folder_with_nothing_of_a_transaction_in_it_opens()
    {
        var exe = _scratch.Install();
        var update = Directory.CreateDirectory(Scratch.UpdateOf(exe));
        Directory.CreateDirectory(Path.Combine(update.FullName, "staging"));
        File.WriteAllText(Path.Combine(update.FullName, "journal.json.bak"), "an old copy, not a journal");
        File.WriteAllText(Path.Combine(update.FullName, "notes.txt"), "text");

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.Open, entry.Outcome);
    }

    [Fact]
    public void A_gate_that_cannot_be_read_is_denied_and_not_missing()
    {
        var exe = _scratch.Install();
        _scratch.Deny(Scratch.GateOf(exe), FileSystemRights.ReadData);

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.Denied, entry.Outcome);
        Assert.Equal(InstallGate.ReasonOf(GateOutcome.Denied), entry.Reason);
        Assert.Contains("access denied", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gate_that_cannot_even_be_looked_at_is_denied_and_not_missing()
    {
        var exe = _scratch.Install();
        var folder = Path.GetDirectoryName(exe)!;
        _scratch.Deny(Scratch.GateOf(exe), FileSystemRights.ReadAttributes | FileSystemRights.ReadData);
        _scratch.Deny(folder, FileSystemRights.ListDirectory);

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.Denied, entry.Outcome);
    }

    [Fact]
    public void Every_outcome_has_its_own_fixed_english_reason_without_a_path_in_it()
    {
        var reasons = Enum.GetValues<GateOutcome>().Select(InstallGate.ReasonOf).ToList();

        Assert.Equal(reasons.Count, reasons.Distinct().Count());
        foreach (var reason in reasons)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.DoesNotContain('\\', reason);
            Assert.All(reason, c => Assert.True(c < 128, "the reason is plain English: " + reason));
        }
    }

    // ---- links where the files are ----

    [Fact]
    public void A_gate_that_is_a_junction_is_refused()
    {
        var exe = _scratch.Install("linked-gate", gate: false);
        var elsewhere = _scratch.Sub("elsewhere");
        _scratch.Junction(Scratch.GateOf(exe), elsewhere);

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.ReparseRefused, entry.Outcome);
        Assert.Contains("link", entry.Detail, StringComparison.Ordinal);
    }

    [SymlinkFact]
    public void A_gate_that_is_a_symbolic_link_to_a_real_file_is_refused()
    {
        var exe = _scratch.Install("symlinked-gate", gate: false);
        var target = Path.Combine(_scratch.Root, "the-real-gate.txt");
        File.WriteAllBytes(target, GateFile.Bytes());
        File.CreateSymbolicLink(Scratch.GateOf(exe), target);
        _scratch.OnDispose(() => File.Delete(Scratch.GateOf(exe)));

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.ReparseRefused, entry.Outcome);
        // the target was not opened by the gate: it is free for anyone
        using var exclusive = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None);
    }

    [Fact]
    public void An_update_folder_that_is_a_junction_is_refused_even_when_nothing_is_in_it()
    {
        var exe = _scratch.Install();
        var elsewhere = _scratch.Sub("elsewhere");
        _scratch.Junction(Scratch.UpdateOf(exe), elsewhere);

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.ReparseRefused, entry.Outcome);
        Assert.Null(entry.RecoveryNotePath);
        using var swapper = InstallGate.EnterExclusive(exe);
        Assert.True(swapper.IsOpen, "a refused entry holds nothing: " + swapper.Detail);
    }

    [Theory]
    [InlineData("journal.json")]
    [InlineData("journal.json.tmp")]
    public void A_journal_file_that_is_a_junction_is_refused(string name)
    {
        var exe = _scratch.Install();
        Directory.CreateDirectory(Scratch.UpdateOf(exe));
        var elsewhere = _scratch.Sub("elsewhere");
        _scratch.Junction(Path.Combine(Scratch.UpdateOf(exe), name), elsewhere);

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.ReparseRefused, entry.Outcome);
    }

    // ---- a manual unzip that cannot be written ----

    [Fact]
    public void A_gate_file_and_a_folder_with_the_read_only_attribute_still_open()
    {
        var exe = _scratch.Install();
        var gate = Scratch.GateOf(exe);
        File.SetAttributes(gate, FileAttributes.ReadOnly);
        File.SetAttributes(Path.GetDirectoryName(exe)!, FileAttributes.ReadOnly | FileAttributes.Directory);
        _scratch.OnDispose(() => File.SetAttributes(Path.GetDirectoryName(exe)!, FileAttributes.Directory));

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.Open, entry.Outcome);
    }

    [Fact]
    public void A_folder_that_cannot_be_written_still_opens_the_gate_because_the_gate_is_only_read()
    {
        var exe = _scratch.Install();
        var folder = Path.GetDirectoryName(exe)!;
        _scratch.Deny(folder, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories);
        _scratch.Deny(Scratch.GateOf(exe), FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes | FileSystemRights.Delete);
        Assert.ThrowsAny<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(folder, "probe.txt"), "x")); // it really is read-only for this user now

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.Open, entry.Outcome);
        using var second = InstallGate.EnterStartup(exe);
        Assert.Equal(GateOutcome.Open, second.Outcome);
    }
}
