using System.Text;
using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Tests;

/// <summary>
/// <c>I\update\recover.exe</c>: who may run it (<see cref="RunnerHandOff.Check"/>, read-only) and how it gets there (<see cref="RunnerHandOff.Prepare"/>, used by the installed Swap.exe), and the text of
/// <c>RECOVER.txt</c> beside it. Scratch folders only; the "exe" is the synthetic Swap.exe of the test kit (random bytes), which is never started here.
/// </summary>
public sealed class RunnerHandOffTests
{
    private static byte[] OldSwap => SyntheticSet.Bytes("swap-old", 2000);

    private static byte[] NewSwap => SyntheticSet.Bytes("swap-new", 2100);

    private static void PlaceRecover(Rig rig, byte[] bytes)
    {
        Directory.CreateDirectory(rig.Paths.Update);
        File.WriteAllBytes(rig.Paths.Recover, bytes);
    }

    // ---------------------------------------------------------------------------------------------------------------------------- Check

    [Fact]
    public void Check_says_not_trusted_when_there_is_no_recover_exe_and_writes_nothing()
    {
        using var rig = Rig.Create("ho-check-none", recorded: true);
        var before = Rig.Snapshot(rig.Install, includeUpdate: true);

        var result = RunnerHandOff.Check(rig.Install);

        Assert.Equal(HandOffStatus.RunnerNotTrusted, result.Status);
        Assert.False(result.IsReady);
        rig.AssertUnchanged(before, "Check is read-only", includeUpdate: true);
    }

    [Fact]
    public void Check_is_ready_for_the_recorded_swap_exe_and_for_nothing_else()
    {
        using var rig = Rig.Create("ho-check-hash", recorded: true);
        PlaceRecover(rig, OldSwap);
        Assert.Equal(HandOffStatus.Ready, RunnerHandOff.Check(rig.Install).Status);

        PlaceRecover(rig, NewSwap); // a file that is the NEXT generation's Swap.exe is not the recorded one
        Assert.Equal(HandOffStatus.RunnerNotTrusted, RunnerHandOff.Check(rig.Install).Status);

        PlaceRecover(rig, [.. OldSwap, 0]); // one byte longer
        Assert.Equal(HandOffStatus.RunnerNotTrusted, RunnerHandOff.Check(rig.Install).Status);

        var changed = OldSwap;
        changed[1000] ^= 0xff; // the same length, one byte different
        PlaceRecover(rig, changed);
        Assert.Equal(HandOffStatus.RunnerNotTrusted, RunnerHandOff.Check(rig.Install).Status);
    }

    [Fact]
    public void Check_says_no_record_when_nothing_names_a_swap_exe()
    {
        using var rig = Rig.Create("ho-check-norec"); // not recorded: no installed.json, and no journal
        PlaceRecover(rig, OldSwap);
        Assert.Equal(HandOffStatus.NoRecord, RunnerHandOff.Check(rig.Install).Status);
    }

    [Fact]
    public void Check_refuses_an_update_folder_that_is_a_link()
    {
        using var rig = Rig.Create("ho-check-link", recorded: true);
        var target = Path.Combine(rig.Root, "elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "recover.exe"), OldSwap);
        Directory.Delete(rig.Paths.Update, recursive: true);
        Rig.Junction(rig.Paths.Update, target);
        try
        {
            Assert.Equal(HandOffStatus.UpdateFolderUnsafe, RunnerHandOff.Check(rig.Install).Status);
            Assert.Equal(HandOffStatus.UpdateFolderUnsafe, RunnerHandOff.Prepare(rig.Install).Status);
            Assert.Equal(OldSwap, File.ReadAllBytes(Path.Combine(target, "recover.exe"))); // nothing was written through the link
        }
        finally
        {
            Directory.Delete(rig.Paths.Update); // the link only
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------- Prepare

    [Fact]
    public void Prepare_copies_the_recorded_swap_exe_to_recover_exe_and_nothing_else_changes()
    {
        using var rig = Rig.Create("ho-prep", recorded: true);
        var before = Rig.Snapshot(rig.Install);

        var result = RunnerHandOff.Prepare(rig.Install);

        Assert.Equal(HandOffStatus.Ready, result.Status);
        Assert.Equal(OldSwap, File.ReadAllBytes(rig.Paths.Recover));
        Assert.False(File.Exists(rig.Paths.Recover + ".new"), "the temporary copy is left");
        rig.AssertUnchanged(before, "Prepare changes only the update folder");
        Assert.Equal(HandOffStatus.Ready, RunnerHandOff.Check(rig.Install).Status);
    }

    [Fact]
    public void Prepare_takes_the_record_from_the_manifest_of_a_folder_that_was_unzipped_by_hand()
    {
        using var rig = Rig.Create("ho-prep-hand"); // no installed.json: the folder holds PCMieru.bundle.json only
        Assert.False(File.Exists(rig.Paths.Installed));

        Assert.Equal(HandOffStatus.Ready, RunnerHandOff.Prepare(rig.Install).Status);
        Assert.Equal(OldSwap, File.ReadAllBytes(rig.Paths.Recover));
    }

    [Fact]
    public void Prepare_replaces_a_recover_exe_that_is_not_the_recorded_file_when_no_update_is_open()
    {
        using var rig = Rig.Create("ho-prep-stale", recorded: true);
        PlaceRecover(rig, NewSwap);

        Assert.Equal(HandOffStatus.Ready, RunnerHandOff.Prepare(rig.Install).Status);
        Assert.Equal(OldSwap, File.ReadAllBytes(rig.Paths.Recover));
    }

    [Fact]
    public void Prepare_does_not_copy_a_swap_exe_that_is_not_the_recorded_file()
    {
        using var rig = Rig.Create("ho-prep-bad", recorded: true);
        var damaged = OldSwap;
        damaged[10] ^= 0x55;
        File.WriteAllBytes(Path.Combine(rig.Install, "PCMieru.Swap.exe"), damaged);

        var result = RunnerHandOff.Prepare(rig.Install);

        Assert.Equal(HandOffStatus.SwapExeNotRecorded, result.Status);
        Assert.False(File.Exists(rig.Paths.Recover), "a damaged Swap.exe must never become recover.exe");
        Assert.False(File.Exists(rig.Paths.Recover + ".new"));
    }

    [Fact]
    public void Prepare_says_so_when_the_record_has_no_swap_exe_or_there_is_no_record()
    {
        using var noLine = Rig.Create("ho-prep-noline", recorded: true, shape: (old, _) => old.Files.Remove("PCMieru.Swap.exe"));
        Assert.Equal(HandOffStatus.SwapExeNotRecorded, RunnerHandOff.Prepare(noLine.Install).Status);
        Assert.False(File.Exists(noLine.Paths.Recover));

        using var none = Rig.Create("ho-prep-norec");
        File.Delete(Path.Combine(none.Install, "PCMieru.bundle.json"));
        Assert.Equal(HandOffStatus.NoRecord, RunnerHandOff.Prepare(none.Install).Status);
        Assert.False(File.Exists(none.Paths.Recover));
    }

    [Fact]
    public void Prepare_never_replaces_recover_exe_while_an_update_is_open()
    {
        using var rig = Rig.Create("ho-prep-open", recorded: true);
        var cutter = new Cutter(new CutPoint(TxSteps.SwapAdd, TxEdge.After, 1));
        Assert.Throws<PowerCutException>(() => rig.Swap(Rig.Seams(cutter, FakeLauncher.Ok())));
        Assert.True(File.Exists(rig.Paths.Journal));
        Assert.Equal(OldSwap, File.ReadAllBytes(rig.Paths.Recover)); // placed before the first change

        // The journal's "to" side is not a trusted source: the NEXT generation's Swap.exe was never verified as installed.
        PlaceRecover(rig, NewSwap);
        var result = RunnerHandOff.Prepare(rig.Install);
        Assert.Equal(HandOffStatus.RunnerNotTrusted, result.Status);
        Assert.Equal(NewSwap, File.ReadAllBytes(rig.Paths.Recover)); // not replaced: the recovery may need exactly what is there

        PlaceRecover(rig, OldSwap);
        Assert.Equal(HandOffStatus.Ready, RunnerHandOff.Prepare(rig.Install).Status);
    }

    [Fact]
    public void The_journals_from_side_vouches_for_the_old_copy_after_installed_json_names_the_new_swap_exe()
    {
        using var rig = Rig.Create("ho-prep-from", recorded: true);
        var cutter = new Cutter(new CutPoint(TxSteps.SwapInstalled, TxEdge.After, 1));
        Assert.Throws<PowerCutException>(() => rig.Swap(Rig.Seams(cutter, FakeLauncher.Ok())));

        // installed.json now records the NEW Swap.exe, the journal's from side the old one, and recover.exe is the old copy: trusted through the journal.
        var installed = File.ReadAllBytes(rig.Paths.Installed);
        Assert.True(InstalledRecord.TryParse(installed, out var record, out _));
        Assert.True(record.TryGet("PCMieru.Swap.exe", out var line));
        Assert.Equal(Rig.Sha(NewSwap), line.Sha256);
        Assert.Equal(OldSwap, File.ReadAllBytes(rig.Paths.Recover));
        Assert.Equal(HandOffStatus.Ready, RunnerHandOff.Check(rig.Install).Status);
        Assert.Equal(HandOffStatus.Ready, RunnerHandOff.Prepare(rig.Install).Status);
    }

    [Fact]
    public void Prepare_does_not_touch_recover_exe_while_the_failed_recovery_sentinel_is_there()
    {
        using var rig = Rig.Create("ho-prep-sentinel", recorded: true);
        PlaceRecover(rig, NewSwap);
        File.WriteAllBytes(rig.Paths.Sentinel, GateFile.Bytes());

        var result = RunnerHandOff.Prepare(rig.Install);

        Assert.Equal(HandOffStatus.RunnerNotTrusted, result.Status);
        Assert.Equal(NewSwap, File.ReadAllBytes(rig.Paths.Recover));
    }

    // ---------------------------------------------------------------------------------------------------------------------------- RECOVER.txt

    [Fact]
    public void The_recovery_note_is_plain_japanese_and_english_with_the_one_thing_to_run_and_no_address()
    {
        var bytes = RecoverNote.Bytes("0.5.0", "0.5.1");

        Assert.True(bytes.AsSpan().StartsWith(new UTF8Encoding(true).GetPreamble()), "the note has a byte order mark (older Notepads read the Japanese by it)");
        var text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        Assert.Equal(text, RecoverNote.Text("0.5.0", "0.5.1"));
        Assert.Contains("[日本語]", text, StringComparison.Ordinal);
        Assert.Contains("[English]", text, StringComparison.Ordinal);
        Assert.Contains("recover.exe", text, StringComparison.Ordinal);
        Assert.Contains("0.5.0 -> 0.5.1", text, StringComparison.Ordinal);
        Assert.Contains("新しいフォルダー", text, StringComparison.Ordinal); // the fallback: unzip the downloaded ZIP into a new folder
        Assert.Contains("unzip the ZIP file you downloaded into a new folder", text, StringComparison.Ordinal);
        Assert.DoesNotContain("://", text, StringComparison.Ordinal);
        Assert.DoesNotContain("http", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("www.", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"(?<!\r)\n", text); // every line ends CRLF
        Assert.DoesNotContain("-> ", RecoverNote.Text("0.5.0", null), StringComparison.Ordinal); // no target version: only the one it started from
    }

    [Fact]
    public void The_recovery_note_says_what_recovery_does_and_leaves_alone_and_not_to_run_it_as_administrator()
    {
        var text = RecoverNote.Text("0.5.0", "0.5.1");
        var ja = text[..text.IndexOf("[English]", StringComparison.Ordinal)];
        var en = text[text.IndexOf("[English]", StringComparison.Ordinal)..];

        // What it does: one whole version, either one. What it never touches: a file it did not install. Where the person's own things are: outside this folder.
        Assert.Contains("更新前または更新後の、そろった状態に戻します", ja, StringComparison.Ordinal);
        Assert.DoesNotContain("更新前か更新後のどちらか", ja, StringComparison.Ordinal);
        Assert.Contains("写真や文書など、PCミエルが入れたもの以外のファイルは変わりません。", ja, StringComparison.Ordinal);
        Assert.Contains("設定や記録はこのフォルダーの外にあるので", ja, StringComparison.Ordinal);
        Assert.Contains("one complete version, either the one from before the update or the new one", en, StringComparison.Ordinal);
        Assert.Contains("Files that PC Mieru did not install, such as photos and documents, are not changed.", en, StringComparison.Ordinal);
        Assert.Contains("kept outside this folder", en, StringComparison.Ordinal);

        // The plain steps and where the ZIP is, the same words as the start gate window (StartGateScreenTests checks the two against each other).
        Assert.Contains("いつもの方法（デスクトップやスタートメニューの PCミエル）で開きます", ja, StringComparison.Ordinal);
        Assert.Contains("Open PC Mieru the usual way (from the desktop or the Start menu).", en, StringComparison.Ordinal);
        Assert.Contains("PCミエルの ZIP は、ふつうは「ダウンロード」フォルダーにあります。見つからなければ、もう一度ダウンロードしてください。", ja, StringComparison.Ordinal);
        Assert.Contains("The PC Mieru ZIP is usually in your Downloads folder. If you cannot find it, download it again.", en, StringComparison.Ordinal);

        // A recovery runs with normal rights only (recover.exe refuses an elevated token): the note says not to ask for administrator rights, and still offers the new folder.
        Assert.Contains("「管理者として実行」は使わないでください", ja, StringComparison.Ordinal);
        Assert.Contains("Do not use \"Run as administrator\"", en, StringComparison.Ordinal);
        Assert.Contains("新しいフォルダーに展開", ja, StringComparison.Ordinal);
        Assert.Contains("into a new folder", en, StringComparison.Ordinal);

        // It no longer promises what recovery cannot promise ("tidies up", "fixes it").
        Assert.DoesNotContain("直らない", text, StringComparison.Ordinal);
        Assert.DoesNotContain("does not fix it", text, StringComparison.Ordinal);
        // Plain words: no file, folder or tool is named except the one thing to run, and no number but the two versions.
        Assert.DoesNotContain("journal", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("installed.json", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gate", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_swap_writes_the_note_before_its_first_change_and_the_gate_points_at_it()
    {
        using var rig = Rig.Create("ho-note", recorded: true);
        var cutter = new Cutter(new CutPoint(TxSteps.JournalPrepared, TxEdge.After, 1));
        Assert.Throws<PowerCutException>(() => rig.Swap(Rig.Seams(cutter, FakeLauncher.Ok())));

        Assert.Equal(RecoverNote.Bytes("0.5.0", "0.5.1"), File.ReadAllBytes(rig.Paths.Note));
        rig.AssertTree(Generation.Old, "the note comes before any change");
        using var start = InstallGate.EnterStartup(rig.ExePath);
        Assert.Equal(GateOutcome.RecoveryPending, start.Outcome);
        Assert.Equal(rig.Paths.Note, start.RecoveryNotePath);
        Assert.Equal(GateFile.RecoverNoteName, Path.GetFileName(start.RecoveryNotePath));
    }
}
