using PcMieru.Checks;
using PcMieru.Checks.Build;

namespace PcMieru.Gui;

internal static partial class SmokeRun
{
    private sealed partial class Runner
    {
        /// <summary>The four looks every picture of the checks and of the configuration code is taken in (the language first, then the theme).</summary>
        private static readonly (Language Language, ThemeMode Theme)[] LookCombos =
        [
            (Language.Ja, ThemeMode.Light),
            (Language.Ja, ThemeMode.Dark),
            (Language.En, ThemeMode.Light),
            (Language.En, ThemeMode.Dark),
        ];

        private string ScaleSuffix => dpiPercent > 0 ? "@" + dpiPercent : string.Empty;

        /// <summary>
        /// Runs <paramref name="body"/> once per look (both languages, both themes) when pictures are taken, and once in the look the walk ended in
        /// when they are not; the language and the theme are put back afterwards. The text it is given names the look ("ja-light").
        /// </summary>
        private async Task ForEachLook(Func<string, Task> body)
        {
            if (shotsDirectory is null)
            {
                await body("current").ConfigureAwait(true);
                return;
            }

            var language = Strings.Current;
            var theme = context.Theme.IsDark ? ThemeMode.Dark : ThemeMode.Light;
            try
            {
                foreach (var (combo, mode) in LookCombos)
                {
                    context.SetLanguage(combo);
                    context.SetTheme(mode);
                    await Task.Delay(250).ConfigureAwait(true);
                    await body($"{combo.ToString().ToLowerInvariant()}-{mode.ToString().ToLowerInvariant()}").ConfigureAwait(true);
                }
            }
            finally
            {
                context.SetLanguage(language);
                context.SetTheme(theme);
                await Task.Delay(150).ConfigureAwait(true);
            }
        }

        /// <summary>The index of the first row that shows this item (of this part, when one is named); -1 when there is none.</summary>
        private static int FindRow(CheckResultGrid grid, string itemId, string? subjectId = null)
        {
            for (var i = 0; i < grid.RowCount; i++)
            {
                if (grid.ItemAt(i) is { } item && item.Id == itemId && (subjectId is null || item.Subject?.Id == subjectId))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Scrolls a table so that this row is the first one on show (down to the end first, then up to the row: a list only scrolls as far as it must).</summary>
        private static void ScrollToTop(GridView grid, int row)
        {
            if (row < 0 || row >= grid.VirtualListSize)
            {
                return;
            }

            grid.EnsureVisible(grid.VirtualListSize - 1);
            grid.EnsureVisible(row);
        }

        /// <summary>The rows of a table that fit under its header (an estimate: the header is about one row high).</summary>
        private static int RowsOnShow(GridView grid) => Math.Max(1, (grid.ClientSize.Height / Math.Max(1, grid.RowHeight)) - 1);

        /// <summary>What a folder holds, as a short text (how many entries and the newest write time), so that "nothing was written here" can be checked without naming a path.</summary>
        private static string FolderStamp(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return "none";
                }

                var entries = new DirectoryInfo(path).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).ToList();
                return entries.Count + " entries, newest " + (entries.Count == 0 ? 0 : entries.Max(static e => e.LastWriteTimeUtc.Ticks));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be looked at is a failed check line (see <see cref="StampUnreadable"/>), never an exception that ends the run.
                return StampUnreadable + CheckFormat.ExceptionDetail(ex);
            }
        }

        /// <summary>The start of the text <see cref="FolderStamp"/> gives when it could not look at the folder.</summary>
        private const string StampUnreadable = "unreadable ";

        private async Task ShootChecksTab(ChecksPage page, string tabId, Func<Task>? settle = null)
        {
            await ForEachLook(async look =>
            {
                page.SelectTab(tabId);
                if (settle is not null)
                {
                    await settle().ConfigureAwait(true);
                }

                await Task.Delay(150).ConfigureAwait(true);
                var missing = SmokeText.Missing(page).Concat(SmokeText.Missing(form)).Distinct().Take(5).ToList();
                Check($"checks: tab {tabId} has its texts ({look})", missing.Count == 0, string.Join(" | ", missing));
                var clipped = SmokeClip.Clipped(form).Distinct().Take(6).ToList();
                Check($"checks: tab {tabId} has no cut-off text ({look})", clipped.Count == 0, string.Join(" | ", clipped));
                // SmokeClip looks at the headers only: every cell of the result table is asked of the grid itself (an item label and a verdict word
                // are never cut; a long evidence line or a part's name may be, when its tooltip holds the whole text).
                var grid = page.TabOf(tabId) switch
                {
                    RunCheckTab run => run.Grid,
                    CheckHistoryTab history => history.Detail,
                    _ => null,
                };
                if (grid is not null)
                {
                    var cuts = grid.UnreadableCuts().Take(4).ToList();
                    Check($"checks: no cell of the {tabId} table is cut short without a tooltip that holds it ({look})", cuts.Count == 0, string.Join(" | ", cuts) + " (columns " + string.Join("/", grid.Columns.Cast<ColumnHeader>().Select(static c => c.Width)) + ")");
                }

                if (shotsDirectory is not null)
                {
                    _shots.Add(await Capture(shotsDirectory, $"{look}-checks-{tabId}{ScaleSuffix}.png").ConfigureAwait(true));
                }
            }).ConfigureAwait(true);
        }

        /// <summary>
        /// <c>--fakes</c> only: the checks page with checks that run. The fake runner measures nothing (no load, no disk, no WMI, no administrator
        /// prompt) and the history is the in-memory one, so nothing is written under the data folder; both are checked before anything starts.
        /// Walks a system check, a storage check (a counter that grew, two disks that cannot be told apart), a load check that is held on the
        /// screen (running with its progress, then stopping, then stopped) and the history with the stopped run selected, taking the pictures.
        /// "Read again as administrator" is never pressed.
        /// </summary>
        private async Task FakeChecksRun()
        {
            if (!context.Services.Options.ForceFakes)
            {
                Check("checks: without --fakes the smoke run starts no check", !context.Checks.IsBusy && context.Checks.State == CheckRunState.NotRun, context.Checks.State.ToString());
                return;
            }

            if (context.Checks.History is not InMemoryCheckHistory || context.FakeHold is not { } hold)
            {
                Check("checks: --fakes has the fake runner and the in-memory history", false, context.Checks.History.GetType().Name);
                return;
            }

            if (await Open("checks").ConfigureAwait(true) is not ChecksPage page
                || page.TabOf("system") is not SystemCheckTab system
                || page.TabOf("load") is not LoadCheckTab load
                || page.TabOf("storage") is not StorageCheckTab storage
                || page.TabOf("history") is not CheckHistoryTab history)
            {
                Check("checks: the page opens with its four tabs", false, LastOpen);
                return;
            }

            var folder = CheckHistoryStore.DefaultRoot();
            var folderBefore = FolderStamp(folder);

            // ---- system ----
            page.SelectTab("system");
            await Task.Delay(100).ConfigureAwait(true);
            Check("checks: the system tab offers its start button", system.StartButton.Enabled && !system.StopButton.Enabled, system.StartButton.Enabled + "/" + system.StopButton.Enabled);
            system.PressStart();
            await Until(() => context.Checks.StateOf(CheckKind.System).State == CheckRunState.Completed && !context.Checks.IsBusy && system.Grid.Result is not null, 10000).ConfigureAwait(true);
            Check("checks: a system check completes and its table is filled", system.CurrentState.State == CheckRunState.Completed && system.Grid.Result is { Kind: CheckKind.System, Outcome: CheckOutcome.Completed } && system.Grid.RowCount > 10, system.CurrentState.State + ", " + system.Grid.RowCount + " rows");
            Check("checks: the system tab says it completed", system.StateLabel.Text == Strings.Get("gui.chk.state.completed"), system.StateLabel.Text);
            var verdictsMissing = new[] { ItemVerdict.Ok, ItemVerdict.Caution, ItemVerdict.Check, ItemVerdict.Unavailable, ItemVerdict.Info }
                .Where(v => !Enumerable.Range(0, system.Grid.RowCount).Any(r => system.Grid.ItemAt(r) is not null && system.Grid.TextAt(r, 1) == CheckFormat.VerdictWord(v)))
                .Select(static v => v.ToString())
                .ToList();
            Check("checks: the system table shows every verdict (OK, caution, check, not measured, observation)", verdictsMissing.Count == 0, "missing: " + string.Join(",", verdictsMissing));
            var scrollSystem = FindRow(system.Grid, "system.pagefile.count");
            await ShootChecksTab(page, "system", () =>
            {
                // The loose items end with the event log, which carries three of the verdicts; the shot starts a few rows above them.
                ScrollToTop(system.Grid, scrollSystem);
                return Task.CompletedTask;
            }).ConfigureAwait(true);

            // ---- storage ----
            page.SelectTab("storage");
            await Task.Delay(100).ConfigureAwait(true);
            storage.PressStart();
            await Until(() => context.Checks.StateOf(CheckKind.Storage).State == CheckRunState.Completed && !context.Checks.IsBusy && storage.Grid.Result is not null, 10000).ConfigureAwait(true);
            Check("checks: a storage check completes and its table is filled", storage.CurrentState.State == CheckRunState.Completed && storage.Grid.Result is { Kind: CheckKind.Storage, Outcome: CheckOutcome.Completed } && storage.Grid.RowCount > 20, storage.CurrentState.State + ", " + storage.Grid.RowCount + " rows");
            Check("checks: the storage check was the normal one (read under normal rights, and a denied disk offers the administrator read: not pressed here)", storage.CurrentState.Request?.AllowElevation != true && storage.ElevateOffered, storage.CurrentState.Request?.AllowElevation + " / offered " + storage.ElevateOffered);
            var hoursRow = FindRow(storage.Grid, "storage.nvme.power_on_hours", "disk-1");
            var hoursDelta = FindRow(storage.Grid, "storage.nvme.power_on_hours.delta", "disk-1");
            Check("checks: a counter that grew is written as before, after and change", hoursDelta > 0 && storage.Grid.TextAt(hoursDelta, 3).Contains('→', StringComparison.Ordinal) && storage.Grid.TextAt(hoursDelta, 3).Contains("23", StringComparison.Ordinal), hoursDelta >= 0 ? storage.Grid.TextAt(hoursDelta, 3) : "no such row");
            var twinDelta = FindRow(storage.Grid, "storage.nvme.power_on_hours.delta", "disk-3");
            var twinDelta2 = FindRow(storage.Grid, "storage.nvme.power_on_hours.delta", "disk-4");
            var twinText = twinDelta >= 0 ? storage.Grid.TextAt(twinDelta, 3) : string.Empty;
            Check("checks: two disks that look the same say they cannot be compared (no before and after)", twinDelta > 0 && twinDelta2 > 0 && twinText.Length > 0 && !twinText.Contains('→', StringComparison.Ordinal) && !SmokeText.IsPlaceholder(twinText) && storage.Grid.TextAt(twinDelta2, 3) == twinText, twinText);
            // At the normal scale the counter, its change and the twin disk's row are on one screen. The same window at a denser scale is the same
            // number of pixels, so it holds fewer rows (a 1080-line screen at 150% shows about a dozen): there the rule is the part that still
            // matters, the counter and its change sit together on the screen, the change directly under the counter. A window that a small screen
            // made lower than its 800-line start size (a CI runner's 1024 x 768 desktop) holds fewer rows in the same way and gets the same rule.
            var onShow = RowsOnShow(storage.Grid);
            var denser = dpiPercent > 100;
            var lower = form.ClientSize.Height < form.LogicalToDeviceUnits(800);
            var why = denser ? ", " + dpiPercent + "%: the counter and its change only" : lower ? ", window " + form.ClientSize.Height + " high: the counter and its change only" : string.Empty;
            var fits = denser || lower ? hoursDelta == hoursRow + 1 && hoursDelta - hoursRow < onShow : twinDelta - hoursRow < onShow;
            Check("checks: the counter's change and the twin disk's row are on one screen when the table starts at the counter" + (denser || lower ? " (at a denser scale or in a lower window: the counter and its change)" : string.Empty), hoursRow >= 0 && twinDelta > hoursRow && fits, "rows " + hoursRow + ".." + twinDelta + " of " + onShow + " on show" + why);
            await ShootChecksTab(page, "storage", () =>
            {
                ScrollToTop(storage.Grid, hoursRow);
                return Task.CompletedTask;
            }).ConfigureAwait(true);

            // ---- load: running, stopping, stopped ----
            page.SelectTab("load");
            await Task.Delay(100).ConfigureAwait(true);
            var gates = hold.Arm();
            try
            {
                load.PressStart();
                await Task.WhenAny(gates.Reached, Task.Delay(8000)).ConfigureAwait(true);
                await Until(() => context.Checks.Progress is { Stage: CheckStage.Loading } && load.ProgressShown && load.RateLabel.Text.Length > 0, 4000).ConfigureAwait(true);
                Check("checks: a load check that is held is running", gates.Reached.IsCompleted && load.CurrentState.State == CheckRunState.Running && context.Checks.State == CheckRunState.Running, load.CurrentState.State.ToString());
                Check("checks: while it runs the tab shows how far it is, the rate and a stop button", load.ProgressShown && load.Strip.Visible && load.Bar.Fraction is > 0 and < 1 && load.ElapsedLabel.Text.Length > 0 && load.RateLabel.Text.Length > 0 && load.StopButton.Enabled && !load.StartButton.Enabled && !load.Duration.Enabled, load.ElapsedLabel.Text + " / " + load.RateLabel.Text);
                Check("checks: the running load has no placeholder text", !SmokeText.IsPlaceholder(load.ElapsedLabel.Text + load.RateLabel.Text + load.StateLabel.Text), load.StateLabel.Text);
                await ShootChecksTab(page, "load").ConfigureAwait(true);

                load.PressStop();
                // The controller says "stopping" at once; the tab draws it when the posted change arrives, so the wait is for the drawn text.
                await Until(() => load.CurrentState.State == CheckRunState.Stopping && load.StateLabel.Text == Strings.Get("gui.chk.state.stopping") && !load.StopButton.Enabled, 2000).ConfigureAwait(true);
                Check("checks: after a stop is asked the tab says it is stopping, and nothing else can be started", load.CurrentState.State == CheckRunState.Stopping && context.Checks.State == CheckRunState.Stopping && load.StateLabel.Text == Strings.Get("gui.chk.state.stopping") && !load.StopButton.Enabled && !load.StartButton.Enabled, load.StateLabel.Text);
            }
            finally
            {
                gates.ReleaseStopped();
                if (context.Checks.IsBusy)
                {
                    context.Checks.Stop();
                    await Until(() => !context.Checks.IsBusy, 10000).ConfigureAwait(true);
                }
            }

            await Until(() => !context.Checks.IsBusy && context.Checks.StateOf(CheckKind.Cpu).State == CheckRunState.Stopped && load.StateLabel.Text == Strings.Get("gui.chk.state.stopped") && load.StartButton.Enabled, 8000).ConfigureAwait(true);
            await Task.Delay(200).ConfigureAwait(true);
            var stoppedRun = context.Checks.StateOf(CheckKind.Cpu).Run;
            Check("checks: the stopped load ends as a stopped run with what it had counted", load.CurrentState.State == CheckRunState.Stopped && stoppedRun?.Result is { Outcome: CheckOutcome.Stopped } && load.Grid.RowCount > 0 && load.StateLabel.Text == Strings.Get("gui.chk.state.stopped") && load.StartButton.Enabled && !load.ProgressShown, load.CurrentState.State + ", " + load.Grid.RowCount + " rows");

            // ---- history, with the stopped run selected ----
            page.SelectTab("history");
            var expected = FakeCheckData.Seed.Count + 3;
            await Until(() => history.View == HistoryView.List && history.Entries.Count == expected, 5000).ConfigureAwait(true);
            var runId = stoppedRun?.Result.RunId;
            var index = runId is { } id ? history.Entries.ToList().FindIndex(e => e.RunId == id) : -1;
            Check("checks: the history lists the sample runs and the three that were just made", history.View == HistoryView.List && history.Entries.Count == expected && index >= 0, history.View + " / " + history.Entries.Count + " / stopped run at " + index);
            if (index >= 0)
            {
                history.SelectEntry(index);
                await Until(() => history.SelectedRunId == runId && history.DetailTask.IsCompleted && history.Detail.RowCount > 0, 4000).ConfigureAwait(true);
                await Until(() => history.List.WholeRowsProblem(out _) is null, 2000).ConfigureAwait(true);
                var listProblem = history.List.WholeRowsProblem(out var listFacts);
                Check("checks: the list of runs starts under its header and ends on a whole row", listProblem is null, (listProblem is null ? string.Empty : listProblem + " | ") + listFacts);
                Check("checks: the stopped run is selected and its saved values are shown", history.SelectedRunId == runId && history.Detail.RowCount > 0 && history.ListText(index, 3) == CheckFormat.OutcomeName(CheckOutcome.Stopped), history.SelectedRunId + " / " + history.Detail.RowCount + " rows / " + history.ListText(index, 3));
                await ShootChecksTab(page, "history", async () =>
                {
                    history.SelectEntry(index);
                    await Until(() => history.SelectedRunId == runId && history.DetailTask.IsCompleted && history.Detail.RowCount > 0, 4000).ConfigureAwait(true);
                }).ConfigureAwait(true);
            }

            var folderAfter = FolderStamp(folder);
            Check("checks: the fake checks wrote nothing under the data folder", !folderBefore.StartsWith(StampUnreadable, StringComparison.Ordinal) && !folderAfter.StartsWith(StampUnreadable, StringComparison.Ordinal) && folderAfter == folderBefore, folderBefore + " -> " + folderAfter);
            Check("checks: nothing is running at the end", !context.Checks.IsBusy, context.Checks.State.ToString());
            page.SelectTab("system");
        }

        /// <summary>
        /// The configuration code (PCB1) on the hardware page. With <c>--fakes</c>: the button is pressed (the dialog only logs under the smoke run,
        /// so nobody has to click it away), and the dialog itself is built from the sample provider's result and shown without taking the focus,
        /// in each language and theme, for its picture. Without <c>--fakes</c>: the button is only looked at; the dialog is never opened, because
        /// opening it reads this PC.
        /// </summary>
        private async Task Pcb1Run()
        {
            if (await Open("hardware").ConfigureAwait(true) is not HardwarePage hardware)
            {
                Check("hardware: the page opens for the configuration code", false, LastOpen);
                return;
            }

            var share = hardware.ShareButton;
            var button = hardware.BuildCodeButton;
            Check("hardware: the configuration code button sits beside the share button (same bar, same height)", button.Parent is not null && button.Parent == share.Parent && button.Height == share.Height && button.Top == share.Top && button.Left > share.Left, button.Left + "," + button.Top + " " + button.Height + " / " + share.Left + "," + share.Top + " " + share.Height);
            Check("hardware: the configuration code button has its words", button.Text.Length > 0 && !SmokeText.IsPlaceholder(button.Text) && button.Text.Contains("/my-config", StringComparison.Ordinal), button.Text);
            var logged = Dialogs.Log.Count;
            if (!context.Services.Options.ForceFakes)
            {
                Check("hardware: without --fakes the smoke run does not press the configuration code button (it would read this PC)", button.Enabled && Dialogs.Log.Count == logged, button.Enabled.ToString());
                return;
            }

            if (context.BuildCode is not IFakeImplementation)
            {
                Check("hardware: --fakes has the sample configuration code", false, context.BuildCode.GetType().Name);
                return;
            }

            button.PerformClick();
            Check("hardware: while the code is made the button is off", !button.Enabled, button.Enabled.ToString());
            await Until(() => Dialogs.Log.Count > logged && button.Enabled, 8000).ConfigureAwait(true);
            var line = Dialogs.Log.Skip(logged).FirstOrDefault() ?? string.Empty;
            Check("hardware: the button gives the dialog the code, and is back afterwards", button.Enabled && line.Contains("| PCB1.", StringComparison.Ordinal) && !SmokeText.IsPlaceholder(line), line.Length > 40 ? line[..40] + "..." : line);

            // The plain sample (nothing left out) and the trimmed one (cut to the length limit by the real rules, with categories that could not be read):
            // both are the library's own results, shown in every look.
            var plain = await context.BuildCode.CreateAsync(CancellationToken.None).ConfigureAwait(true);
            Check("hardware: the sample code is a PCB1 code within the length limit", plain.Code.StartsWith("PCB1.", StringComparison.Ordinal) && plain.Code.Length <= BuildCode.MaxLength, plain.Code.Length + " characters");
            Check("hardware: the plain sample has nothing left out and every category read", plain.Notes.Count == 0 && plain.Unread.Count == 0, plain.Notes.Count + " notes, " + plain.Unread.Count + " unread");
            var trimmed = await new FakeBuildCode(SampleVariant.Trimmed).CreateAsync(CancellationToken.None).ConfigureAwait(true);
            Check("hardware: the trimmed sample is a PCB1 code within the length limit, with notes and unread categories from the rules", trimmed.Code.StartsWith("PCB1.", StringComparison.Ordinal) && trimmed.Code.Length <= BuildCode.MaxLength && trimmed.Notes.Count > 0 && trimmed.Unread.Count > 0, trimmed.Code.Length + " characters, " + trimmed.Notes.Count + " notes, " + trimmed.Unread.Count + " unread");
            Check("hardware: the trimmed sample has several kinds of note and several reasons for what could not be read", trimmed.Notes.Select(static n => n.Kind).Distinct().Count() >= 3 && trimmed.Unread.Select(static u => u.Reason).Distinct().Count() >= 3, string.Join(",", trimmed.Notes.Select(static n => n.Kind)) + " / " + string.Join(",", trimmed.Unread.Select(static u => u.Reason)));
            await ShootPcb1Dialog(plain, string.Empty, mayCut: false).ConfigureAwait(true);
            await ShootPcb1Dialog(trimmed, "-trimmed", mayCut: true).ConfigureAwait(true);
        }

        /// <summary>
        /// Shows the configuration code dialog of one result, without the focus, in every look, and checks it: the texts, the notes and the unread
        /// lines as the screen writes them, the cut-off text (headers by <see cref="SmokeClip"/>, every cell of the parts list by the grid itself) and the
        /// fit on the screen. With <paramref name="mayCut"/> a name or a detail that its column cuts is accepted when the tooltip holds the whole text (a
        /// long model name is real); without it nothing may be cut.
        /// </summary>
        private async Task ShootPcb1Dialog(BuildCodeResult result, string nameSuffix, bool mayCut)
        {
            var work = Screen.FromControl(form).WorkingArea;
            var kind = nameSuffix.Length == 0 ? "plain" : nameSuffix.TrimStart('-');
            await ForEachLook(async look =>
            {
                using var dialog = new BuildCodeDialog(context.Theme, result, _ => true);
                dialog.PreventActivation();
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Location = onScreen ? new Point(work.Left + 40, work.Top + 40) : new Point(-24000, -24000);
                dialog.TopMost = onScreen;
                dialog.Show();
                await Task.Delay(300).ConfigureAwait(true);
                if (dpiPercent > 0)
                {
                    await EmulateDpiOf(dialog).ConfigureAwait(true);
                }

                var partCount = BuildConfig.Categories.Sum(c => result.Config[c].Count);
                var noteLines = BuildCodeText.NoteLines(result.Notes).Count;
                Check($"hardware: the {kind} dialog shows the parts, the code, the notes and the unread categories ({look})", dialog.PartLines.Count == partCount && dialog.CodeText == result.Code && (noteLines == 0 ? dialog.NotesText.Length == 0 : dialog.NotesText.Split('\n').Length == noteLines) && dialog.UnreadText.Split('\n').Length == Math.Max(result.Unread.Count, 1), dialog.PartLines.Count + " parts, " + dialog.NotesText.Split('\n').Length + " note lines, " + dialog.UnreadText.Split('\n').Length + " unread");
                var all = string.Join("\n", dialog.PartLines.Select(static p => p.Category + p.Name + p.Detail + p.Count)) + "\n" + dialog.NotesText + "\n" + dialog.UnreadText;
                var missing = SmokeText.Missing(dialog).Distinct().Take(5).ToList();
                Check($"hardware: the {kind} dialog has no missing strings ({look})", missing.Count == 0 && !SmokeText.IsPlaceholder(all), string.Join(" | ", missing));
                var clipped = SmokeClip.Clipped(dialog).Distinct().Take(6).ToList();
                Check($"hardware: the {kind} dialog has no cut-off text ({look})", clipped.Count == 0, string.Join(" | ", clipped));
                var grid = dialog.PartsList;
                var cells = new List<string>();
                if (grid.Columns.Count >= 4)
                {
                    for (var row = 0; row < dialog.PartLines.Count; row++)
                    {
                        // A table cell that is too narrow ends in "..." and nothing else says so (SmokeClip looks at the headers only): the grid is asked cell by cell.
                        var (_, name, detail, _) = dialog.PartLines[row];
                        if (grid.IsCut(row, 1) && !(mayCut && grid.CellTip?.Invoke(row, 1) == name))
                        {
                            cells.Add(name);
                        }

                        if (grid.IsCut(row, 2) && !(mayCut && grid.CellTip?.Invoke(row, 2) == detail))
                        {
                            cells.Add(detail);
                        }
                    }
                }

                Check($"hardware: no part name or detail of the {kind} dialog is cut short without a tooltip that holds it ({look})", grid.Columns.Count >= 4 && cells.Count == 0, string.Join(" | ", cells.Take(4)) + " (columns " + string.Join("/", grid.Columns.Cast<ColumnHeader>().Select(static c => c.Width)) + ")");
                // The list ends on a whole row under its header in the window that is really on show (the test of it runs on a list the window has not laid out).
                await Until(() => grid.WholeRowsProblem(out _) is null, 2000).ConfigureAwait(true);
                var rowsProblem = grid.WholeRowsProblem(out var rowFacts);
                Check($"hardware: the parts list of the {kind} dialog starts under its header and ends on a whole row ({look})", rowsProblem is null, (rowsProblem is null ? string.Empty : rowsProblem + " | ") + rowFacts);
                Check($"hardware: the {kind} dialog fits on the screen ({look})", dialog.Height <= work.Height && dialog.Width <= work.Width && dialog.BackColor == context.Theme.Palette.Window, dialog.Width + "x" + dialog.Height + " in " + work.Width + "x" + work.Height);
                if (shotsDirectory is not null)
                {
                    // The code box and the parts list are the two things this picture is for: it is taken again while either is flat, and the picture kept is checked.
                    var regions = new List<(string, Control)> { ("the code box", dialog.CodeBox), ("the parts list", dialog.PartsList) };
                    _shots.Add(await Capture(shotsDirectory, $"{look}-hardware-pcb1{nameSuffix}{ScaleSuffix}.png", dialog, regions).ConfigureAwait(true));
                }

                dialog.Close();
            }).ConfigureAwait(true);
        }

        /// <summary>The same message that moves a window to a denser monitor, for a window the run opened itself (its own size scaled, left where it is).</summary>
        private async Task EmulateDpiOf(Form subject)
        {
            var target = 96 * dpiPercent / 100;
            if (subject.DeviceDpi == target)
            {
                return;
            }

            var scale = (double)target / subject.DeviceDpi;
            var rect = new NativeRect
            {
                Left = subject.Left,
                Top = subject.Top,
                Right = subject.Left + (int)(subject.Width * scale),
                Bottom = subject.Top + (int)(subject.Height * scale),
            };
            var memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<NativeRect>());
            try
            {
                System.Runtime.InteropServices.Marshal.StructureToPtr(rect, memory, false);
                SendMessage(subject.Handle, 0x02E0, (IntPtr)((target << 16) | target), memory);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(memory);
            }

            await Task.Delay(400).ConfigureAwait(true);
            Check("dpi emulation reached " + target + " (the configuration code dialog)", subject.DeviceDpi == target, "device dpi " + subject.DeviceDpi);
        }
    }
}
