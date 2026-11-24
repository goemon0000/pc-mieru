using System.Text.Json;
using PcMieru.Update.Bundle;
using PcMieru.Update.Feed;
using PcMieru.Update.Launch;
using PcMieru.Update.Staging;
using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Tests;

/// <summary>An in-memory <see cref="IMessageChannel"/>: lines to hand out, lines that were sent.</summary>
internal sealed class ScriptedChannel(params string[] incoming) : IMessageChannel
{
    private readonly Queue<string> _incoming = new(incoming);

    public List<string> Sent { get; } = [];

    public bool SendFails { get; set; }

    public bool Disposed { get; private set; }

    public bool IsClosed => Disposed;

    public bool Send(string line)
    {
        if (SendFails || Disposed)
        {
            return false;
        }

        Sent.Add(line);
        return true;
    }

    public bool TryReceive(TimeSpan timeout, out string line)
    {
        if (!Disposed && _incoming.TryDequeue(out var next))
        {
            line = next;
            return true;
        }

        line = string.Empty;
        return false;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>An install (the old generation, recorded), a scratch L with gui.ini, and a stage of the next generation in <c>L\update\&lt;id&gt;\cache</c>: what <c>recover.exe --apply</c> looks at.</summary>
internal sealed class ApplyRig : IDisposable
{
    /// <summary>Files of the user's own that are in L (planted before, and byte-identical after, whatever the update does).</summary>
    public static readonly string[] UserFilesInLocal = ["settings/mine.json", "records/a.bin"];

    public ApplyRig(string label, string? mode = "auto", Action<SyntheticSet, SyntheticSet>? shape = null, string? profile = null)
    {
        Rig = Rig.Create(label, recorded: true, shape);
        Local = profile is null ? Scratch.NewFolder(label + "-L") : Path.Combine(profile, "PCMieru");
        foreach (var name in UserFilesInLocal)
        {
            var path = Path.Combine(Local, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, SyntheticSet.Bytes(name, 777));
        }

        State = InstallId.StateFolder(Local, Rig.ExePath) ?? throw new InvalidOperationException("the rig's install has no id");
        Staged = MakeStage(Rig.New);
        if (mode is not null)
        {
            File.WriteAllText(GuiIni, "unrelated=1\nupdate.mode=" + mode + "\n");
        }
    }

    public Rig Rig { get; }

    public string Local { get; }

    public string State { get; }

    public StagedUpdate Staged { get; set; }

    public string GuiIni => Path.Combine(Local, "gui.ini");

    public ApplyMessage Message => ApplyMessage.Of(Staged);

    public StagedUpdate MakeStage(SyntheticSet set, string tx = "0123456789ab")
    {
        var folder = Path.Combine(StageLayout.CacheOf(State), tx);
        set.WriteTo(Path.Combine(folder, StageLayout.FilesFolderName));
        var manifest = new BundleManifest(set.Version, set.BuildId, set.AllFiles().Select(f => TestBundle.Line(f.Key, f.Value))).ToUtf8Bytes();
        File.WriteAllBytes(Path.Combine(folder, StageLayout.ManifestFileName), manifest);
        return new StagedUpdate { Version = set.Version, BuildId = set.BuildId, Tag = "v" + set.Version, Folder = folder, ManifestSha256 = Rig.Sha(manifest) };
    }

    public string OuterManifest => Path.Combine(Staged.Folder, StageLayout.ManifestFileName);

    public string InnerManifest => Path.Combine(Staged.FilesFolder(), BundleFormat.InnerManifestName);

    public void Block(string version, string buildId)
    {
        Directory.CreateDirectory(Rig.Paths.Update);
        File.WriteAllBytes(Rig.Paths.Blocked, BlockedTags.Bytes([], new BlockedTag(version, buildId, TxTag.LaunchFailed, "2026-11-22T01:02:03Z")));
    }

    public (SortedDictionary<string, string> Install, SortedDictionary<string, string> Local) Snapshot() =>
        (Rig.Snapshot(Rig.Install, includeUpdate: true), Rig.Snapshot(Local, includeUpdate: true));

    public void AssertUnchanged((SortedDictionary<string, string> Install, SortedDictionary<string, string> Local) before, string context)
    {
        Rig.AssertUnchanged(before.Install, context, includeUpdate: true);
        Assert.Equal(before.Local, Rig.Snapshot(Local, includeUpdate: true));
    }

    public (string Outcome, string Reason)? LastResultOnDisk()
    {
        var path = Rig.Paths.LastResult;
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return (document.RootElement.GetProperty("outcome").GetString()!, document.RootElement.GetProperty("reason").GetString()!);
    }

    public void Dispose()
    {
        Rig.Dispose();
        Scratch.Delete(Local);
    }
}

/// <summary>
/// <c>recover.exe --apply</c> (DESIGN-B 3.3 and 5), in-process on a scratch install with a fake channel and a fake launch check: the handles, the message, every refusal, the waits, the second look from the disk after the
/// program closed, and what is started after each end. The real processes (the pipe, the handles that are passed down, the real launch check) are in LaunchProcessTests.
/// </summary>
public sealed class SwapApplyTests
{
    private const string Pipe = "1234";
    private const string Process = "5678";

    private sealed class Harness(ApplyRig rig)
    {
        public ScriptedChannel Channel { get; set; } = new(rig.Message.ToLine());

        public StringWriter Out { get; } = new();

        public StringWriter Err { get; } = new();

        public FakeStarter Starter { get; } = new();

        public bool Elevated { get; set; }

        public bool GuiEnds { get; set; } = true;

        public Action? OnGuiEnd { get; set; }

        public ILaunchVerifier? Launcher { get; set; }

        public Action<GateEntry>? OnLauncherMade { get; set; }

        public bool NoEndpoint { get; set; }

        public bool AnotherGui { get; set; }

        public IFaultInjector? Fault { get; set; }

        public int Opened { get; private set; }

        public int Released { get; private set; }

        public List<TimeSpan> GuiWaits { get; } = [];

        public string Self { get; set; } = rig.Rig.Paths.Recover;

        public string LocalFolder { get; set; } = rig.Local;

        /// <summary>What the host says to start I\PCMieru.exe with at the end: nothing, like the product's runner, unless a test (standing for the test build of the runner) says otherwise.</summary>
        public IReadOnlyList<string> ProgramArguments { get; set; } = [];

        public int Run(params string[] args) => SwapCli.Run(args.Length == 0 ? ["--apply", Pipe, Process] : args, Host());

        private SwapHost Host() => new()
        {
            SelfPath = Self,
            Out = Out,
            Error = Err,
            IsElevated = () => Elevated,
            Seams = Rig.Seams(Fault),
            Starter = Starter,
            Sleep = static _ => { },
            ConsoleIsAlone = static () => false,
            SwapGateWait = TimeSpan.FromSeconds(1),
            HasGuiGateHolder = _ => AnotherGui,
            ReadingPause = TimeSpan.Zero,
            LocalFolder = LocalFolder,
            ProgramArguments = ProgramArguments,
            OpenApplyEndpoint = (_, _) =>
            {
                Opened++;
                return NoEndpoint ? null : new ApplyEndpoint(Channel, limit =>
                {
                    GuiWaits.Add(limit);
                    OnGuiEnd?.Invoke();
                    return GuiEnds;
                }, () => Released++);
            },
            LauncherFor = gate =>
            {
                OnLauncherMade?.Invoke(gate);
                return Launcher ?? throw new InvalidOperationException("this test did not expect a launch check");
            },
        };
    }

    private static void AssertStartedOnce(Harness run, ApplyRig rig)
    {
        var start = Assert.Single(run.Starter.Starts);
        Assert.Equal(Path.Combine(InstallGate.InstallFolderOf(rig.Rig.ExePath), "PCMieru.exe"), start.Exe); // the product names the install by its final path (8.3 names long)
        Assert.Empty(start.Arguments);
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the command line

    [Theory]
    [InlineData("--apply")]
    [InlineData("--apply|1234")]
    [InlineData("--apply|1234|5678|9")]
    [InlineData("--apply|0|5678")]
    [InlineData("--apply|1234|0")]
    [InlineData("--apply|abc|5678")]
    [InlineData("--apply|-5|5678")]
    [InlineData("--apply|0x10|5678")]
    [InlineData("--apply|1234|1234")]
    [InlineData("--apply|4294967296|5678")]
    [InlineData("--apply|1234|5678|--then-start")]
    [InlineData("--apply|1234|5678|--after-pid|44")]
    public void A_command_line_that_is_not_exactly_the_option_and_two_handles_changes_nothing(string args)
    {
        using var rig = new ApplyRig("apply-cli");
        var before = rig.Snapshot();
        var run = new Harness(rig);

        Assert.Equal(SwapExit.BadCommandLine, run.Run(args.Split('|')));

        Assert.Equal(0, run.Opened); // not even the handles were looked at
        Assert.Empty(run.Starter.Starts);
        Assert.Empty(run.Channel.Sent);
        rig.AssertUnchanged(before, "a bad command line");
    }

    [Fact]
    public void Apply_is_for_recover_exe_only_the_launcher_does_not_apply_and_another_place_is_refused()
    {
        using var rig = new ApplyRig("apply-roles");
        var before = rig.Snapshot();

        var asLauncher = new Harness(rig) { Self = Path.Combine(rig.Rig.Install, "PCMieru.Swap.exe") };
        Assert.Equal(SwapExit.BadCommandLine, asLauncher.Run());
        Assert.Equal(0, asLauncher.Opened);

        var elsewhere = new Harness(rig) { Self = Path.Combine(rig.Local, "recover.exe") };
        Assert.Equal(SwapExit.WrongPlace, elsewhere.Run());
        Assert.Equal(0, elsewhere.Opened);

        Assert.Empty(asLauncher.Starter.Starts);
        Assert.Empty(elsewhere.Starter.Starts);
        rig.AssertUnchanged(before, "the wrong role or place");
    }

    [Fact]
    public void Handles_that_are_not_a_pipe_and_a_process_change_nothing_and_exit_with_a_bad_command_line()
    {
        using var rig = new ApplyRig("apply-nopipe");
        var before = rig.Snapshot();
        var run = new Harness(rig) { NoEndpoint = true };

        Assert.Equal(SwapExit.BadCommandLine, run.Run());

        Assert.Equal(1, run.Opened);
        Assert.Contains("--apply", run.Err.ToString(), StringComparison.Ordinal);
        Assert.Empty(run.Starter.Starts);
        rig.AssertUnchanged(before, "no usable handles");
    }

    [Fact]
    public void The_real_handle_checks_refuse_a_value_that_is_not_a_pipe_or_not_a_process()
    {
        // The product's own seam, not a fake: this process's console handle values and plain numbers are neither.
        Assert.Null(ApplyEndpoint.FromInherited(7, 9));
        Assert.Null(ApplyEndpoint.FromInherited(0, 0));
        Assert.True(LaunchNative.TryCreatePipe(out var server, out var client));
        using (server)
        using (client)
        {
            // a pipe, but no process
            Assert.Null(ApplyEndpoint.FromInherited(client.DangerousGetHandle(), 9));
            Assert.Null(ApplyEndpoint.FromInherited(client.DangerousGetHandle(), client.DangerousGetHandle()));
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the token

    [Fact]
    public void An_elevated_runner_answers_that_it_is_elevated_and_changes_and_starts_nothing()
    {
        using var rig = new ApplyRig("apply-elevated");
        var before = rig.Snapshot();
        var run = new Harness(rig) { Elevated = true };

        Assert.Equal(SwapExit.Elevated, run.Run());

        Assert.Equal([ApplyAnswer.Refuse(ApplyRefusal.Elevated)], run.Channel.Sent);
        Assert.Empty(run.Starter.Starts);
        Assert.Empty(run.GuiWaits);
        Assert.Equal(1, run.Released);
        Assert.Contains("result Refused Elevated exit=32", run.Out.ToString(), StringComparison.Ordinal);
        rig.AssertUnchanged(before, "an elevated runner");
        Assert.Null(rig.LastResultOnDisk());
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the message

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("pcm-apply/2\tC:\\x\t0\t0.5.1\t22222222-2222-4222-8222-222222222222\tv0.5.1")]
    [InlineData("pcm-apply/1\tC:\\x")]
    public void A_message_that_is_missing_or_not_understood_is_refused_and_changes_nothing(string? line)
    {
        using var rig = new ApplyRig("apply-badmsg");
        var before = rig.Snapshot();
        var run = new Harness(rig) { Channel = line is null ? new ScriptedChannel() : new ScriptedChannel(line) };

        Assert.Equal(SwapExit.BadCommandLine, run.Run());

        Assert.Equal([ApplyAnswer.Refuse(ApplyRefusal.BadMessage)], run.Channel.Sent);
        Assert.Empty(run.Starter.Starts);
        Assert.Empty(run.GuiWaits);
        Assert.Equal(1, run.Released);
        rig.AssertUnchanged(before, "a bad message");
        Assert.Null(rig.LastResultOnDisk());
    }

    // ---------------------------------------------------------------------------------------------------------------------------- what is looked at on disk

    private static void AssertRefused(Harness run, ApplyRig rig, ApplyRefusal why, (SortedDictionary<string, string> Install, SortedDictionary<string, string> Local) before)
    {
        Assert.Equal(SwapExit.Aborted, run.Run());
        Assert.Equal([ApplyAnswer.Refuse(why)], run.Channel.Sent);
        Assert.Contains($"result Refused {why} exit=12", run.Out.ToString(), StringComparison.Ordinal);
        Assert.Empty(run.GuiWaits); // the program is told no and stays open: nothing is waited for
        Assert.Empty(run.Starter.Starts); // it never closed
        Assert.Equal(1, run.Released);
        Assert.Null(rig.LastResultOnDisk()); // the program is still open and says why itself
        rig.AssertUnchanged(before, "a refusal before the answer");
    }

    [Fact]
    public void A_stage_of_another_install_or_anywhere_else_is_not_found()
    {
        using var rig = new ApplyRig("apply-otherstage");
        var elsewhere = Path.Combine(Scratch.NewFolder("apply-elsewhere"), "cache", "0123456789ab");
        try
        {
            var set = rig.Rig.New;
            set.WriteTo(Path.Combine(elsewhere, "files"));
            var other = Path.Combine(rig.Local, "update", "deadbeef-0123456789abcdef", "cache", "0123456789ab");
            set.WriteTo(Path.Combine(other, "files"));
            foreach (var folder in new[] { elsewhere, other })
            {
                File.Copy(rig.OuterManifest, Path.Combine(folder, StageLayout.ManifestFileName));
                var before = rig.Snapshot();
                var run = new Harness(rig) { Channel = new ScriptedChannel((rig.Message with { Stage = folder }).ToLine()) };
                AssertRefused(run, rig, ApplyRefusal.StageNotFound, before);
            }
        }
        finally
        {
            Scratch.Delete(Path.GetDirectoryName(Path.GetDirectoryName(elsewhere))!);
        }
    }

    [Fact]
    public void A_stage_folder_that_is_not_there_is_a_changed_stage()
    {
        using var rig = new ApplyRig("apply-nostage");
        Directory.Delete(rig.Staged.Folder, recursive: true);
        var before = rig.Snapshot();
        AssertRefused(new Harness(rig), rig, ApplyRefusal.StageChanged, before);
    }

    [Fact]
    public void A_manifest_hash_that_is_not_the_hash_of_the_manifest_on_disk_is_a_changed_stage()
    {
        using var rig = new ApplyRig("apply-wronghash");
        var before = rig.Snapshot();
        var wrong = rig.Message with { ManifestSha256 = new string('0', 64) };
        AssertRefused(new Harness(rig) { Channel = new ScriptedChannel(wrong.ToLine()) }, rig, ApplyRefusal.StageChanged, before);
    }

    [Fact]
    public void A_manifest_on_disk_that_changed_after_the_hash_was_taken_is_a_changed_stage_even_when_the_message_is_unchanged()
    {
        using var rig = new ApplyRig("apply-edited-outer");
        File.AppendAllText(rig.OuterManifest, " "); // still a valid manifest of the same version and build: only its hash tells
        var before = rig.Snapshot();
        AssertRefused(new Harness(rig), rig, ApplyRefusal.StageChanged, before);
    }

    [Fact]
    public void An_inner_manifest_that_is_not_the_file_the_outer_manifest_lists_is_a_changed_stage()
    {
        using var rig = new ApplyRig("apply-edited-inner");
        File.AppendAllText(rig.InnerManifest, "\n");
        var before = rig.Snapshot();
        AssertRefused(new Harness(rig), rig, ApplyRefusal.StageChanged, before);
    }

    [Fact]
    public void A_link_in_the_way_of_the_stage_is_a_changed_stage()
    {
        using var rig = new ApplyRig("apply-link");
        var moved = rig.Staged.Folder + "-real";
        Directory.Move(rig.Staged.Folder, moved);
        Rig.Junction(rig.Staged.Folder, moved);
        try
        {
            var before = rig.Snapshot();
            AssertRefused(new Harness(rig), rig, ApplyRefusal.StageChanged, before);
        }
        finally
        {
            Directory.Delete(rig.Staged.Folder); // the link only
        }
    }

    [Fact]
    public void A_version_that_is_in_blocked_json_is_refused_and_another_version_there_is_not()
    {
        using var rig = new ApplyRig("apply-blocked");
        rig.Block("0.5.2", "33333333-3333-4333-8333-333333333333");
        Assert.Equal(SwapExit.Busy, new Harness(rig) { GuiEnds = false }.Run()); // not blocked: it goes on as far as the wait for the program

        rig.Block(rig.Staged.Version, rig.Staged.BuildId);
        var before = rig.Snapshot();
        AssertRefused(new Harness(rig), rig, ApplyRefusal.Blocked, before);
    }

    [Theory]
    [InlineData("0.5.0")]
    [InlineData("0.4.9")]
    public void A_version_that_is_not_newer_than_the_installed_one_is_refused(string version)
    {
        using var rig = new ApplyRig("apply-notnewer");
        var set = new SyntheticSet(version, SyntheticSet.NewBuild);
        foreach (var (name, data) in rig.Rig.New.Files)
        {
            set.Files[name] = data;
        }

        rig.Staged = rig.MakeStage(set, "aaaaaaaaaaaa");
        var before = rig.Snapshot();
        AssertRefused(new Harness(rig), rig, ApplyRefusal.NotNewer, before);
    }

    [Fact]
    public void An_installed_version_that_cannot_be_read_is_refused()
    {
        using var rig = new ApplyRig("apply-noinstalled");
        File.WriteAllText(rig.Rig.Paths.Installed, "not json");
        var before = rig.Snapshot();
        AssertRefused(new Harness(rig), rig, ApplyRefusal.NotNewer, before);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("notify")]
    [InlineData("off")]
    [InlineData("ask")]
    [InlineData("automatic")]
    [InlineData("")]
    public void Only_a_gui_ini_that_says_auto_lets_the_runner_go_on(string? mode)
    {
        using var rig = new ApplyRig("apply-mode", mode);
        var before = rig.Snapshot();
        AssertRefused(new Harness(rig), rig, ApplyRefusal.NotAutomatic, before);
    }

    [Fact]
    public void The_last_update_mode_in_gui_ini_wins_and_the_case_of_the_word_does_not_matter()
    {
        using var rig = new ApplyRig("apply-lastwins", mode: null);
        File.WriteAllText(rig.GuiIni, "update.mode=auto\r\nupdate.mode = NOTIFY\r\n");
        var before = rig.Snapshot();
        AssertRefused(new Harness(rig), rig, ApplyRefusal.NotAutomatic, before);

        File.WriteAllText(rig.GuiIni, "update.mode=off\r\n  update.mode = AUTO \r\n");
        var run = new Harness(rig) { GuiEnds = false };
        Assert.Equal(SwapExit.Busy, run.Run()); // accepted: it got as far as waiting for the program
        Assert.Equal([ApplyAnswer.Accepted], run.Channel.Sent);
    }

    [Fact]
    public void An_answer_that_cannot_be_sent_means_the_program_is_gone_and_nothing_is_done()
    {
        using var rig = new ApplyRig("apply-nosend");
        var before = rig.Snapshot();
        var run = new Harness(rig);
        run.Channel.SendFails = true;

        Assert.Equal(SwapExit.Aborted, run.Run());

        Assert.Empty(run.GuiWaits);
        Assert.Empty(run.Starter.Starts);
        rig.AssertUnchanged(before, "no one to answer");
    }

    // ---------------------------------------------------------------------------------------------------------------------------- after "accepted"

    [Fact]
    public void A_program_that_does_not_close_in_five_minutes_leaves_everything_as_it_was_and_starts_once()
    {
        using var rig = new ApplyRig("apply-notclosed");
        var before = rig.Snapshot();
        var run = new Harness(rig) { GuiEnds = false };

        Assert.Equal(SwapExit.Busy, run.Run());

        Assert.Equal([ApplyAnswer.Accepted], run.Channel.Sent);
        Assert.Equal([TimeSpan.FromMinutes(5)], run.GuiWaits);
        AssertStartedOnce(run, rig);
        Assert.Contains("result AfterPid Busy exit=30", run.Out.ToString(), StringComparison.Ordinal);
        rig.AssertUnchanged(before, "the program did not close");
        Assert.Null(rig.LastResultOnDisk());
        Assert.False(File.Exists(rig.Rig.Paths.Journal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_gate_that_stays_busy_starts_the_old_gui_only_if_another_gui_does_not_hold_it(bool anotherGui)
    {
        using var rig = new ApplyRig("apply-gatebusy");
        var before = rig.Snapshot();
        var run = new Harness(rig) { AnotherGui = anotherGui };

        using (var program = InstallGate.EnterStartup(rig.Rig.ExePath)) // a shared hold keeps exclusive entry out
        {
            Assert.True(program.IsOpen);
            Assert.Equal(SwapExit.Busy, run.Run());
        }

        Assert.Contains("result Gate Busy exit=30", run.Out.ToString(), StringComparison.Ordinal);
        if (anotherGui)
        {
            Assert.Empty(run.Starter.Starts);
        }
        else
        {
            AssertStartedOnce(run, rig);
        }

        Assert.True(run.Channel.Disposed);
        rig.AssertUnchanged(before, "a gate that stayed busy");
        Assert.Null(rig.LastResultOnDisk());
        Assert.False(File.Exists(rig.Rig.Paths.Journal));
        Assert.False(File.Exists(rig.Rig.Paths.Sentinel));
    }

    [Theory]
    [InlineData("gui.ini")]
    [InlineData("blocked")]
    [InlineData("stage")]
    [InlineData("stage-gone")]
    [InlineData("stage-unchanged-file")]
    [InlineData("stage-gate")]
    public void A_change_that_the_closing_program_makes_is_seen_again_after_it_closed_and_the_old_program_is_started(string change)
    {
        using var rig = new ApplyRig("apply-recheck-" + change);
        const string attemptId = "1123456789ab";
        var treeBefore = Rig.Snapshot(rig.Rig.Install, includeUpdate: true);
        var run = new Harness(rig)
        {
            Channel = new ScriptedChannel((rig.Message with { AttemptId = attemptId }).ToLine()),
            OnGuiEnd = () =>
            {
                switch (change)
                {
                    case "gui.ini":
                        File.WriteAllText(rig.GuiIni, "update.mode=notify\n"); // the program saves its settings as it closes
                        break;
                    case "blocked":
                        rig.Block(rig.Staged.Version, rig.Staged.BuildId);
                        break;
                    case "stage":
                        File.AppendAllText(rig.InnerManifest, "\n");
                        break;
                    case "stage-unchanged-file":
                        File.AppendAllText(Path.Combine(rig.Staged.FilesFolder(), "stable.dll"), "changed");
                        break;
                    case "stage-gate":
                        File.AppendAllText(Path.Combine(rig.Staged.FilesFolder(), GateFile.FileName), "changed");
                        break;
                    default:
                        Directory.Delete(rig.Staged.Folder, recursive: true);
                        break;
                }
            },
            Launcher = FakeLauncher.Ok(),
        };

        Assert.Equal(SwapExit.Aborted, run.Run());

        Assert.Equal([ApplyAnswer.Accepted], run.Channel.Sent);
        AssertStartedOnce(run, rig);
        Assert.Empty(((FakeLauncher)run.Launcher!).Calls); // nothing was installed, nothing was checked
        var written = rig.LastResultOnDisk();
        Assert.Equal("aborted", written?.Outcome);
        Assert.Equal(change.StartsWith("stage", StringComparison.Ordinal) ? "stagedMismatch" : "updateNotAllowed", written?.Reason);
        Assert.Equal(written?.Reason, LastResult.ReadAttemptReason(rig.Rig.Paths.LastResult, attemptId, rig.Staged.Version, rig.Staged.BuildId));
        rig.Rig.AssertTree(Generation.Old, "after an abort");
        var treeAfter = Rig.Snapshot(rig.Rig.Install, includeUpdate: true);
        foreach (var key in treeBefore.Keys.Where(k => k != "update/last-result.json"))
        {
            Assert.Equal(treeBefore[key], treeAfter.GetValueOrDefault(key));
        }

        Assert.False(File.Exists(rig.Rig.Paths.Journal));
    }

    [Fact]
    public void A_junction_above_L_is_allowed_by_the_gui_cache_and_the_runner()
    {
        var scratch = Scratch.NewFolder("apply-ancestor-link");
        var profile = Path.Combine(scratch, "profile");
        var alias = Path.Combine(scratch, "alias");
        try
        {
            using var rig = new ApplyRig("apply-ancestor", profile: profile);
            Rig.Junction(alias, profile);
            var local = Path.Combine(alias, Path.GetFileName(rig.Local));
            var state = InstallId.StateFolder(local, rig.Rig.ExePath)!;
            var stage = rig.Staged with { Folder = Path.Combine(StageLayout.CacheOf(state), Path.GetFileName(rig.Staged.Folder)) };
            Assert.True(StageCache.IsIntact(state, stage, localFolder: local));
            var run = new Harness(rig)
            {
                LocalFolder = local,
                Channel = new ScriptedChannel(ApplyMessage.Of(stage).ToLine()),
                Launcher = FakeLauncher.Ok(),
            };

            Assert.Equal(SwapExit.Committed, run.Run());
            AssertStartedOnce(run, rig);
        }
        finally
        {
            if (Directory.Exists(alias))
            {
                Directory.Delete(alias);
            }

            Scratch.Delete(scratch);
        }
    }

    [Fact]
    public void A_good_update_is_swapped_checked_with_the_gate_still_held_and_the_new_program_is_started()
    {
        using var rig = new ApplyRig("apply-commit");
        var launcher = FakeLauncher.Ok();
        var blocked = new List<GateOutcome>();
        Harness? run = null;
        run = new Harness(rig)
        {
            Launcher = launcher,
            OnLauncherMade = gate =>
            {
                Assert.True(gate.IsOpen);
                Assert.True(run!.Channel.Disposed, "the pipe and the process handle must be let go of before anything is started");
                using var second = InstallGate.EnterExclusive(rig.Rig.ExePath); // the gate is held by the runner, so a second entry is refused
                blocked.Add(second.Outcome);
            },
        };

        Assert.Equal(SwapExit.Committed, run.Run());

        Assert.Equal([ApplyAnswer.Accepted], run.Channel.Sent);
        Assert.Equal(1, run.Released);
        Assert.Equal([GateOutcome.Busy], blocked);
        rig.Rig.AssertTree(Generation.New, "after the commit");
        rig.Rig.AssertSettled(Generation.New, "after the commit");
        Assert.NotEmpty(launcher.Calls);
        Assert.Equal(InstallGate.InstallFolderOf(rig.Rig.ExePath), launcher.Calls[0].InstallFolder);
        Assert.Equal(rig.Rig.New.Version, launcher.Calls[0].Version);
        Assert.Equal(("committed", "none"), rig.LastResultOnDisk());
        AssertStartedOnce(run, rig);
        Assert.Contains("result Committed None exit=10", run.Out.ToString(), StringComparison.Ordinal);
        Assert.Empty(BlockedTags.Read(rig.Rig.Paths.Blocked));
    }

    [Fact]
    public void A_new_version_that_does_not_start_is_rolled_back_blocked_and_the_old_program_is_started()
    {
        using var rig = new ApplyRig("apply-rollback");
        var launcher = FakeLauncher.AlwaysFail();
        var run = new Harness(rig) { Launcher = launcher };

        Assert.Equal(SwapExit.RolledBack, run.Run());

        Assert.Equal(TxLimits.MaxLaunchAttempts, launcher.Calls.Count);
        rig.Rig.AssertTree(Generation.Old, "after the rollback");
        rig.Rig.AssertSettled(Generation.Old, "after the rollback");
        var tag = Assert.Single(BlockedTags.Read(rig.Rig.Paths.Blocked));
        Assert.Equal((rig.Rig.New.Version, rig.Rig.New.BuildId, TxTag.LaunchFailed), (tag.Version, tag.BuildId, tag.Reason));
        Assert.Equal(("rolledBack", "launchFailed"), rig.LastResultOnDisk());
        AssertStartedOnce(run, rig);
    }

    [Fact]
    public void A_recovery_that_failed_starts_the_gui_once_for_its_recovery_screen()
    {
        using var rig = new ApplyRig("apply-recoveryfailed");
        var launcher = new FakeLauncher(LaunchVerdict.Failed)
        {
            OnCall = _ => File.WriteAllBytes(Path.Combine(rig.Rig.Install, "PcMieru.Core.dll"), [1, 2, 3]), // neither the old nor the new file
        };
        var run = new Harness(rig) { Launcher = launcher };

        Assert.Equal(SwapExit.RecoveryFailed, run.Run());

        AssertStartedOnce(run, rig);
        Assert.Equal(("recoveryFailed", "fileNeitherHash"), rig.LastResultOnDisk());
        Assert.True(File.Exists(rig.Rig.Paths.Sentinel));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_gate_refusal_or_an_unexpected_exception_after_accept_starts_the_program_once(bool throws)
    {
        using var rig = new ApplyRig("apply-final-start");
        var run = new Harness(rig)
        {
            OnGuiEnd = () =>
            {
                if (throws)
                {
                    throw new InvalidOperationException("the wait failed");
                }

                File.Delete(rig.Rig.Paths.Gate);
            },
        };

        Assert.Equal(throws ? SwapExit.Crashed : SwapExit.GateRefused, run.Run());
        Assert.Equal([ApplyAnswer.Accepted], run.Channel.Sent);
        Assert.True(run.Channel.Disposed);
        Assert.Equal(1, run.Released);
        AssertStartedOnce(run, rig);
    }

    [Fact]
    public void A_failed_blocked_list_write_keeps_the_old_list_and_is_reported_in_the_rollback_result()
    {
        using var rig = new ApplyRig("apply-block-error");
        rig.Block("0.0.1", rig.Rig.Old.BuildId);
        var run = new Harness(rig)
        {
            Launcher = FakeLauncher.AlwaysFail(),
            Fault = new FailOnce(new CutPoint(TxSteps.BlockedTag, TxEdge.Mid, 1)),
        };

        Assert.Equal(SwapExit.RolledBack, run.Run());
        AssertStartedOnce(run, rig);
        Assert.Equal("0.0.1", Assert.Single(BlockedTags.Read(rig.Rig.Paths.Blocked)).Version);
        var result = File.ReadAllText(rig.Rig.Paths.LastResult);
        Assert.Contains("blocked.json could not be written", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wait_on_an_invalid_process_handle_does_not_count_as_exit()
    {
        Assert.False(LaunchNative.WaitForProcessHandle(0, TimeSpan.Zero));
    }

    [Fact]
    public void A_swap_that_could_not_replace_a_file_is_rolled_back_without_a_tag_and_the_old_program_is_started()
    {
        using var rig = new ApplyRig("apply-ioroll");
        var run = new Harness(rig) { Launcher = FakeLauncher.Ok() };

        using (new FileStream(Path.Combine(rig.Rig.Install, "PcMieru.Core.dll"), FileMode.Open, FileAccess.Read, FileShare.Read)) // a scanner
        {
            Assert.Equal(SwapExit.RolledBack, run.Run());
        }

        Assert.Equal(("rolledBack", "swapIoFailure"), rig.LastResultOnDisk());
        Assert.Empty(BlockedTags.Read(rig.Rig.Paths.Blocked));
        AssertStartedOnce(run, rig);
        rig.Rig.AssertTree(Generation.Old, "after the rollback");
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("rollback")]
    [InlineData("abort")]
    public void The_final_start_carries_the_arguments_the_host_names_after_a_commit_a_rollback_and_an_abort_and_nothing_else_changes(string end)
    {
        using var rig = new ApplyRig("apply-startargs-" + end);
        var smoke = Path.Combine(rig.Rig.Root, "smoke-report.json");
        string[] arguments = ["--smoke", smoke, "--fakes"];
        var run = new Harness(rig)
        {
            ProgramArguments = arguments,
            Launcher = end == "rollback" ? FakeLauncher.AlwaysFail() : FakeLauncher.Ok(),
        };
        var expected = end switch { "commit" => SwapExit.Committed, "rollback" => SwapExit.RolledBack, _ => SwapExit.Aborted };

        if (end == "abort")
        {
            run.OnGuiEnd = () => rig.Block(rig.Staged.Version, rig.Staged.BuildId); // the program closes, and by then the version is one that must not be installed
        }

        Assert.Equal(expected, run.Run());

        var start = Assert.Single(run.Starter.Starts);
        Assert.Equal(Path.Combine(InstallGate.InstallFolderOf(rig.Rig.ExePath), "PCMieru.exe"), start.Exe);
        Assert.Equal(arguments, start.Arguments);
        rig.Rig.AssertTree(end == "commit" ? Generation.New : Generation.Old, "the swap is the same whatever the final start is");
    }

    [Fact]
    public void A_start_that_fails_after_the_swap_is_said_and_does_not_change_the_exit_code()
    {
        using var rig = new ApplyRig("apply-startfails");
        var run = new Harness(rig) { Launcher = FakeLauncher.Ok() };
        run.Starter.Succeeds = false;

        Assert.Equal(SwapExit.Committed, run.Run());

        Assert.Contains("PCMieru.exe could not be started", run.Err.ToString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the protocol itself

    [Fact]
    public void The_message_round_trips_and_every_wrong_shape_is_refused()
    {
        var good = new ApplyMessage(@"C:\Users\ユーザー\AppData\Local\PCMieru\update\11111111-0123456789abcdef\cache\0123456789ab", new string('a', 64), "1.12.3", "abcdef01-2222-4222-8222-222222222222", "v1.12.3");
        Assert.True(ApplyMessage.TryParse(good.ToLine(), out var parsed));
        Assert.Equal(good, parsed);

        var bad = new[]
        {
            null, string.Empty, "x", good.ToLine() + "\textra", good.ToLine().Replace("pcm-apply/1", "pcm-apply/0", StringComparison.Ordinal),
            (good with { Stage = "relative\\path" }).ToLine(), (good with { Stage = string.Empty }).ToLine(), (good with { Stage = good.Stage + "\u0007" }).ToLine(),
            (good with { ManifestSha256 = new string('A', 64) }).ToLine(), (good with { ManifestSha256 = new string('a', 63) }).ToLine(), (good with { ManifestSha256 = new string('g', 64) }).ToLine(),
            (good with { Version = "1.12" }).ToLine(), (good with { Version = "01.12.3", Tag = "v01.12.3" }).ToLine(), (good with { Tag = "1.12.3" }).ToLine(), (good with { Tag = "v1.12.4" }).ToLine(),
            (good with { BuildId = "NOT-A-GUID" }).ToLine(), (good with { BuildId = good.BuildId.ToUpperInvariant() }).ToLine(), (good with { BuildId = good.BuildId.Replace("-", string.Empty, StringComparison.Ordinal) }).ToLine(),
        };
        foreach (var line in bad)
        {
            Assert.False(ApplyMessage.TryParse(line, out _), "accepted: " + line);
        }
    }

    [Fact]
    public void The_answers_round_trip_and_every_refusal_has_a_sentence_in_both_languages_without_an_address()
    {
        Assert.True(ApplyAnswer.TryParse(ApplyAnswer.Accepted, out _));
        foreach (var refusal in Enum.GetValues<ApplyRefusal>())
        {
            Assert.False(ApplyAnswer.TryParse(ApplyAnswer.Refuse(refusal), out var back));
            Assert.Equal(refusal, back);
            Assert.False(string.IsNullOrWhiteSpace(ApplyAnswer.Ja(refusal)));
            Assert.False(string.IsNullOrWhiteSpace(ApplyAnswer.En(refusal)));
            Assert.DoesNotContain("://", ApplyAnswer.Ja(refusal) + ApplyAnswer.En(refusal), StringComparison.Ordinal);
        }

        foreach (var line in new[] { null, string.Empty, "accepted ", "Accepted", "refused", "refused ", "refused Nonsense", "refused elevated", "refused 3", "refused Elevated now" })
        {
            Assert.False(ApplyAnswer.TryParse(line, out var refusal));
            Assert.Equal(ApplyRefusal.BadMessage, refusal);
        }
    }

    [Fact]
    public void The_lines_of_the_launch_check_carry_a_number_only_where_they_should()
    {
        Assert.Equal("ping 7", VerifyProtocol.Ping(7));
        Assert.Equal("pong 7", VerifyProtocol.Pong(7));
        Assert.Equal(7, VerifyProtocol.NumberOf("ping 7", VerifyProtocol.PingPrefix));
        Assert.Null(VerifyProtocol.NumberOf("ping 7", VerifyProtocol.PongPrefix));
        Assert.Null(VerifyProtocol.NumberOf("ping x", VerifyProtocol.PingPrefix));
        Assert.Null(VerifyProtocol.NumberOf("ping -1", VerifyProtocol.PingPrefix));
        Assert.Null(VerifyProtocol.NumberOf("ping ", VerifyProtocol.PingPrefix));
        Assert.Null(VerifyProtocol.NumberOf("ping 99999999999", VerifyProtocol.PingPrefix));
    }

    [Fact]
    public void The_exit_code_of_a_refused_apply_and_the_usage_text_agree()
    {
        Assert.Contains("--apply <pipe> <process>", SwapCli.Usage, StringComparison.Ordinal);
        Assert.Contains("12 aborted or refused (nothing changed)", SwapCli.Usage, StringComparison.Ordinal);
        Assert.Contains("32 refused: running as administrator", SwapCli.Usage, StringComparison.Ordinal);
        Assert.Equal(TxReasonText.Ja(TxReason.UpdateNotAllowed).Length > 0, TxReasonText.En(TxReason.UpdateNotAllowed).Length > 0);
        Assert.NotEqual(string.Empty, TxReasonText.Ja(TxReason.UpdateNotAllowed));
        Assert.DoesNotContain("://", TxReasonText.Ja(TxReason.UpdateNotAllowed) + TxReasonText.En(TxReason.UpdateNotAllowed), StringComparison.Ordinal);
    }
}

/// <summary><c>update.mode</c> as the runner reads it from gui.ini.</summary>
public sealed class UpdateModeFileTests
{
    [Theory]
    [InlineData("update.mode=auto", UpdateMode.Automatic)]
    [InlineData("update.mode = Auto ", UpdateMode.Automatic)]
    [InlineData("  update.mode=notify", UpdateMode.Notify)]
    [InlineData("update.mode=off", UpdateMode.Off)]
    [InlineData("update.mode=ask", UpdateMode.Ask)]
    [InlineData("update.mode=", UpdateMode.Ask)]
    [InlineData("update.mode", UpdateMode.Ask)]
    [InlineData("=auto", UpdateMode.Ask)]
    [InlineData("xupdate.mode=auto", UpdateMode.Ask)]
    [InlineData("update.mode=auto\nupdate.mode=off", UpdateMode.Off)]
    [InlineData("update.mode=off\nupdate.mode=auto", UpdateMode.Automatic)]
    [InlineData("update.mode=auto\nupdate.mode=bogus", UpdateMode.Ask)]
    [InlineData("a=b\r\nupdate.mode=auto\r\nc=d", UpdateMode.Automatic)]
    [InlineData("", UpdateMode.Ask)]
    public void The_last_word_of_the_key_decides(string text, UpdateMode expected)
    {
        var folder = Scratch.NewFolder("mode-file");
        try
        {
            var path = Path.Combine(folder, "gui.ini");
            File.WriteAllText(path, text);
            Assert.Equal(expected, UpdateModes.ReadFromFile(path));
        }
        finally
        {
            Scratch.Delete(folder);
        }
    }

    [Fact]
    public void A_missing_unreadable_or_huge_file_is_ask()
    {
        var folder = Scratch.NewFolder("mode-file2");
        try
        {
            Assert.Equal(UpdateMode.Ask, UpdateModes.ReadFromFile(Path.Combine(folder, "none.ini")));
            Assert.Equal(UpdateMode.Ask, UpdateModes.ReadFromFile(folder)); // a folder, not a file
            var huge = Path.Combine(folder, "huge.ini");
            File.WriteAllText(huge, "update.mode=auto\n" + new string('#', 1024 * 1024 + 10));
            Assert.Equal(UpdateMode.Ask, UpdateModes.ReadFromFile(huge));
            var locked = Path.Combine(folder, "locked.ini");
            File.WriteAllText(locked, "update.mode=auto\n");
            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Equal(UpdateMode.Ask, UpdateModes.ReadFromFile(locked));
            }

            Assert.Equal(UpdateMode.Automatic, UpdateModes.ReadFromFile(locked));
        }
        finally
        {
            Scratch.Delete(folder);
        }
    }
}
