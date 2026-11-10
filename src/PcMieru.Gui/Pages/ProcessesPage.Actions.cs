using System.ComponentModel;
using System.Globalization;
using System.Text;
using PcMieru.Wire;

namespace PcMieru.Gui;

/// <summary>
/// The context menu and everything it does. Every destructive action goes through <see cref="IProtectionPolicy"/> right
/// before it runs and asks first, naming the target by name, PID and start time. A failure says why, in words.
/// </summary>
internal sealed partial class ProcessesPage
{
    private static readonly (SettablePriority Priority, int Level)[] Priorities =
    [
        (SettablePriority.Idle, 4),
        (SettablePriority.BelowNormal, 6),
        (SettablePriority.Normal, 8),
        (SettablePriority.AboveNormal, 10),
        (SettablePriority.High, 13),
    ];

    internal ContextMenuStrip Menu => _menu;

    /// <summary>Starts a program the user typed into "run new task". Separate from the dialog so a test can call it.</summary>
    internal OperationResult StartTask(string commandLine, bool elevated)
    {
        var result = Context.Services.Tasks.Run(commandLine, elevated);
        if (result.IsSuccess)
        {
            Context.Report("gui.run.started", ("command", commandLine));
        }
        else
        {
            Context.Report("gui.run.failed", ("message", string.IsNullOrWhiteSpace(result.Message) ? OperationText(result.Code) : result.Message));
        }

        return result;
    }

    internal void RunNewTask()
    {
        using var dialog = new RunTaskDialog(Context.Theme);
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK && dialog.CommandLine.Length > 0)
        {
            StartTask(dialog.CommandLine, dialog.RunAsAdministrator);
        }
    }

    internal async Task TerminateSelectedAsync()
    {
        if (CurrentTarget() is not { } target || !CheckAllowed(target, "gui.proc.act.terminate"))
        {
            return;
        }

        var confirmed = Dialogs.Confirm(
            FindForm(),
            Context.Theme,
            Strings.Get("gui.proc.confirm.terminate.title"),
            Strings.Get("gui.proc.confirm.terminate.msg"),
            Facts(target),
            Strings.Get("gui.proc.confirm.terminate.ok"));
        if (!confirmed)
        {
            return;
        }

        var result = await Context.Services.ProcessControl.TerminateAsync(target.Key).ConfigureAwait(true);
        ReportResult(target, "gui.proc.act.terminate", result);
        await Context.Pump.TickAsync().ConfigureAwait(true);
    }

    internal async Task TerminateTreeSelectedAsync()
    {
        if (CurrentTarget() is not { } target || !CheckAllowed(target, "gui.proc.act.tree"))
        {
            return;
        }

        var plan = await Context.Services.ProcessControl.PlanTerminateTreeAsync(target.Key).ConfigureAwait(true);
        if (plan.Targets.Count == 0)
        {
            Dialogs.Info(FindForm(), Context.Theme, Strings.Get("gui.proc.confirm.tree.title"), Strings.Get("gui.proc.plan.empty"), Facts(target));
            return;
        }

        var message = Strings.Format("gui.proc.confirm.tree.msg", Fmt.Dict(("count", plan.Targets.Count.ToString(CultureInfo.CurrentCulture))));
        if (plan.SkippedProtected.Count > 0)
        {
            message += " " + Strings.Format("gui.proc.confirm.tree.skipped", Fmt.Dict(("skipped", plan.SkippedProtected.Count.ToString(CultureInfo.CurrentCulture))));
        }

        var names = plan.Targets.Select(DescribeKey).Take(40).ToList();
        var confirmed = Dialogs.Confirm(
            FindForm(),
            Context.Theme,
            Strings.Get("gui.proc.confirm.tree.title"),
            message,
            Facts(target),
            Strings.Format("gui.proc.confirm.tree.ok", Fmt.Dict(("count", plan.Targets.Count.ToString(CultureInfo.CurrentCulture)))),
            lines: names);
        if (!confirmed)
        {
            return;
        }

        var result = await Context.Services.ProcessControl.TerminateTreeAsync(plan).ConfigureAwait(true);
        var details = result.Details ?? [];
        var ok = details.Count(d => d.Code == OperationCode.Ok);
        var failed = details.Count - ok;
        var lines = details.Select(d => d.Code == OperationCode.Ok
            ? Strings.Format("gui.op.result", Fmt.Dict(("target", d.Target), ("result", OperationText(d.Code))))
            : Strings.Format("gui.op.result.detail", Fmt.Dict(("target", d.Target), ("result", OperationText(d.Code)), ("message", d.Message)))).ToList();
        var summary = details.Count > 0
            ? Strings.Format("gui.proc.result.tree.summary", Fmt.Dict(("ok", ok.ToString(CultureInfo.CurrentCulture)), ("failed", failed.ToString(CultureInfo.CurrentCulture))))
            : OperationText(result.Code);
        Dialogs.Info(FindForm(), Context.Theme, Strings.Get("gui.proc.result.tree.title"), summary, null, lines);
        await Context.Pump.TickAsync().ConfigureAwait(true);
    }

    internal async Task CloseWindowSelectedAsync()
    {
        if (CurrentTarget() is not { } target || !CheckAllowed(target, "gui.proc.act.close"))
        {
            return;
        }

        var result = await Context.Services.ProcessControl.CloseWindowAsync(target.Key).ConfigureAwait(true);
        ReportResult(target, "gui.proc.act.close", result);
    }

    internal async Task SetPrioritySelectedAsync(SettablePriority priority)
    {
        if (CurrentTarget() is not { } target || !CheckAllowed(target, "gui.proc.act.priority"))
        {
            return;
        }

        if (priority is SettablePriority.AboveNormal or SettablePriority.High)
        {
            var confirmed = Dialogs.Confirm(
                FindForm(),
                Context.Theme,
                Strings.Get("gui.proc.confirm.priority.title"),
                Strings.Get("gui.proc.confirm.priority.msg"),
                Facts(target),
                Strings.Get("gui.proc.confirm.priority.ok"),
                destructive: false);
            if (!confirmed)
            {
                return;
            }
        }

        var result = await Context.Services.ProcessControl.SetPriorityAsync(target.Key, priority).ConfigureAwait(true);
        ReportResult(target, "gui.proc.act.priority", result);
        await Context.Pump.TickAsync().ConfigureAwait(true);
    }

    internal async Task SetEfficiencySelectedAsync(bool enabled)
    {
        if (CurrentTarget() is not { } target || !CheckAllowed(target, "gui.proc.detail.efficiency"))
        {
            return;
        }

        var result = await Context.Services.ProcessControl.SetEfficiencyModeAsync(target.Key, enabled).ConfigureAwait(true);
        ReportResult(target, "gui.proc.detail.efficiency", result);
        _details.Forget(target.Key);
        ShowSelected();
    }

    /// <summary>
    /// CPU assignment (SPEC 5.2, B.md 3). The dialog is a check list with one box per logical processor of the process's
    /// group; "back to all" checks every box. A protected process is refused here before the dialog opens and again by
    /// D's API (the protection rules are applied on the handle that is changed), and the API alone refuses an empty
    /// mask, CPUs the system does not have and a process that spans processor groups, which is reported in words.
    /// </summary>
    internal async Task SetAffinitySelectedAsync()
    {
        if (CurrentTarget() is { } target)
        {
            _ = await ApplyAffinityAsync(target).ConfigureAwait(true);
        }
    }

    /// <summary>The whole flow for one process. Returns what D's API answered, or null when nothing was asked of it (refused here, unreadable, cancelled, unchanged).</summary>
    internal async Task<OperationResult?> ApplyAffinityAsync(ProcessInfo target)
    {
        if (!CheckAllowed(target, "gui.proc.act.affinity"))
        {
            return null;
        }

        var affinity = Context.Services.Get<IProcessAffinityControl>();
        if (affinity.QueryAffinity(target.Key) is not { } current)
        {
            Dialogs.Info(FindForm(), Context.Theme, Strings.Get("gui.proc.act.affinity"), Strings.Get("gui.proc.affinity.unreadable"), Facts(target));
            return null;
        }

        var who = target.Name + " (" + target.Key.Pid.ToString(CultureInfo.CurrentCulture) + ")";
        if (AffinityDialog.Choose(FindForm(), Context.Theme, who, current) is not { } mask || mask == current.ProcessMask)
        {
            return null;
        }

        var result = await affinity.SetAffinityAsync(target.Key, mask).ConfigureAwait(true);
        ReportResult(target, "gui.proc.act.affinity", result);
        return result;
    }

    private ProcessInfo? CurrentTarget() => _selectedKey is { } key ? _model.Find(key) : null;

    private IReadOnlyList<DialogFact> Facts(ProcessInfo target) =>
    [
        new DialogFact(Strings.Get("gui.proc.col.name"), target.Name),
        new DialogFact(Strings.Get("gui.proc.col.pid"), target.Key.Pid.ToString(CultureInfo.CurrentCulture)),
        new DialogFact(Strings.Get("gui.proc.col.started"), Fmt.Started(target.Key)),
    ];

    private string DescribeKey(ProcessKey key) =>
        (_model.Find(key)?.Name ?? "?") + " (" + key.Pid.ToString(CultureInfo.CurrentCulture) + ")";

    private static string OperationText(OperationCode code) => Strings.Get("gui.op.code." + code);

    /// <summary>The check right before an operation. A protected process or a failed query gets an explanation, never a try.</summary>
    private bool CheckAllowed(ProcessInfo target, string titleKey)
    {
        var verdict = Context.Services.Protection.Evaluate(target.Key);
        _verdict = verdict;
        if (verdict.MayOperate)
        {
            return true;
        }

        Dialogs.Info(FindForm(), Context.Theme, Strings.Get(titleKey), ProcessDetailPane.ProtectionText(verdict), Facts(target));
        ShowSelected();
        return false;
    }

    private void ReportResult(ProcessInfo target, string actionKey, OperationResult result)
    {
        var who = target.Name + " (" + target.Key.Pid.ToString(CultureInfo.CurrentCulture) + ")";
        if (result.Code is OperationCode.Ok or OperationCode.Gone)
        {
            Context.Report("gui.op.result", ("target", who), ("result", OperationText(result.Code)));
            return;
        }

        var text = string.IsNullOrWhiteSpace(result.Message)
            ? Strings.Format("gui.op.result", Fmt.Dict(("target", who), ("result", OperationText(result.Code))))
            : Strings.Format("gui.op.result.detail", Fmt.Dict(("target", who), ("result", OperationText(result.Code)), ("message", result.Message)));
        Context.Report(text);
        Dialogs.Info(FindForm(), Context.Theme, Strings.Get(actionKey), text, Facts(target));
    }

    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        var row = _list.SelectedRow;
        if (row < 0 || row >= _model.Count || _model[row].Process is not { } process)
        {
            e.Cancel = true;
            return;
        }

        _menuOpen = true;
        _model.Frozen = true;
        BuildMenu(process);
    }

    /// <summary>Fills the menu for one process. A choice that cannot be offered is greyed out and says why in its tooltip.</summary>
    internal ContextMenuStrip BuildMenu(ProcessInfo process)
    {
        var verdict = Context.Services.Protection.Evaluate(process.Key);
        _verdict = verdict;
        var details = _details.TryGet(process.Key);
        var may = verdict.MayOperate;
        var blocked = may ? null : Strings.Get("gui.proc.act.off.protected") + " " + ProcessDetailPane.ProtectionText(verdict);

        _menu.Items.Clear();
        AddItem(_menu.Items, Strings.Get("gui.proc.act.terminate"), may, blocked, () => Run(TerminateSelectedAsync), "Del");
        AddItem(_menu.Items, Strings.Get("gui.proc.act.tree"), may, blocked, () => Run(TerminateTreeSelectedAsync));
        var hasWindow = _classifier.HasWindow(process.Key.Pid);
        AddItem(_menu.Items, Strings.Get("gui.proc.act.close"), may && hasWindow, may ? Strings.Get("gui.proc.act.off.window") : blocked, () => Run(CloseWindowSelectedAsync));

        var priority = new ToolStripMenuItem(Strings.Get("gui.proc.act.priority")) { Enabled = may, ToolTipText = blocked };
        foreach (var (value, level) in Priorities)
        {
            var item = new ToolStripMenuItem(Strings.Get("gui.proc.priority." + level.ToString(CultureInfo.InvariantCulture)))
            {
                Checked = process.BasePriority == level,
            };
            var chosen = value;
            item.Click += (_, _) => Run(() => SetPrioritySelectedAsync(chosen));
            priority.DropDownItems.Add(item);
        }

        _menu.Items.Add(priority);

        var efficient = details is not null && details.EfficiencyMode.TryGet(out var on) && on;
        var win10 = Fmt.IsWindows10;
        var efficiencyText = Strings.Get(win10
            ? (efficient ? "gui.proc.act.qos.off" : "gui.proc.act.qos.on")
            : (efficient ? "gui.proc.act.efficiency.off" : "gui.proc.act.efficiency.on"));
        AddItem(_menu.Items, efficiencyText, may, blocked, () => Run(() => SetEfficiencySelectedAsync(!efficient)));
        AddItem(_menu.Items, Strings.Get("gui.proc.act.affinity"), may, blocked, () => Run(SetAffinitySelectedAsync));

        _menu.Items.Add(new ToolStripSeparator());
        var path = details is not null && details.Path.TryGet(out var p) ? p : null;
        var noPath = Strings.Get("gui.proc.act.off.path");
        AddItem(_menu.Items, Strings.Get("gui.proc.act.folder"), path is not null, noPath, OpenSelectedFolder);
        AddItem(_menu.Items, Strings.Get("gui.proc.act.properties"), path is not null, noPath, ShowSelectedProperties);
        AddItem(_menu.Items, Strings.Get("gui.proc.act.web"), true, null, SearchSelectedOnWeb, tip: Strings.Get("gui.proc.act.web.tip"));
        var services = details?.ServiceNames ?? [];
        AddItem(_menu.Items, Strings.Get("gui.proc.act.services"), services.Count > 0, Strings.Get("gui.proc.act.off.services"), () => Context.Navigate("services", process.Key, services[0]));

        _menu.Items.Add(new ToolStripSeparator());
        var copy = new ToolStripMenuItem(Strings.Get("gui.proc.act.copy"));
        AddItem(copy.DropDownItems, Strings.Get("gui.proc.act.copy.name"), true, null, () => Context.CopyText(process.Name));
        AddItem(copy.DropDownItems, Strings.Get("gui.proc.act.copy.pid"), true, null, () => Context.CopyText(process.Key.Pid.ToString(CultureInfo.InvariantCulture)));
        AddItem(copy.DropDownItems, Strings.Get("gui.proc.act.copy.path"), path is not null, noPath, () => Context.CopyText(path!));
        var commandLine = details is not null && details.CommandLine.TryGet(out var line) ? line : null;
        AddItem(copy.DropDownItems, Strings.Get("gui.proc.act.copy.cmdline"), commandLine is not null, noPath, () => Context.CopyText(commandLine!));
        AddItem(copy.DropDownItems, Strings.Get("gui.proc.act.copy.info"), true, null, () => Context.CopyText(InfoText(process, details)));
        _menu.Items.Add(copy);

        Context.Theme.StyleMenu(_menu);
        return _menu;
    }

    private static ToolStripMenuItem AddItem(ToolStripItemCollection items, string text, bool enabled, string? disabledTip, Action click, string? shortcut = null, string? tip = null)
    {
        var item = new ToolStripMenuItem(text) { Enabled = enabled, ToolTipText = enabled ? tip : disabledTip };
        if (shortcut is not null)
        {
            item.ShortcutKeyDisplayString = shortcut;
        }

        item.Click += (_, _) => click();
        items.Add(item);
        return item;
    }

    /// <summary>A text for pasting into a message to someone who helps. Run through the redactor, so user names in paths do not leave.</summary>
    private string InfoText(ProcessInfo process, ProcessDetails? details)
    {
        var text = new StringBuilder();
        void Line(string key, string value) => text.Append(Strings.Get(key)).Append(": ").AppendLine(value);
        Line("gui.proc.col.name", process.Name);
        Line("gui.proc.col.pid", process.Key.Pid.ToString(CultureInfo.InvariantCulture));
        Line("gui.proc.col.started", Fmt.Started(process.Key));
        Line("gui.proc.col.cpu", Fmt.Percent(process.CpuPercent));
        Line("gui.proc.col.memory", Fmt.Bytes(process.PrivateWorkingSetBytes));
        Line("gui.proc.col.ioread", Fmt.Rate(process.IoReadBytesPerSec));
        Line("gui.proc.col.iowrite", Fmt.Rate(process.IoWriteBytesPerSec));
        Line("gui.proc.detail.netsend", Fmt.NetRate(process.NetSendBytesPerSec, Context.Settings.NetworkUnit));
        Line("gui.proc.detail.netreceive", Fmt.NetRate(process.NetReceiveBytesPerSec, Context.Settings.NetworkUnit));
        if (details is not null)
        {
            Line("gui.proc.detail.product", Fmt.Text(details.Product, s => s));
            Line("gui.proc.detail.company", Fmt.Text(details.Company, s => s));
            Line("gui.proc.detail.path", Fmt.Text(details.Path, s => s));
            Line("gui.proc.detail.signature", Fmt.Text(details.Signature, s => s.State + (string.IsNullOrWhiteSpace(s.Signer) ? string.Empty : " / " + s.Signer)));
        }

        return Context.Services.Redactor.RedactText(text.ToString());
    }

    internal void OpenSelectedFolder()
    {
        var details = _selectedKey is { } key ? _details.TryGet(key) : null;
        if (details is null || !details.Path.TryGet(out var path))
        {
            return;
        }

        LaunchShell(() => ShellActions.OpenFileLocation(path));
    }

    private void ShowSelectedProperties()
    {
        var details = _selectedKey is { } key ? _details.TryGet(key) : null;
        if (details is null || !details.Path.TryGet(out var path))
        {
            return;
        }

        LaunchShell(() => ShellActions.ShowProperties(path));
    }

    internal void SearchSelectedOnWeb()
    {
        if (CurrentTarget() is { } target)
        {
            LaunchShell(() => ShellActions.SearchWeb(target.Name));
        }
    }

    private void LaunchShell(Action launch)
    {
        try
        {
            launch();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Context.Report(Strings.Format("gui.status.failed", Fmt.Dict(("error", ex.Message))));
        }
    }
}
