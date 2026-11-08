using System.Diagnostics;
using PcMieru.Core.Management;
using PcMieru.Wire;

namespace PcMieru.Gui.Tests;

/// <summary>
/// CPU assignment in the process menu (B.md 3): a check list of logical processors, "back to all", protected processes
/// refused. Every test runs on fake services with a recording stand-in for D's affinity API, so no real process is touched.
/// </summary>
public class AffinityTests
{
    private sealed class RecordingAffinity : IProcessAffinityControl
    {
        public AffinityInfo? Info { get; set; } = new(0b0011, 0b1111);

        public OperationResult Answer { get; set; } = OperationResult.Success("assigned");

        public List<(ProcessKey Key, ulong Mask)> Set { get; } = [];

        public AffinityInfo? QueryAffinity(ProcessKey key) => Info;

        public Task<OperationResult> SetAffinityAsync(ProcessKey key, ulong mask, CancellationToken ct = default)
        {
            Set.Add((key, mask));
            return Task.FromResult(Answer);
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly IDisposable _locale = Harness.Locale();
        private readonly ThemeService _theme = new(ThemeMode.Light);

        public Rig(Snapshot snapshot)
        {
            Affinity = new RecordingAffinity();
            Services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true }, registry => registry.Set<IProcessAffinityControl>(Affinity));
            Pump = new SnapshotPump(Services);
            Context = new GuiContext(Services, Pump, _theme, new GuiSettings());
            Context.StatusMessage += Status.Add;
            Page = new ProcessesPage(Context);
            Normal = snapshot.Processes.First(p => Services.Protection.Evaluate(p.Key).MayOperate);
            Protected = snapshot.Processes.First(p => Services.Protection.Evaluate(p.Key).Kind == ProtectionKind.Protected);
            Dialogs.AutoAnswer = false; // dialogs never open and never wait for a click
            Dialogs.Log.Clear();
        }

        public RecordingAffinity Affinity { get; }

        public PcMieruServices Services { get; }

        public SnapshotPump Pump { get; }

        public GuiContext Context { get; }

        public ProcessesPage Page { get; }

        public ProcessInfo Normal { get; }

        public ProcessInfo Protected { get; }

        public List<string> Status { get; } = [];

        public void Dispose()
        {
            Dialogs.AutoAnswer = null;
            AffinityDialog.AutoMask = null;
            Page.Dispose();
            Pump.Dispose();
            Services.Dispose();
            _theme.Dispose();
            _locale.Dispose();
        }
    }

    private static Rig Build() => new(Harness.FakeSnapshotAsync().GetAwaiter().GetResult());

    // The page's continuations come back through the message loop, so the test thread has to run it while it waits.
    private static T Finish<T>(Task<T> task)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.ElapsedMilliseconds < 10_000)
        {
            Application.DoEvents();
            Thread.Sleep(2);
        }

        Assert.True(task.IsCompleted, "the action did not finish");
        return task.GetAwaiter().GetResult();
    }

    private static AffinityDialog Dialog(ThemeService theme, ulong process, ulong system) =>
        new(theme, "app.exe (42)", new AffinityInfo(process, system));

    [Fact]
    public void The_dialog_has_one_box_per_logical_processor_checked_as_the_process_has_it()
    {
        using var _ = Harness.Locale();
        using var theme = new ThemeService(ThemeMode.Light);
        using var dialog = Dialog(theme, 0b0101, 0b1111);

        Assert.Equal(4, dialog.CpuCount);
        Assert.Equal(0b0101UL, dialog.Mask);
        Assert.True(dialog.CanApply);
        Assert.Equal("2 of 4 CPUs in use", dialog.CountText);
    }

    [Fact]
    public void The_boxes_follow_the_system_mask_so_a_sparse_group_gets_only_the_cpus_it_has()
    {
        using var _ = Harness.Locale();
        using var theme = new ThemeService(ThemeMode.Light);
        using var dialog = Dialog(theme, 0b1000_0000, 0b1010_0000);

        Assert.Equal(2, dialog.CpuCount);
        Assert.Equal(0b1000_0000UL, dialog.Mask);
        dialog.SelectAll();
        Assert.Equal(0b1010_0000UL, dialog.Mask); // never a bit the system does not have
    }

    [Fact]
    public void Sixty_four_processors_fit_and_back_to_all_checks_every_one_of_them()
    {
        using var _ = Harness.Locale();
        using var theme = new ThemeService(ThemeMode.Light);
        using var dialog = Dialog(theme, 1, ulong.MaxValue);

        Assert.Equal(AffinityDialog.MaxCpus, dialog.CpuCount);
        Assert.Equal(1UL, dialog.Mask);
        dialog.SelectAll();
        Assert.Equal(ulong.MaxValue, dialog.Mask);
        Assert.Equal("64 of 64 CPUs in use", dialog.CountText);
    }

    [Fact]
    public void Nothing_checked_cannot_be_applied_and_says_so()
    {
        using var _ = Harness.Locale();
        using var theme = new ThemeService(ThemeMode.Light);
        using var dialog = Dialog(theme, 0b0011, 0b1111);

        dialog.SetMask(0);
        Assert.False(dialog.CanApply);
        Assert.Equal(0UL, dialog.Mask);
        Assert.Equal("Choose at least one CPU", dialog.CountText);

        dialog.SelectAll(); // back to all brings it back
        Assert.True(dialog.CanApply);
        Assert.Equal(0b1111UL, dialog.Mask);
    }

    [Fact]
    public void The_dialog_texts_exist_in_both_languages()
    {
        foreach (var language in new[] { Language.Ja, Language.En })
        {
            foreach (var key in new[] { "gui.proc.act.affinity", "gui.proc.affinity.title", "gui.proc.affinity.msg", "gui.proc.affinity.all", "gui.proc.affinity.ok", "gui.proc.affinity.none", "gui.proc.affinity.unreadable" })
            {
                Strings.Register(GuiStrings.All);
                var text = Strings.Get(key, language);
                Assert.False(string.IsNullOrWhiteSpace(text) || text.StartsWith('['), key + " " + language);
            }
        }
    }

    [Fact]
    public void A_chosen_mask_goes_to_the_affinity_api_for_exactly_that_process()
    {
        using var rig = Build();
        AffinityDialog.AutoMask = 0b0100;

        var result = Finish(rig.Page.ApplyAffinityAsync(rig.Normal));

        Assert.NotNull(result);
        Assert.Equal(OperationCode.Ok, result.Code);
        var call = Assert.Single(rig.Affinity.Set);
        Assert.Equal(rig.Normal.Key, call.Key);
        Assert.Equal(0b0100UL, call.Mask);
        Assert.Contains(rig.Status, s => s.Contains(rig.Normal.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void Cancelling_or_choosing_what_the_process_already_has_asks_nothing_of_the_api()
    {
        using var rig = Build();

        AffinityDialog.AutoMask = null; // cancel
        Assert.Null(Finish(rig.Page.ApplyAffinityAsync(rig.Normal)));

        AffinityDialog.AutoMask = rig.Affinity.Info!.ProcessMask; // unchanged
        Assert.Null(Finish(rig.Page.ApplyAffinityAsync(rig.Normal)));

        Assert.Empty(rig.Affinity.Set);
    }

    [Fact]
    public void A_protected_process_is_refused_before_the_dialog_and_nothing_reaches_the_api()
    {
        using var rig = Build();
        AffinityDialog.AutoMask = 0b0001; // would be applied if the check let it through

        var result = Finish(rig.Page.ApplyAffinityAsync(rig.Protected));

        Assert.Null(result);
        Assert.Empty(rig.Affinity.Set);
        Assert.Contains(Dialogs.Log, line => line.StartsWith(Strings.Get("gui.proc.act.affinity"), StringComparison.Ordinal));
        Assert.DoesNotContain(Dialogs.Log, line => line.StartsWith(Strings.Get("gui.proc.affinity.title") + " |", StringComparison.Ordinal));
    }

    [Fact]
    public void A_process_whose_affinity_cannot_be_read_gets_an_explanation_instead_of_a_dialog()
    {
        using var rig = Build();
        rig.Affinity.Info = null;
        AffinityDialog.AutoMask = 0b0001;

        var result = Finish(rig.Page.ApplyAffinityAsync(rig.Normal));

        Assert.Null(result);
        Assert.Empty(rig.Affinity.Set);
        Assert.Contains(Dialogs.Log, line => line.Contains(Strings.Get("gui.proc.affinity.unreadable"), StringComparison.Ordinal));
    }

    [Fact]
    public void A_refusal_from_the_api_is_shown_in_words()
    {
        using var rig = Build();
        rig.Affinity.Answer = OperationResult.Fail(OperationCode.Unsupported, "spans processor groups");
        AffinityDialog.AutoMask = 0b0001;

        var result = Finish(rig.Page.ApplyAffinityAsync(rig.Normal));

        Assert.Equal(OperationCode.Unsupported, result!.Code);
        Assert.Contains(Dialogs.Log, line => line.Contains("spans processor groups", StringComparison.Ordinal));
        Assert.Contains(rig.Status, s => s.Contains("spans processor groups", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_real_api_refuses_the_system_process_before_it_opens_anything()
    {
        // PID 4 is answered from the PID alone (no handle, no side effect), so the real control is safe to ask here.
        var control = new ProcessControl(new ProtectionPolicy(), new EcoStateStore(Path.Combine(Path.GetTempPath(), "pcmieru-affinity-" + Guid.NewGuid().ToString("N") + ".json")));

        var result = await control.SetAffinityAsync(new ProcessKey(4, 0), 1);

        Assert.Equal(OperationCode.Protected, result.Code);
    }

    [Fact]
    public void The_process_menu_offers_it_and_greys_it_out_with_the_reason_for_a_protected_process()
    {
        using var rig = Build();
        var text = Strings.Get("gui.proc.act.affinity");

        var open = rig.Page.BuildMenu(rig.Normal).Items.OfType<ToolStripMenuItem>().Single(i => i.Text == text);
        Assert.True(open.Enabled);

        var blocked = rig.Page.BuildMenu(rig.Protected).Items.OfType<ToolStripMenuItem>().Single(i => i.Text == text);
        Assert.False(blocked.Enabled);
        Assert.Contains(Strings.Get("gui.proc.act.off.protected"), blocked.ToolTipText, StringComparison.Ordinal);
    }
}
