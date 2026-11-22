using PcMieru.Checks;

namespace PcMieru.Gui;

internal static partial class SmokeRun
{
    private sealed partial class Runner
    {
        /// <summary>
        /// One look of the recording tab in every language and theme: the texts are all there, nothing is cut off without a tooltip that holds it, the
        /// list ends on a whole row; and the picture (the looks the scale is checked on take it again at 200%: the empty tab, a recording, a summary, a failed run, a cut-off run and an old record).
        /// </summary>
        private async Task ShootRecord(ChecksPage page, RecordTab tab, string name, bool atScale, Func<Task>? settle = null)
        {
            await ForEachLook(async look =>
            {
                page.SelectTab("record");
                if (settle is not null)
                {
                    await settle().ConfigureAwait(true);
                }

                await Task.Delay(150).ConfigureAwait(true);
                var missing = SmokeText.Missing(page).Concat(SmokeText.Missing(form)).Distinct().Take(5).ToList();
                Check($"record: {name} has its texts ({look})", missing.Count == 0, string.Join(" | ", missing));
                var clipped = SmokeClip.Clipped(form).Distinct().Take(6).ToList();
                Check($"record: {name} has no cut-off text ({look})", clipped.Count == 0, string.Join(" | ", clipped));
                var cuts = tab.Summary.UnreadableCuts().Take(4).ToList();
                Check($"record: no cell of the summary in {name} is cut short without a tooltip that holds it ({look})", cuts.Count == 0, string.Join(" | ", cuts));
                var listCuts = new List<string>();
                for (var row = 0; row < tab.Runs.Count; row++)
                {
                    for (var column = 0; column < 3; column++)
                    {
                        if (tab.List.IsCut(row, column))
                        {
                            listCuts.Add($"{row}:{column} [{tab.ListText(row, column)}]");
                        }
                    }
                }

                Check($"record: no cell of the list in {name} is cut ({look})", listCuts.Count == 0, string.Join(" | ", listCuts.Take(4)));

                // With a run open the summary is what the person came for: it has most of the height under the tabs (the bar can move it, a window that is low
                // keeps a row of the list and the summary's minimum, so the share is measured on the list the page gave it).
                if (tab.SelectedRunId is not null)
                {
                    var divided = tab.Body.ClientSize.Height - tab.Splitter.Height;
                    var share = divided <= 0 ? 0 : tab.DetailPanel.Height * 100 / divided;
                    Check($"record: with a run open the summary has at least 60 % of the height in {name} ({look})", divided > 0 && tab.DetailPanel.Height * 100 >= divided * 60, $"{tab.DetailPanel.Height} of {divided} = {share} %, list {tab.List.Height} ({tab.List.RowsThatFit(tab.List.Height)} rows)");
                }

                // The readings are the table's job: with a run open, several of them are whole on the first screen of the table, and no header is cut.
                if (tab.SelectedRunId is not null && tab.Summary.RowCount > 0 && tab.Summary.TopRow == 0)
                {
                    var readings = tab.Summary.Rows.Count(static r => r.Kind is SummaryRowKind.Metric or SummaryRowKind.NotMeasured);
                    var wholeReadings = tab.Summary.ReadingsWholeAtTop();
                    Check($"record: the summary in {name} shows several readings whole without scrolling ({look})", wholeReadings >= Math.Min(4, readings), $"{wholeReadings} of {readings} readings whole in {tab.Summary.RowsThatFit(tab.Summary.Height)} lines ({tab.Summary.Height} px, line {tab.Summary.MeasuredRowHeight} px, columns {string.Join("/", tab.Summary.Columns.Cast<ColumnHeader>().Select(static c => c.Width))} of {tab.Summary.ClientSize.Width} px, headers need {string.Join("/", Enumerable.Range(0, tab.Summary.Columns.Count).Select(tab.Summary.HeaderNeed))})");
                    var headers = new List<string>();
                    for (var column = 0; column < tab.Summary.Columns.Count; column++)
                    {
                        if (tab.Summary.Columns[column].Width < tab.Summary.HeaderNeed(column))
                        {
                            headers.Add($"{column} [{tab.Summary.Columns[column].Text}] needs {tab.Summary.HeaderNeed(column)} has {tab.Summary.Columns[column].Width}");
                        }
                    }

                    Check($"record: every header of the summary in {name} is whole ({look})", headers.Count == 0, string.Join(" | ", headers));
                }

                var facts = string.Empty;

                // A list with no run in it (the empty tab, the first recording not yet made) has no row to end on; its words are checked above.
                var whole = tab.List.VirtualListSize == 0 ? null : tab.List.WholeRowsProblem(out facts);
                Check($"record: the list in {name} ends on a whole row ({look})", whole is null, whole is null ? string.Empty : whole + " " + facts);

                // The record that is chosen is among the rows on show: a list that is a few rows high had it scrolled out of sight (the picture of an interrupted run
                // showed the third row of four, and the chosen one was the fourth).
                var selectedRow = tab.List.SelectedRow;
                if (selectedRow >= 0)
                {
                    Check($"record: the selected record in {name} is inside the rows on show ({look})", tab.List.IsRowVisible(selectedRow), $"row {selectedRow} of {tab.List.VirtualListSize}, first row on show {tab.List.TopRow}, {tab.List.RowsThatFit(tab.List.Height)} rows fit in {tab.List.Height} px");
                }

                if (shotsDirectory is not null && (dpiPercent == 0 || atScale))
                {
                    _shots.Add(await Capture(shotsDirectory, $"{look}-record-{name}{ScaleSuffix}.png").ConfigureAwait(true));
                }
            }).ConfigureAwait(true);
        }

        /// <summary>
        /// The recording tab. Without <c>--fakes</c> it only checks that the smoke run made no recorder and no store. With it: the empty tab, a recording
        /// (elapsed time, marks), the page switch (the recording goes on, the status bar says so), the stop that waits for the recorder, a finished run
        /// with its summary (a hole and two sections), a failed run, a cut-off run and an old record. The recorder and the store are in memory: nothing is
        /// written to the disk, and the folder the real recorder would use is checked before and after.
        /// </summary>
        private async Task RecordRun()
        {
            var hub = context.Recording;
            if (!context.Services.Options.ForceFakes)
            {
                Check("record: without --fakes the smoke run made no recorder, no store and no recording", !hub.BackendCreated && !hub.StoreCreated && !hub.IsActive, $"recorder {hub.BackendCreated}, store {hub.StoreCreated}, phase {hub.Phase}");
                return;
            }

            if (context.RecordingFake is not { } kit)
            {
                Check("record: --fakes has the fake recorder and the in-memory store", false, "no fake kit");
                return;
            }

            if (await Open("checks").ConfigureAwait(true) is not ChecksPage page || page.TabOf("record") is not RecordTab tab)
            {
                Check("record: the checks page has the recording tab", false, LastOpen);
                return;
            }

            var uiThread = Environment.CurrentManagedThreadId;
            var documentsFolder = Path.GetDirectoryName(RunStore.DefaultRunsFolder()) ?? string.Empty;
            var folderBefore = FolderStamp(documentsFolder);

            // ---- empty ----
            page.SelectTab("record");
            await Until(() => tab.IsReady && tab.View is not (HistoryView.NotLoaded or HistoryView.Loading), 4000).ConfigureAwait(true);
            Check("record: the first look at the tab lists nothing and says so", tab.View == HistoryView.Empty && tab.Runs.Count == 0, tab.View + " / " + tab.Runs.Count);
            Check("record: the list was read off the window's thread", kit.Store.ListThreads.Count > 0 && kit.Store.ListThreads.All(t => t != uiThread), string.Join(",", kit.Store.ListThreads) + " vs " + uiThread);
            Check("record: reading the list made no recorder", !hub.BackendCreated && kit.Backend.StartCalls == 0, $"recorder {hub.BackendCreated}, starts {kit.Backend.StartCalls}");
            Check("record: with nothing recording only start is on offer", tab.StartButton.Enabled && !tab.StopButton.Enabled && !tab.MarkButton.Enabled && !tab.CsvButton.Enabled && !tab.DeleteButton.Enabled, $"{tab.StartButton.Enabled}/{tab.StopButton.Enabled}/{tab.MarkButton.Enabled}/{tab.CsvButton.Enabled}/{tab.DeleteButton.Enabled}");
            Check("record: the tab says where recordings go", tab.FolderLabel.Text.Length > 0, tab.FolderLabel.Text);
            Check("record: the status bar says nothing about a recording that is not there", !form.RecordingIndicator.Visible, form.RecordingIndicator.Text);
            await ShootRecord(page, tab, "empty", true).ConfigureAwait(true);

            // ---- recording ----
            await tab.PressStart().ConfigureAwait(true);
            await Until(() => hub.Phase == RecordingPhase.Recording, 3000).ConfigureAwait(true);
            Check("record: start begins a recording", hub.Phase == RecordingPhase.Recording && kit.Backend.StartCalls == 1 && hub.BackendCreated, hub.Phase + ", starts " + kit.Backend.StartCalls);
            await Until(() => hub.Elapsed >= TimeSpan.FromSeconds(2.2), 5000).ConfigureAwait(true);
            await tab.PressMark(MarkerKind.BenchStart).ConfigureAwait(true);
            await tab.PressMark(MarkerKind.StressStart).ConfigureAwait(true);
            Check("record: marks are put in the run while it records", hub.Marks.Count == 2 && kit.Backend.MarkKinds.SequenceEqual([MarkerKind.BenchStart, MarkerKind.StressStart]), string.Join(",", kit.Backend.MarkKinds));
            Check("record: while recording stop and the marks are on offer and start is not", !tab.StartButton.Enabled && tab.StopButton.Enabled && tab.MarkButton.Enabled && tab.MarkItems.Count == 5 && tab.MarkItems.All(static i => i.Enabled), $"{tab.StartButton.Enabled}/{tab.StopButton.Enabled}/{tab.MarkButton.Enabled}/{tab.MarkItems.Count}");
            Check("record: the state line moves on with the time", !tab.StateLabel.Text.Contains(RecordFormat.Elapsed(TimeSpan.Zero), StringComparison.Ordinal), tab.StateLabel.Text);
            Check("record: the status bar shows the recording while the tab is open", form.RecordingIndicator.Visible && form.RecordingIndicator.Text.Length > 0, form.RecordingIndicator.Text);
            await Until(() => tab.View == HistoryView.List && tab.Runs.Count == 1, 3000).ConfigureAwait(true);
            tab.SelectRun(0);
            await Until(() => tab.DetailState == RecordDetailState.Running, 2000).ConfigureAwait(true);
            Check("record: the run that is recording is in the list as recording and cannot be deleted", tab.Runs.Count == 1 && tab.Runs[0].Status == RunStatus.Recording && !tab.DeleteButton.Enabled && !tab.CsvButton.Enabled, tab.DetailState + ", delete " + tab.DeleteButton.Enabled);
            var clock = tab.ListText(0, 1);
            Check("record: the length of the run that is recording is a clock that runs, not a dash and not about", System.Text.RegularExpressions.Regex.IsMatch(clock, @"^\d+:\d\d$") && System.Text.RegularExpressions.Regex.IsMatch(tab.DetailHead.Text, @"\d+:\d\d"), $"list [{clock}] details [{tab.DetailHead.Text}]");
            await ShootRecord(page, tab, "recording", true).ConfigureAwait(true);

            // ---- another tab, another page: the recording goes on and the status bar says so ----
            page.SelectTab("history");
            await Task.Delay(150).ConfigureAwait(true);
            Check("record: another tab does not stop the recording", hub.Phase == RecordingPhase.Recording && kit.Backend.StopCalls == 0, hub.Phase + ", stops " + kit.Backend.StopCalls);
            await Open("performance").ConfigureAwait(true);
            Check("record: another page does not stop the recording", hub.Phase == RecordingPhase.Recording && kit.Backend.StopCalls == 0, hub.Phase + ", stops " + kit.Backend.StopCalls);
            Check("record: the status bar shows the recording on another page", form.RecordingIndicator.Visible && form.RecordingIndicator.Text.Contains(':'), form.RecordingIndicator.Text);
            await ForEachLook(async look =>
            {
                await Task.Delay(150).ConfigureAwait(true);
                var clipped = SmokeClip.Clipped(form).Distinct().Take(6).ToList();
                Check($"record: the status bar with the recording has no cut-off text ({look})", clipped.Count == 0, string.Join(" | ", clipped));
                Check($"record: the status bar's recording label is whole ({look})", form.RecordingIndicator.Visible && TextRenderer.MeasureText(form.RecordingIndicator.Text, form.RecordingIndicator.Font).Width <= form.RecordingIndicator.Width, form.RecordingIndicator.Text + " in " + form.RecordingIndicator.Width);
                if (shotsDirectory is not null && dpiPercent == 0)
                {
                    _shots.Add(await Capture(shotsDirectory, $"{look}-record-indicator{ScaleSuffix}.png").ConfigureAwait(true));
                }
            }).ConfigureAwait(true);
            form.ClickRecordingIndicator();
            await Until(() => form.CurrentPage is ChecksPage { CurrentId: "record" }, 3000).ConfigureAwait(true);
            Check("record: a click on the status bar's label opens the recording tab", form.CurrentPage is ChecksPage { CurrentId: "record" }, form.CurrentPage?.Id + " / " + (form.CurrentPage as ChecksPage)?.CurrentId);

            // ---- stopping: the recorder takes its time ----
            page.SelectTab("record");
            kit.Backend.HangStop = true;
            var stopping = tab.PressStop();
            await Until(() => hub.Phase == RecordingPhase.Stopping, 2000).ConfigureAwait(true);
            Check("record: while the recording is saved nothing can be pressed but the list", hub.Phase == RecordingPhase.Stopping && !tab.StartButton.Enabled && !tab.StopButton.Enabled && !tab.MarkButton.Enabled, $"{hub.Phase} {tab.StartButton.Enabled}/{tab.StopButton.Enabled}/{tab.MarkButton.Enabled}");
            Check("record: the state line and the status bar say it is being saved", tab.StateLabel.Text == Strings.Get("gui.rtab.state.stopping") && form.RecordingIndicator.Text == Strings.Get("gui.rtab.saving"), tab.StateLabel.Text + " / " + form.RecordingIndicator.Text);
            var savingWords = Strings.Get("gui.rtab.status.stopping");
            Check("record: while it is saved the row and the details of the run say it is being stopped and saved", tab.Runs.Count == 1 && tab.ListText(0, 2) == savingWords && tab.DetailHead.Text.Contains(savingWords, StringComparison.Ordinal), $"list [{tab.ListText(0, 2)}] details [{tab.DetailHead.Text}]");
            await ShootRecord(page, tab, "stopping", false).ConfigureAwait(true);
            kit.Backend.ReleaseStop();
            kit.Backend.HangStop = false;
            await stopping.ConfigureAwait(true);
            await Until(() => hub.Phase == RecordingPhase.Idle && tab.View == HistoryView.List && tab.DetailState == RecordDetailState.Found && tab.DetailTask.IsCompleted, 4000).ConfigureAwait(true);
            Check("record: stop ends the run for the user and the list has it as completed", hub.Phase == RecordingPhase.Idle && kit.Backend.LastStopReason == RunStopReason.User && tab.Runs.Count == 1 && tab.Runs[0].Status == RunStatus.Completed, hub.Phase + ", " + kit.Backend.LastStopReason + ", " + tab.Runs.Count);
            Check("record: the run just finished is open with its summary", tab.DetailState == RecordDetailState.Found && tab.Summary.RowCount > 0 && tab.DeleteButton.Enabled && tab.CsvButton.Enabled, tab.DetailState + ", " + tab.Summary.RowCount + " rows");
            Check("record: the status bar's label is gone once it is stopped", !form.RecordingIndicator.Visible, form.RecordingIndicator.Text);

            // ---- a finished run with a hole and two sections ----
            FakeRunData.Seed(kit.Store, kit.Folder, DateTime.UtcNow);
            tab.Reload();
            await tab.LoadTask.ConfigureAwait(true);
            await Until(() => tab.IsReady && tab.Runs.Count >= 4, 3000).ConfigureAwait(true);
            var completedRow = RowOf(tab, FakeRunData.CompletedId);
            tab.SelectRun(completedRow);
            await Until(() => tab.DetailState == RecordDetailState.Found && tab.DetailTask.IsCompleted && tab.Summary.RowCount > 0 && tab.SelectedRunId == FakeRunData.CompletedId, 3000).ConfigureAwait(true);
            var rows = tab.Summary.Rows;
            Check("record: the finished run's summary has a reading that was not measured, and it is said so, never 0", rows.Any(static r => r.Kind == SummaryRowKind.NotMeasured) && rows.Where(static r => r.Kind == SummaryRowKind.NotMeasured).All(static r => r.Min != "0" && r.Avg != "0" && r.Max != "0" && r.Time.Length > 0), string.Join(" | ", rows.Where(static r => r.Kind == SummaryRowKind.NotMeasured).Select(static r => r.Item + "=" + r.Min + " / " + r.Time)));
            Check("record: the summary has a bench section and a load section and a warning", rows.Count(static r => r.Kind == SummaryRowKind.Section) == 2 && rows.Any(static r => r.Kind == SummaryRowKind.Warning), rows.Count(static r => r.Kind == SummaryRowKind.Section) + " sections");
            Check("record: the finished run can be opened as CSV and deleted", tab.CsvButton.Enabled && tab.DeleteButton.Enabled, $"{tab.CsvButton.Enabled}/{tab.DeleteButton.Enabled}");
            await ShootRecord(page, tab, "summary", true).ConfigureAwait(true);
            await ShootRecord(page, tab, "summary-sections", false, () =>
            {
                // The readings come first; the shot starts at the group under them (why clocks were held back, what happened together, the sections).
                ScrollToTop(tab.Summary, FirstRowOf(tab.Summary, SummaryRowKind.Heading, afterKind: SummaryRowKind.Record));
                return Task.CompletedTask;
            }).ConfigureAwait(true);

            // "no" at the question keeps the run (a smoke run never answers a dialog "yes").
            var logged = Dialogs.Log.Count;
            await tab.PressDelete().ConfigureAwait(true);
            Check("record: delete asks first and a no keeps the run", Dialogs.Log.Count > logged && kit.Store.DeleteCalls == 0 && kit.Store.Info(FakeRunData.CompletedId) is not null, "asked " + (Dialogs.Log.Count - logged) + ", deletes " + kit.Store.DeleteCalls);

            // ---- a failed run and a run that was cut off ----
            tab.SelectRun(RowOf(tab, FakeRunData.FailedId));
            await Until(() => tab.SelectedRunId == FakeRunData.FailedId && tab.DetailState == RecordDetailState.Found && tab.DetailTask.IsCompleted, 3000).ConfigureAwait(true);
            var summaryTop = tab.Summary.RowRect(0)?.Top ?? -1;
            Check("record: a short summary opened after a long one starts at its first row, under its header", tab.Summary.RowCount > 0 && tab.Summary.TopRow == 0 && summaryTop >= 0 && summaryTop <= tab.Summary.HeaderHeight + 3, "rows " + tab.Summary.RowCount + ", first on show " + tab.Summary.TopRow + ", first row at " + summaryTop + " px, header " + tab.Summary.HeaderHeight + " px");
            await ShootRecord(page, tab, "failed", true).ConfigureAwait(true);
            var interruptedRow = RowOf(tab, FakeRunData.InterruptedId);
            tab.SelectRun(interruptedRow);
            await Until(() => tab.SelectedRunId == FakeRunData.InterruptedId && tab.DetailState == RecordDetailState.Found && tab.DetailTask.IsCompleted, 3000).ConfigureAwait(true);
            Check("record: the length of a run that was cut off is about, not exact", interruptedRow >= 0 && RecordFormat.IsApproximate(tab.Runs[interruptedRow]) && tab.ListText(interruptedRow, 1) != RecordFormat.ExactLength(735), interruptedRow >= 0 ? tab.ListText(interruptedRow, 1) : "no row");
            await ShootRecord(page, tab, "interrupted", true).ConfigureAwait(true);

            // ---- an old record: read only ----
            tab.ToggleLegacy();
            await Until(() => RowOf(tab, FakeRunData.LegacyId) >= 0, 2000).ConfigureAwait(true);
            tab.SelectRun(RowOf(tab, FakeRunData.LegacyId));
            await Until(() => tab.SelectedRunId == FakeRunData.LegacyId && tab.DetailState == RecordDetailState.Found && tab.DetailTask.IsCompleted, 3000).ConfigureAwait(true);
            Check("record: an old record opens read only: it can be looked at and not deleted", tab.DetailState == RecordDetailState.Found && !tab.DeleteButton.Enabled && tab.DetailHead.Text.Contains(Strings.Get("gui.rtab.detail.legacy"), StringComparison.Ordinal), tab.DetailState + ", " + tab.DetailHead.Text);
            await ShootRecord(page, tab, "legacy", true).ConfigureAwait(true);
            tab.ToggleLegacy();
            await Until(() => RowOf(tab, FakeRunData.LegacyId) < 0, 2000).ConfigureAwait(true);
            await Task.Delay(300).ConfigureAwait(true);
            var shorter = tab.List.WholeRowsProblem(out var shorterFacts);
            // All its rows fit: it starts at its first row, right under the header. With more rows than room it may stay where it was scrolled, as long as it shows no gap.
            var allFit = tab.List.VirtualListSize <= tab.List.RowsThatFit(tab.List.Height);
            Check("record: the list made shorter again starts under its header (not scrolled past its first row)", shorter is null && (tab.List.TopRow == 0 || !allFit), (shorter ?? "whole") + " " + shorterFacts + ", first row on show " + tab.List.TopRow + ", all rows fit " + allFit);

            Check("record: the whole walk made no recorder write and no folder under Documents", FolderStamp(documentsFolder) == folderBefore && !hub.IsActive, FolderStamp(documentsFolder) + " vs " + folderBefore);
            page.SelectTab("system");
        }

        /// <summary>The row of the list that shows this run; -1 when it is not listed.</summary>
        private static int RowOf(RecordTab tab, Guid runId)
        {
            for (var i = 0; i < tab.Runs.Count; i++)
            {
                if (tab.Runs[i].RunId == runId)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>The first line (of the lines drawn: a row that wraps takes several) of a row of this kind that comes after a row of another kind (the heading of the group under the readings); 0 when there is none.</summary>
        private static int FirstRowOf(RunSummaryGrid grid, SummaryRowKind kind, SummaryRowKind afterKind)
        {
            var seen = false;
            for (var i = 0; i < grid.RowCount; i++)
            {
                if (grid.Rows[i].Kind == afterKind)
                {
                    seen = true;
                }
                else if (seen && grid.Rows[i].Kind == kind)
                {
                    return Math.Max(grid.FirstLineOf(i), 0);
                }
            }

            return 0;
        }
    }
}
