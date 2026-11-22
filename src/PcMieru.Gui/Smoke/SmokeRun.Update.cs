using PcMieru.Update.Feed;

namespace PcMieru.Gui;

internal static partial class SmokeRun
{
    private sealed partial class Runner
    {
        /// <summary>No request was made and no HTTP client was ever built in this process (the counters of the feed's one HTTP file, <see cref="FeedTraffic"/>).</summary>
        private static bool NoHttp() => FeedTraffic.Requests == 0 && FeedTraffic.ClientsCreated == 0;

        private static string HttpCounts() => FeedTraffic.Requests + " requests, " + FeedTraffic.ClientsCreated + " clients";

        /// <summary>
        /// The update check, walked with the fake feed (DESIGN-B 4): the first-run dialog, the settings section in Ask, in Notify with a candidate, with a refusal and
        /// during a wait that GitHub asked for, and Off. Every state is looked at in four looks. The whole walk asserts that **no HTTP request was made and no HTTP
        /// client built** (the fake service answers from a script), and that nothing was written under the data folder's update folder. Without <c>--fakes</c> the run
        /// only checks that nothing started: the real service would ask GitHub.
        /// </summary>
        private async Task UpdateRun()
        {
            if (context.FakeUpdates is not { } fake)
            {
                Check("update: without --fakes the smoke run makes no update timer and no request", !context.Updates.TimersRunning && NoHttp() && context.Updates.Mode == UpdateMode.Ask, HttpCounts() + ", mode " + context.Updates.Mode);
                return;
            }

            var updateFolder = Path.Combine(Path.GetDirectoryName(GuiSettings.DefaultPath) ?? string.Empty, "update");
            var folderBefore = FolderStamp(updateFolder);
            var logged = Dialogs.Log.Count;
            var notices = new List<string>();
            void Heard(string message) => notices.Add(message);
            context.StatusMessage += Heard;
            var opened = new List<string>();
            var starter = ShellActions.Start;
            try
            {
                ShellActions.Start = info => opened.Add(info.FileName);
                await UpdateWalk(fake, notices, opened, logged).ConfigureAwait(true);
            }
            finally
            {
                ShellActions.Start = starter;
                context.StatusMessage -= Heard;
                fake.Answer = static (_, _) => UpdateCheck.NoCandidate(fetched: true);
                fake.Placement = new Placement(PlacementKind.CanSwap, "sample");
                context.Updates.ChooseMode(UpdateMode.Ask);
            }

            var folderAfter = FolderStamp(updateFolder);
            Check("update: the walk wrote nothing under the data folder's update folder", !folderBefore.StartsWith(StampUnreadable, StringComparison.Ordinal) && !folderAfter.StartsWith(StampUnreadable, StringComparison.Ordinal) && folderAfter == folderBefore, folderBefore + " -> " + folderAfter);
            Check("update: the whole walk made no HTTP request and built no HTTP client", NoHttp(), HttpCounts());
        }

        private async Task UpdateWalk(FakeUpdateService fake, List<string> notices, List<string> opened, int logged)
        {
            var updates = context.Updates;
            var candidate = fake.Candidate();
            var found = candidate.Version!.Value;

            if (await Open("settings").ConfigureAwait(true) is not SettingsPage settings)
            {
                Check("update: the settings page opens", false, LastOpen);
                return;
            }

            // --- Ask: the first run. Nothing is checked, nothing is scheduled, the button that would ask is off. ---
            Check("update: a first run is in Ask", updates.Mode == UpdateMode.Ask && !updates.Allowed, updates.Mode.ToString());
            Check("update: Ask makes no timer and asks nobody", !updates.TimersRunning && fake.Calls == 0 && NoHttp(), "timers " + updates.TimersRunning + ", calls " + fake.Calls + ", " + HttpCounts());
            Check("update: Ask shows its mode, the first-run line and a button that is off", settings.UpdateModeBox.Text == UpdateText.ModeName(UpdateMode.Ask) && settings.UpdateStatusText == Strings.Get("gui.update.state.ask") && !settings.UpdateCheckNowButton.Enabled && !settings.UpdateNotesButton.Enabled, settings.UpdateModeBox.Text + " / " + settings.UpdateStatusText);
            Check("update: the settings show this version, no check yet and a writable install", settings.UpdateLastText == Strings.Get("gui.update.lastcheck.never") && settings.UpdatePlacementText == Strings.Get("gui.update.placement.canswap") && settings.UpdatePlacementNoteText.Length == 0 && settings.UpdateCandidateText == Strings.Get("gui.update.candidate.none"), settings.UpdateLastText + " / " + settings.UpdatePlacementText);
            await ShootUpdateSettings(settings, "ask").ConfigureAwait(true);

            // --- the first-run dialog: only the smoke run shows it (the app's own start would only log it) ---
            var asked = UpdateChoiceDialog.Open(form, context.Theme, updates);
            Check("update: in an unattended run the first-run dialog only logs and chooses nothing", !asked && Dialogs.Log.Count == logged + 1 && updates.Mode == UpdateMode.Ask && fake.Calls == 0, "log " + (Dialogs.Log.Count - logged) + ", mode " + updates.Mode);
            await ShootUpdateChoice().ConfigureAwait(true);
            Check("update: closing the first-run dialog without a choice leaves Ask and asks nobody", updates.Mode == UpdateMode.Ask && fake.Calls == 0 && !updates.TimersRunning && NoHttp(), updates.Mode + ", calls " + fake.Calls);

            // --- 「決定」 with the preselected 「知らせるだけ」: saved first, then the mode is Notify ---
            using (var confirm = new UpdateChoiceDialog(context.Theme, updates))
            {
                var saved = confirm.ConfirmNow();
                Check("update: 決定 saves the preselected choice (Notify only)", saved && updates.Mode == UpdateMode.Notify && updates.Allowed && context.Settings.UpdateMode == UpdateMode.Notify, saved + ", " + updates.Mode);
            }

            // --- Notify with a candidate ---
            await Open("settings").ConfigureAwait(true);
            Check("update: Notify turns the check-now button on", settings.UpdateCheckNowButton.Enabled && settings.UpdateModeBox.Text == UpdateText.ModeName(UpdateMode.Notify) && fake.Calls == 0, settings.UpdateModeBox.Text);
            fake.Answer = (_, _) => candidate;
            var dialogsBefore = Dialogs.Log.Count;
            settings.UpdateCheckNowButton.PerformClick();
            await Until(() => updates.Pending.IsCompleted && fake.Calls == 1, 4000).ConfigureAwait(true);
            Check("update: 今すぐ確認 asks the (fake) feed once and finds the newer version", fake.Calls == 1 && updates.Last is { Kind: UpdateCheckKind.Candidate } && settings.UpdateCandidateText == found.ToString(), "calls " + fake.Calls + ", " + settings.UpdateCandidateText);
            var notice = Strings.Format("gui.update.notice", Fmt.Dict(("version", found.ToString())));
            Check("update: the candidate is said once, quietly, on the status bar (no dialog)", notices.Count == 1 && notices[0] == notice && Dialogs.Log.Count == dialogsBefore, notices.Count + " notices, " + (Dialogs.Log.Count - dialogsBefore) + " dialogs");
            Check("update: the candidate line, the status line and the notes button agree", settings.UpdateStatusText == Strings.Format("gui.update.state.candidate", Fmt.Dict(("version", found.ToString()))) && settings.UpdateStatusTone == CellTone.Accent && settings.UpdateNotesButton.Enabled && settings.NotesUrl == UpdateEndpoints.ReleasePageUrl(found.Tag), settings.UpdateStatusText + " / " + settings.NotesUrl);
            settings.UpdateCheckNowButton.PerformClick();
            await Until(() => updates.Pending.IsCompleted && fake.Calls == 2, 4000).ConfigureAwait(true);
            Check("update: the same version is not announced again in this run", fake.Calls == 2 && notices.Count == 1, "calls " + fake.Calls + ", " + notices.Count + " notices");
            settings.UpdateNotesButton.PerformClick();
            Check("update: the notes button opens the release page of this project and nothing else", opened.Count == 1 && opened[0] == UpdateEndpoints.ReleasePageUrl(found.Tag) && UpdateEndpoints.IsReleasePage(opened[0]), string.Join(", ", opened));
            await ShootUpdateSettings(settings, "candidate").ConfigureAwait(true);

            // --- a refusal: the reason is said in words ---
            fake.Answer = (_, _) => UpdateCheck.Refuse(UpdateRefusal.AssetMissing, "SHA256.txt", fetched: true);
            settings.UpdateCheckNowButton.PerformClick();
            await Until(() => updates.Pending.IsCompleted && fake.Calls == 3, 4000).ConfigureAwait(true);
            var refusal = Strings.Format("gui.update.state.refused", Fmt.Dict(("reason", UpdateText.RefusalText(UpdateRefusal.AssetMissing))));
            Check("update: a refused release says why and offers no notes", updates.Last is { Kind: UpdateCheckKind.Refused } && settings.UpdateStatusText == refusal && settings.UpdateStatusTone == CellTone.Accent && !settings.UpdateNotesButton.Enabled && settings.UpdateCandidateText == Strings.Get("gui.update.candidate.none"), settings.UpdateStatusText);
            await ShootUpdateSettings(settings, "refusal").ConfigureAwait(true);

            // --- a wait that GitHub asked for, on an install that cannot be written to ---
            fake.Placement = new Placement(PlacementKind.ReadOnly, "sample");
            fake.Answer = (_, _) => UpdateCheck.Wait(updates.Now.AddHours(24), fetched: true);
            settings.UpdateCheckNowButton.PerformClick();
            await Until(() => updates.Pending.IsCompleted && fake.Calls == 4, 4000).ConfigureAwait(true);
            Check("update: a wait is said with the time until which nothing is asked", updates.Last is { Kind: UpdateCheckKind.Waiting } && settings.UpdateStatusText.Contains(UpdateText.When(updates.Last.WaitUntil!.Value), StringComparison.Ordinal) && settings.UpdateStatusTone == CellTone.Accent, settings.UpdateStatusText);
            Check("update: a folder that cannot be written to says it will only notify", settings.UpdatePlacementText == Strings.Get("gui.update.placement.readonly") && settings.UpdatePlacementNoteText == Strings.Get("gui.update.placement.note"), settings.UpdatePlacementText + " / " + settings.UpdatePlacementNoteText);
            await ShootUpdateSettings(settings, "backoff").ConfigureAwait(true);

            // --- Off: the button goes off, the feed is never asked again ---
            fake.Placement = new Placement(PlacementKind.CanSwap, "sample");
            settings.UpdateModeBox.SelectedIndex = settings.UpdateModeBox.FindStringExact(UpdateText.ModeName(UpdateMode.Off));
            var after = await updates.CheckNowAsync().ConfigureAwait(true);
            Check("update: Off turns the button off, says so, and asks nobody", updates.Mode == UpdateMode.Off && !settings.UpdateCheckNowButton.Enabled && settings.UpdateStatusText == Strings.Get("gui.update.state.off") && after is null && fake.Calls == 4 && !updates.TimersRunning, updates.Mode + ", calls " + fake.Calls);
            Check("update: the walk went through the window's own controller with no real request", NoHttp(), HttpCounts());
        }

        /// <summary>The settings section of the update check in one state, in every look: its words, no cut-off text, the theme, and (with --shots) its picture.</summary>
        private async Task ShootUpdateSettings(SettingsPage page, string state)
        {
            await ForEachLook(async look =>
            {
                page.ScrollToUpdateSection();
                await Task.Delay(200).ConfigureAwait(true);
                var texts = new[] { page.UpdateStatusText, page.UpdateCandidateText, page.UpdateLastText, page.UpdatePlacementText, page.UpdatePlacementNoteText, page.UpdateModeBox.Text, page.UpdateCheckNowButton.Text, page.UpdateNotesButton.Text };
                var missing = SmokeText.Missing(page).Concat(SmokeText.Missing(form)).Distinct().Take(5).ToList();
                Check($"update: the settings section has its texts, {state} ({look})", missing.Count == 0 && !texts.Any(SmokeText.IsPlaceholder), string.Join(" | ", missing));
                var clipped = SmokeClip.Clipped(form).Distinct().Take(6).ToList();
                Check($"update: the settings section has no cut-off text, {state} ({look})", clipped.Count == 0, string.Join(" | ", clipped));
                var off = SmokeTheme.Mismatches(form, context.Theme.Palette).Take(5).ToList();
                Check($"update: the settings section follows the theme, {state} ({look})", off.Count == 0, string.Join(" | ", off));
                if (shotsDirectory is not null)
                {
                    _shots.Add(await Capture(shotsDirectory, $"{look}-update-settings-{state}{ScaleSuffix}.png").ConfigureAwait(true));
                }
            }).ConfigureAwait(true);
        }

        /// <summary>
        /// The first-run dialog in every look, shown without taking the focus: 「知らせるだけ」 is the one that is selected, 「自動」 says in its own line that versions
        /// that are not signed are also installed without asking, nothing is missing or cut off, and it fits the screen. Closing it chooses nothing.
        /// </summary>
        private async Task ShootUpdateChoice()
        {
            var work = Screen.FromControl(form).WorkingArea;
            await ForEachLook(async look =>
            {
                using var dialog = new UpdateChoiceDialog(context.Theme, context.Updates);
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

                Check($"update: the first-run dialog preselects 知らせるだけ ({look})", dialog.NotifyChoice.Checked && !dialog.AutomaticChoice.Checked && !dialog.OffChoice.Checked && dialog.SelectedMode == UpdateMode.Notify, dialog.SelectedMode.ToString());
                var unsigned = Strings.Current == Language.Ja ? "署名のない版" : "not signed";
                Check($"update: the 自動 line says that unsigned versions are installed without asking ({look})", dialog.AutomaticLine.Contains(unsigned, StringComparison.Ordinal) && dialog.AutomaticChoice.Text == UpdateText.ModeName(UpdateMode.Automatic), dialog.AutomaticLine);
                var missing = SmokeText.Missing(dialog).Distinct().Take(5).ToList();
                Check($"update: the first-run dialog has no missing strings ({look})", missing.Count == 0 && !SmokeText.IsPlaceholder(dialog.AutomaticLine) && !SmokeText.IsPlaceholder(dialog.OkButton.Text), string.Join(" | ", missing));
                var clipped = SmokeClip.Clipped(dialog).Distinct().Take(6).ToList();
                Check($"update: the first-run dialog has no cut-off text ({look})", clipped.Count == 0, string.Join(" | ", clipped));
                Check($"update: the first-run dialog fits on the screen ({look})", dialog.Height <= work.Height && dialog.Width <= work.Width && dialog.BackColor == context.Theme.Palette.Window, dialog.Width + "x" + dialog.Height + " in " + work.Width + "x" + work.Height);
                if (shotsDirectory is not null)
                {
                    _shots.Add(await Capture(shotsDirectory, $"{look}-update-firstrun{ScaleSuffix}.png", dialog).ConfigureAwait(true));
                }

                dialog.Close();
            }).ConfigureAwait(true);
        }
    }
}
