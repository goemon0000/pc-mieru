using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using PcMieru.Update.Feed;
using PcMieru.Update.Launch;
using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;

namespace PcMieru.Gui;

internal static partial class SmokeRun
{
    private sealed partial class Runner
    {
        /// <summary>No request was made and no HTTP client was ever built in this process (the counters of the feed's one HTTP file, <see cref="FeedTraffic"/>).</summary>
        private static bool NoHttp() => FeedTraffic.Requests == 0 && FeedTraffic.ClientsCreated == 0;

        private static string HttpCounts() => FeedTraffic.Requests + " requests, " + FeedTraffic.ClientsCreated + " clients";

        /// <summary>
        /// What the report says of the runner's note about the last update (the file the real window shows once on its settings page, <see cref="UpdateResultNote"/>): the note that the install folder of THIS running program
        /// holds (<c>update\last-result.json</c>, and <c>blocked.json</c> for "blocked" against "retry"), as <c>message</c> (committed, blocked or retry), <c>to</c> and <c>from</c> (versions); null when there is none.
        /// It is only read, from the folder of the running exe (never from L), and never marks the note as shown: a trial that starts the installed version in this mode reads from the report that the version started and what it
        /// was told. The walks of the update screens point the window at folders of their own and do not look at this one.
        /// </summary>
        private static JsonObject? UpdateNoteOfThisInstall()
        {
            var program = Environment.ProcessPath;
            if (string.IsNullOrEmpty(program) || !Path.IsPathFullyQualified(program))
            {
                return null;
            }

            return UpdateResultNote.Read(InstallGate.InstallFolderOf(program)) is { } note
                ? new JsonObject { ["message"] = note.Message, ["to"] = note.To, ["from"] = note.From }
                : null;
        }

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

            await ShootStartGate().ConfigureAwait(true);

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
            Check("update: Ask shows its mode, the first-run line and buttons that are off, each with its reason", settings.UpdateModeBox.Text == UpdateText.ModeName(UpdateMode.Ask) && settings.UpdateStatusText == Strings.Get("gui.update.state.ask") && !settings.UpdateCheckNowButton.Enabled && !settings.UpdateNotesButton.Enabled && settings.UpdateCheckNowReasonText == Strings.Get("gui.update.reason.choosemode") && settings.UpdateNotesReasonText == Strings.Get("gui.update.reason.nocandidate"), settings.UpdateModeBox.Text + " / " + settings.UpdateStatusText + " / " + settings.UpdateCheckNowReasonText + " / " + settings.UpdateNotesReasonText);
            await Until(() => updates.PlacementSeen is not null, 4000).ConfigureAwait(true);
            Check("update: the settings show this version, no check yet and a writable install (the probe ran once, off the UI thread)", settings.UpdateLastText == Strings.Get("gui.update.lastcheck.never") && settings.UpdatePlacementText == Strings.Get("gui.update.placement.canswap") && settings.UpdateCandidateText == Strings.Get("gui.update.candidate.none"), settings.UpdateLastText + " / " + settings.UpdatePlacementText);
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
            Check("update: Notify turns the check-now button on and its reason line goes", settings.UpdateCheckNowButton.Enabled && settings.UpdateCheckNowReasonText.Length == 0 && settings.UpdateModeBox.Text == UpdateText.ModeName(UpdateMode.Notify) && fake.Calls == 0, settings.UpdateModeBox.Text);
            fake.Answer = (_, _) => candidate;
            var dialogsBefore = Dialogs.Log.Count;
            settings.UpdateCheckNowButton.PerformClick();
            await Until(() => updates.Pending.IsCompleted && fake.Calls == 1, 4000).ConfigureAwait(true);
            Check("update: 今すぐ確認 asks the (fake) feed once and finds the newer version", fake.Calls == 1 && updates.Last is { Kind: UpdateCheckKind.Candidate } && settings.UpdateCandidateText == found.ToString(), "calls " + fake.Calls + ", " + settings.UpdateCandidateText);
            var notice = Strings.Format("gui.update.notice", Fmt.Dict(("version", found.ToString())));
            var noticeLabel = form.Controls.Find("updateNotice", searchAllChildren: true).OfType<Label>().SingleOrDefault();
            Check("update: the candidate is said quietly in a status bar line that stays (no dialog, no flash)", notices.Count == 0 && Dialogs.Log.Count == dialogsBefore && context.UpdateNotice == notice && noticeLabel is { Visible: true } && noticeLabel.Text == notice, notices.Count + " flashes, " + (Dialogs.Log.Count - dialogsBefore) + " dialogs, " + noticeLabel?.Text);
            Check("update: the candidate line, the status line and the notes button agree (and the line is not link blue)", settings.UpdateStatusText == Strings.Format("gui.update.state.candidate", Fmt.Dict(("version", found.ToString()))) && settings.UpdateStatusTone == CellTone.Normal && settings.UpdateNotesButton.Enabled && settings.UpdateNotesReasonText.Length == 0 && settings.NotesUrl == UpdateEndpoints.ReleasePageUrl(found.Tag), settings.UpdateStatusText + " / " + settings.NotesUrl);
            settings.UpdateCheckNowButton.PerformClick();
            await Until(() => updates.Pending.IsCompleted && fake.Calls == 2, 4000).ConfigureAwait(true);
            Check("update: the same version is not announced again in this run", fake.Calls == 2 && notices.Count == 0 && context.UpdateNotice == notice, "calls " + fake.Calls + ", " + notices.Count + " flashes");
            settings.UpdateNotesButton.PerformClick();
            Check("update: the notes button opens the release page of this project and nothing else", opened.Count == 1 && opened[0] == UpdateEndpoints.ReleasePageUrl(found.Tag) && UpdateEndpoints.IsReleasePage(opened[0]), string.Join(", ", opened));
            await ShootUpdateSettings(settings, "candidate").ConfigureAwait(true);

            // --- a check that did not finish (no network): it judged no release, so the candidate and the notice stay ---
            fake.Answer = (_, _) => UpdateCheck.Refuse(UpdateRefusal.NetworkFailure, "no answer", fetched: true);
            settings.UpdateCheckNowButton.PerformClick();
            await Until(() => updates.Pending.IsCompleted && fake.Calls == 3, 4000).ConfigureAwait(true);
            var failure = Strings.Format("gui.update.state.failedcandidate", Fmt.Dict(("version", found.ToString())));
            Check("update: a failed check says so and that the version shown is the one found earlier, and keeps the candidate, its notes button and the notice", updates.Last is { Kind: UpdateCheckKind.Refused } && settings.UpdateStatusText == failure && settings.UpdateStatusTone == CellTone.Warning && settings.UpdateCandidateText == found.ToString() && settings.UpdateNotesButton.Enabled && context.UpdateNotice == notice && noticeLabel is { Visible: true }, settings.UpdateStatusText + " / " + settings.UpdateCandidateText);
            await ShootUpdateSettings(settings, "failure").ConfigureAwait(true);

            // --- a release whose own rules did not hold: the reason is said in words, the candidate and the notice go ---
            fake.Answer = (_, _) => UpdateCheck.Refuse(UpdateRefusal.AssetMissing, "SHA256.txt", fetched: true, found);
            settings.UpdateCheckNowButton.PerformClick();
            await Until(() => updates.Pending.IsCompleted && fake.Calls == 4, 4000).ConfigureAwait(true);
            var refusal = Strings.Format("gui.update.state.refused", Fmt.Dict(("version", found.ToString()), ("reason", UpdateText.RefusalText(UpdateRefusal.AssetMissing))));
            Check("update: a refused release says why, offers no notes, and the candidate and the notice go", updates.Last is { Kind: UpdateCheckKind.Refused } && settings.UpdateStatusText == refusal && settings.UpdateStatusTone == CellTone.Warning && !settings.UpdateNotesButton.Enabled && settings.UpdateNotesReasonText == Strings.Get("gui.update.reason.refused") && settings.UpdateCandidateText == Strings.Get("gui.update.candidate.none") && context.UpdateNotice is null && noticeLabel is { Visible: false }, settings.UpdateStatusText);
            await ShootUpdateSettings(settings, "refusal").ConfigureAwait(true);

            // --- a wait that GitHub asked for, on an install that cannot be written to ---
            fake.Placement = new Placement(PlacementKind.ReadOnly, "sample");
            await updates.RefreshPlacement().ConfigureAwait(true);
            fake.Answer = (_, _) => UpdateCheck.Wait(updates.Now.AddHours(24), fetched: true);
            settings.UpdateCheckNowButton.PerformClick();
            await Until(() => updates.Pending.IsCompleted && fake.Calls == 5, 4000).ConfigureAwait(true);
            var until = updates.Last!.WaitUntil!.Value;
            var untilText = UpdateText.When(until, updates.Now);
            Check("update: a wait is said with the time until which nothing is asked, and the button is off with that time beside it", updates.Last is { Kind: UpdateCheckKind.Waiting } && settings.UpdateStatusText == Strings.Format("gui.update.state.waiting", Fmt.Dict(("time", untilText))) && settings.UpdateStatusTone == CellTone.Dim && !settings.UpdateCheckNowButton.Enabled && settings.UpdateCheckNowReasonText == Strings.Format("gui.update.reason.backoff", Fmt.Dict(("time", untilText))), settings.UpdateStatusText + " / " + settings.UpdateCheckNowReasonText);
            Check("update: a folder that cannot be written to says it will only notify and why", settings.UpdatePlacementText == UpdateText.PlacementText(new Placement(PlacementKind.ReadOnly, "sample")) && settings.UpdatePlacementTone == CellTone.Warning, settings.UpdatePlacementText);
            await ShootUpdateSettings(settings, "backoff").ConfigureAwait(true);

            // --- Off: the button goes off, the feed is never asked again ---
            fake.Placement = new Placement(PlacementKind.CanSwap, "sample");
            settings.UpdateModeBox.SelectedIndex = settings.UpdateModeBox.FindStringExact(UpdateText.ModeName(UpdateMode.Off));
            var after = await updates.CheckNowAsync().ConfigureAwait(true);
            Check("update: Off turns the button off, says so with its reason, and asks nobody", updates.Mode == UpdateMode.Off && !settings.UpdateCheckNowButton.Enabled && settings.UpdateCheckNowReasonText == Strings.Get("gui.update.reason.choosemode") && settings.UpdateStatusText == Strings.Get("gui.update.state.off") && after is null && fake.Calls == 5 && !updates.TimersRunning && context.UpdateNotice is null, updates.Mode + ", calls " + fake.Calls);
            await UpdateHandOffWalk(settings, fake, candidate).ConfigureAwait(true);
            Check("update: the walk went through the window's own controller with no real request", NoHttp(), HttpCounts());
        }

        /// <summary>
        /// Automatic with an update waiting in the stage folder (DESIGN-B 5a), in the four states the hand-off and the runner's last result add to the settings page. Each is made the way
        /// the program makes it: the two deferrals by the window's own <see cref="UpdateHandOff"/> (the gate rows as Restart Manager gives them, classified by the same function; the
        /// runner is never started and the window never closed) set on the controller through <see cref="UpdateController.SetDeferral"/>; the two notes by <see cref="UpdateResultNote.Read"/>
        /// of a temporary install folder this walk owns, with the files the runner writes (<c>last-result.json</c>, and <c>blocked.json</c> after a rollback). Nothing is written outside it.
        /// </summary>
        private async Task UpdateHandOffWalk(SettingsPage settings, FakeUpdateService fake, UpdateCheck candidate)
        {
            const string BuildBefore = "f0e1d2c3-b4a5-4968-8776-655443322110";
            var updates = context.Updates;
            var running = updates.Running;
            var found = candidate.Version!.Value;
            var root = Path.Combine(Path.GetTempPath(), "pcmieru-smoke-update-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(root);

                // Automatic, the newer version found by the (fake) feed, and the stage the program would have unpacked for it (the fake keeps it in its status as state.json does).
                updates.ChooseMode(UpdateMode.Automatic);
                await updates.RefreshPlacement().ConfigureAwait(true);
                fake.Answer = (_, _) => candidate;
                await updates.CheckNowAsync().ConfigureAwait(true);
                var staged = new StagedUpdate
                {
                    Folder = Path.Combine(root, "cache", "0123456789ab"),
                    Version = found.ToString(),
                    BuildId = candidate.Manifest!.BuildId,
                    Tag = found.Tag,
                    ManifestSha256 = new string('b', 64),
                };
                fake.SetStatus(fake.ReadStatus() with { Staged = staged });
                Check("update: Automatic with a staged update starts no timer and asks nobody beyond the one check", updates.Mode == UpdateMode.Automatic && updates.Allowed && !updates.TimersRunning && updates.Status().Staged == staged && updates.Deferral is null && NoHttp(), updates.Mode + ", timers " + updates.TimersRunning + ", " + HttpCounts());

                var idle = new UpdateIdleFacts(1000, false, false, false, false, RecordingActivity.Idle, false, false);
                var own = new HashSet<int> { 1 };
                IReadOnlyList<RestartManager.Holder> rows = [];
                var runnerStarts = 0;
                var closes = 0;
                var handOff = new UpdateHandOff(
                    () => updates.Allowed ? (updates.Mode, updates.Status().Staged) : (UpdateMode.Off, null),
                    () => idle,
                    () => RestartManager.Classify(rows, own),
                    _ =>
                    {
                        runnerStarts++;
                        return ApplyHandOffResult.No(ApplyRefusal.NoAnswer, "the walk starts no runner");
                    },
                    updates.SetDeferral,
                    () => closes++,
                    () => 61_000);
                var noticeLabel = form.Controls.Find("updateNotice", searchAllChildren: true).OfType<Label>().SingleOrDefault();

                // --- a: pcmi (the command line tool of an AI app) has the gate file open ---
                rows = [new RestartManager.Holder(1, "PCMieru"), new RestartManager.Holder(2, "pcmi")];
                await handOff.TryAsync().ConfigureAwait(true);
                var pcmi = Strings.Get("gui.update.defer.pcmi");
                Check("update: an update that waits for pcmi says so in the status line and the status bar, and neither starts the runner nor closes the window", updates.Deferral is { Holders: GateHolders.Pcmi, Refusal: null } && settings.UpdateStatusText == pcmi && context.UpdateNotice == pcmi && noticeLabel is { Visible: true } && noticeLabel.Text == pcmi && runnerStarts == 0 && closes == 0, settings.UpdateStatusText + " / " + noticeLabel?.Text + ", runner " + runnerStarts + ", close " + closes);
                await ShootUpdateSettings(settings, "deferred-pcmi", expectedStatus: () => Strings.Get("gui.update.defer.pcmi")).ConfigureAwait(true);

                // --- b: another program (here another PC Mieru) has it open ---
                rows = [new RestartManager.Holder(1, "PCMieru"), new RestartManager.Holder(3, "PCMieru")];
                await handOff.TryAsync().ConfigureAwait(true);
                var other = Strings.Get("gui.update.defer.other");
                Check("update: an update that waits for another program says so in the status line and the status bar, and neither starts the runner nor closes the window", updates.Deferral is { Holders: GateHolders.Other, Refusal: null } && settings.UpdateStatusText == other && context.UpdateNotice == other && noticeLabel is { Visible: true } && noticeLabel.Text == other && runnerStarts == 0 && closes == 0, settings.UpdateStatusText + " / " + noticeLabel?.Text + ", runner " + runnerStarts + ", close " + closes);
                await ShootUpdateSettings(settings, "deferred-other", expectedStatus: () => Strings.Get("gui.update.defer.other")).ConfigureAwait(true);

                // --- after the swap: the stage is gone and the feed has nothing newer; what is left is the runner's note ---
                updates.SetDeferral(null);
                fake.Answer = static (_, _) => UpdateCheck.NoCandidate(fetched: true);
                await updates.CheckNowAsync().ConfigureAwait(true);
                fake.SetStatus(fake.ReadStatus() with { Staged = null });
                Check("update: with the stage gone nothing is deferred and the status bar line goes", updates.Deferral is null && settings.UpdateStatusText != pcmi && settings.UpdateStatusText != other && context.UpdateNotice is null && noticeLabel is { Visible: false }, settings.UpdateStatusText + " / " + context.UpdateNotice);

                string InstallWithNote(string name, string outcome, string reason, AppVersion from, AppVersion to, bool blocked)
                {
                    var paths = new TxPaths(Path.Combine(root, name));
                    Directory.CreateDirectory(paths.Update);
                    File.WriteAllText(paths.LastResult, $$"""
                        { "format": "pcm-last-result/1", "kind": "apply", "utc": "2026-11-25T01:02:03Z", "tx": "0123456789ab",
                          "outcome": "{{outcome}}", "reason": "{{reason}}", "detail": "",
                          "from": { "version": "{{from}}", "buildId": "{{BuildBefore}}" },
                          "to": { "version": "{{to}}", "buildId": "{{candidate.Manifest!.BuildId}}" } }
                        """);
                    if (blocked)
                    {
                        File.WriteAllText(paths.Blocked, $$"""
                            { "format": "pcm-blocked/1", "tags": [
                                { "version": "{{to}}", "buildId": "{{candidate.Manifest!.BuildId}}", "reason": "launch-failed", "utc": "2026-11-25T01:02:03Z" }
                            ] }
                            """);
                    }

                    return paths.Install;
                }

                async Task<UpdateResultNote?> ShowNote(string install)
                {
                    // The window reads the note once when the page asks for it: the walk points that at its own folder and opens the page again (the same call the real start makes).
                    context.UseLastUpdate(() => UpdateResultNote.Read(install));
                    settings.OnActivated();
                    await updates.RefreshPlacement().ConfigureAwait(true);
                    return context.LastUpdate.Value;
                }

                // --- c: updated (the note after a committed swap; this program is the new version) ---
                var before = new AppVersion(running.Major, Math.Max(running.Minor - 1, 0), 0);
                var committed = await ShowNote(InstallWithNote("committed", "committed", "none", before, running, blocked: false)).ConfigureAwait(true);
                var updated = Strings.Format("gui.update.result.committed", Fmt.Dict(("to", running.ToString())));
                Check("update: after a committed swap the settings say it updated to this version, once (the stamp is kept as shown), and the status line is the usual one", committed is { Message: "committed" } && settings.UpdateResultText == updated && context.Settings.GetValue(committed.Key) == committed.Stamp && updates.Deferral is null && settings.UpdateStatusText.StartsWith(Strings.Get("gui.update.state.current").Split('{')[0], StringComparison.Ordinal), settings.UpdateResultText + " / " + settings.UpdateStatusText);
                await ShootUpdateSettings(settings, "updated", expectedResult: () => Strings.Format("gui.update.result.committed", Fmt.Dict(("to", running.ToString())))).ConfigureAwait(true);

                // --- d: rolled back (the note after a rollback; the new version could not be started and is blocked, this program is the old one) ---
                var rolledBack = await ShowNote(InstallWithNote("rolledback", "rolledBack", "launchFailed", running, found, blocked: true)).ConfigureAwait(true);
                var back = Strings.Format("gui.update.result.blocked", Fmt.Dict(("to", found.ToString()), ("from", running.ToString())));
                Check("update: after a rollback the settings say it went back to the old version and will not install the new one again, once (the stamp is kept as shown)", rolledBack is { Message: "blocked" } && settings.UpdateResultText == back && context.Settings.GetValue(rolledBack.Key) == rolledBack.Stamp && updates.Deferral is null, settings.UpdateResultText);
                await ShootUpdateSettings(settings, "rolledback", expectedResult: () => Strings.Format("gui.update.result.blocked", Fmt.Dict(("to", found.ToString()), ("from", running.ToString())))).ConfigureAwait(true);
            }
            finally
            {
                context.UseLastUpdate(null);
                updates.SetDeferral(null);
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // a folder in the temporary folder that stays is not a failure of the screens
                }
            }
        }

        /// <summary>
        /// The settings section of the update check in one state, in every look: its words, no cut-off text, the theme, and (with --shots) its picture.
        /// <paramref name="expectedStatus"/> and <paramref name="expectedResult"/> give the exact words the status line and the note about the last update must have in the
        /// language of each look (a language change writes the section again, so the words are asked again too).
        /// </summary>
        private async Task ShootUpdateSettings(SettingsPage page, string state, Func<string>? expectedStatus = null, Func<string>? expectedResult = null)
        {
            await ForEachLook(async look =>
            {
                page.ScrollToUpdateSection();
                await Task.Delay(200).ConfigureAwait(true);
                var texts = new[] { page.UpdateStatusText, page.UpdateResultText, page.UpdateCandidateText, page.UpdateLastText, page.UpdatePlacementText, page.UpdateCheckNowReasonText, page.UpdateNotesReasonText, page.UpdateModeBox.Text, page.UpdateCheckNowButton.Text, page.UpdateNotesButton.Text, page.ThemeBox.Text };
                var missing = SmokeText.Missing(page).Concat(SmokeText.Missing(form)).Distinct().Take(5).ToList();
                Check($"update: the settings section has its texts, {state} ({look})", missing.Count == 0 && !texts.Any(SmokeText.IsPlaceholder), string.Join(" | ", missing));
                if (expectedStatus is not null)
                {
                    Check($"update: the status line is exactly the words of {state} in this language ({look})", page.UpdateStatusText == expectedStatus(), page.UpdateStatusText + " / " + expectedStatus());
                }

                if (expectedResult is not null)
                {
                    Check($"update: the note about the last update is exactly the words of {state} in this language ({look})", page.UpdateResultText == expectedResult(), page.UpdateResultText + " / " + expectedResult());
                }

                var clipped = SmokeClip.Clipped(form).Distinct().Take(6).ToList();
                Check($"update: the settings section has no cut-off text, {state} ({look})", clipped.Count == 0, string.Join(" | ", clipped));
                var off = SmokeTheme.Mismatches(form, context.Theme.Palette).Take(5).ToList();
                Check($"update: the settings section follows the theme, {state} ({look})", off.Count == 0, string.Join(" | ", off));
                Check($"update: the theme box shows the theme of this look, {state} ({look})", page.ThemeBox.SelectedIndex == (int)context.Theme.Mode, page.ThemeBox.Text + " / " + context.Theme.Mode);
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
                var unsigned = Strings.Current == Language.Ja ? "電子署名（発行元の証明）がない版も入れ替えます" : "without a Windows digital signature (proof of the publisher)";
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

        /// <summary>
        /// The window of the start gate (DESIGN-B 4) in each state that stops a start and has a window: busy, an interrupted update, a recovery that failed, a gate file that is not there and one
        /// that is refused (access denied). Each state is made for real (a small install folder under the temporary folder, the gate opened the way every start opens it), then shown without taking the
        /// focus in every look, at every scale the run is made at (all five states at 200 percent too). This run's own gate is not touched. The folder line under an interrupted update is also checked
        /// for what it must be: broken only after backslashes, and every line of it inside the label.
        ///
        /// An interrupted update is shown in four more ways, with 「回復する」 (a recovery that is available: the walk gives the window one that starts nothing), after the button was pressed
        /// and recover.exe could not be started (the window says so and the button is gone), and in a process that runs elevated (no button; the text says to open PC Mieru again with normal rights). The five
        /// plain states have no recover.exe in their folder, so they show no button, as in a real folder that has none.
        /// </summary>
        private async Task ShootStartGate()
        {
            var root = Path.Combine(Path.GetTempPath(), "pcmieru-smoke-gate-" + Guid.NewGuid().ToString("N")[..8]);
            var cleanups = new List<Action>();
            var work = Screen.FromControl(form).WorkingArea;
            try
            {
                Directory.CreateDirectory(root);
                string Install(string name, bool gate = true)
                {
                    var folder = Path.Combine(root, name);
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, "PCMieru.exe"), "not a program");
                    if (gate)
                    {
                        File.WriteAllBytes(Path.Combine(folder, GateFile.FileName), GateFile.Bytes());
                    }

                    return Path.Combine(folder, "PCMieru.exe");
                }

                void UpdateFile(string exe, string name)
                {
                    var update = Path.Combine(Path.GetDirectoryName(exe)!, GateFile.UpdateFolderName);
                    Directory.CreateDirectory(update);
                    File.WriteAllText(Path.Combine(update, name), name == GateFile.JournalName ? "{}" : string.Empty);
                }

                var busy = Install("busy");
                var holder = InstallGate.EnterExclusive(busy);
                cleanups.Add(holder.Dispose);
                var pending = Install("pending");
                UpdateFile(pending, GateFile.JournalName);
                var failed = Install("failed");
                UpdateFile(failed, GateFile.RecoveryFailedName);
                var missing = Install("missing", gate: false);
                var denied = Install("denied");
                // Access denied: the read right of the gate file is taken from this user, in this run's own folder only, and given back before the folder is removed.
                var deniedGate = new FileInfo(Path.Combine(Path.GetDirectoryName(denied)!, GateFile.FileName));
                var user = WindowsIdentity.GetCurrent().User!;
                var rule = new FileSystemAccessRule(user, FileSystemRights.ReadData, AccessControlType.Deny);
                var security = deniedGate.GetAccessControl();
                security.AddAccessRule(rule);
                deniedGate.SetAccessControl(security);
                cleanups.Add(() =>
                {
                    var back = deniedGate.GetAccessControl();
                    back.RemoveAccessRule(rule);
                    deniedGate.SetAccessControl(back);
                });
                Check("gate: the busy state is held by a swapper of its own for the walk", holder.IsOpen, holder.Detail);

                // Recovery: "" = the real one (these folders have no recover.exe: no button), "button" = one that is available, "startfails" = available, and the button is pressed: it cannot be started, "elevated" = this process is elevated: no button.
                var states = new (string Name, GateOutcome Outcome, string Exe, string Recovery)[]
                {
                    ("busy", GateOutcome.Busy, busy, string.Empty),
                    ("recoverypending", GateOutcome.RecoveryPending, pending, string.Empty),
                    ("recoveryfailed", GateOutcome.RecoveryFailed, failed, string.Empty),
                    ("missinggate", GateOutcome.MissingGate, missing, string.Empty),
                    ("denied", GateOutcome.Denied, denied, string.Empty),
                    ("recoverypending-button", GateOutcome.RecoveryPending, pending, "button"),
                    ("recoveryfailed-button", GateOutcome.RecoveryFailed, failed, "button"),
                    ("recoverypending-startfailed", GateOutcome.RecoveryPending, pending, "startfails"),
                    ("recoverypending-elevated", GateOutcome.RecoveryPending, pending, "elevated"),
                };
                foreach (var (name, outcome, exe, recoveryMode) in states)
                {
                    await ForEachLook(async look =>
                    {
                        var entry = InstallGate.EnterStartup(exe);
                        Check($"gate: the {name} state is the real outcome of the gate ({look})", entry.Outcome == outcome, entry.Outcome + ": " + entry.Detail);
                        IGateRecovery? recovery = recoveryMode.Length == 0 ? null : new ShotRecovery(starts: recoveryMode != "startfails", elevated: recoveryMode == "elevated");
                        using var window = new StartGateWindow(context.Theme, entry, () => InstallGate.EnterStartup(exe), recovery);
                        window.PreventActivation();
                        window.StartPosition = FormStartPosition.Manual;
                        window.Location = onScreen ? new Point(work.Left + 40, work.Top + 40) : new Point(-24000, -24000);
                        window.TopMost = onScreen;
                        window.Show();
                        await Task.Delay(300).ConfigureAwait(true);
                        if (dpiPercent > 0)
                        {
                            await EmulateDpiOf(window).ConfigureAwait(true);
                        }

                        var busyOne = outcome == GateOutcome.Busy;
                        var folderOne = outcome is GateOutcome.RecoveryPending or GateOutcome.RecoveryFailed;
                        if (recoveryMode == "startfails")
                        {
                            // The person pressed 「回復する」 and recover.exe could not be started: the window says so, gives the new folder as the way on, and the button is gone.
                            var pressed = window.RecoverNow();
                            await Task.Delay(300).ConfigureAwait(true);
                            Check($"gate: the {name} window, after the press, is still open, shows the plain failure text and no button ({look})", !pressed && !window.RecoveryStarted && !window.RecoverOffered && !window.RecoverButton.Visible && window.ReasonText == Strings.Get("gui.gate.recover.failed") && window.AcceptButton == window.CloseButton, window.ReasonText);
                        }
                        else if (recoveryMode == "elevated")
                        {
                            Check($"gate: the {name} window offers no 「回復する」 and says to open PC Mieru again with normal rights ({look})", window.RecoverElevated && !window.RecoverOffered && !window.RecoverButton.Visible && window.ReasonText == Strings.Get(outcome == GateOutcome.RecoveryFailed ? "gui.gate.recoveryfailed.elevated" : "gui.gate.recover.elevated") && window.AcceptButton == window.CloseButton, window.ReasonText);
                        }
                        else
                        {
                            var expectButton = recoveryMode == "button";
                            Check($"gate: the {name} window has 「回復する」 exactly when a recovery is available ({look})", window.RecoverOffered == expectButton && window.RecoverButton.Visible == expectButton && (!expectButton || (window.AcceptButton == window.RecoverButton && window.RecoverButton.Text == Strings.Get("gui.gate.recover"))), "offered " + window.RecoverOffered + ", visible " + window.RecoverButton.Visible + ", text " + window.RecoverButton.Text);
                        }

                        Check($"gate: the {name} window has one reason text{(folderOne ? " and the dim folder line" : string.Empty)} in this language, no missing strings ({look})", window.ReasonText.Length > 0 && (folderOne == window.HintText.Length > 0) && !SmokeText.IsPlaceholder(window.ReasonText) && !SmokeText.IsPlaceholder(window.HintText) && SmokeText.Missing(window).Distinct().Take(5).Count() == 0, window.ReasonText + " | " + window.HintText);
                        Check($"gate: the {name} window names no file, no path and no tool in its reason ({look})", !window.ReasonText.Contains(GateFile.RecoverNoteName, StringComparison.Ordinal) && !window.ReasonText.Contains('\\', StringComparison.Ordinal), window.ReasonText);
                        if (folderOne)
                        {
                            var folderText = Path.GetDirectoryName(entry.GatePath)!;
                            Check($"gate: the {name} window's folder line is the folder, broken only after backslashes, every line inside the label ({look})", window.HintProblem() is null && window.HintText.Replace("\n", string.Empty, StringComparison.Ordinal) == Strings.Format("gui.gate.folder", Fmt.Dict(("path", folderText))), window.HintProblem() + " | " + window.HintText.Replace("\n", " / ", StringComparison.Ordinal));
                        }
                        Check($"gate: the {name} window offers 「もう一度試す」 only when it is busy ({look})", window.RetryOffered == busyOne && window.RetryButton.Visible == busyOne, "retry " + window.RetryOffered);
                        var clipped = SmokeClip.Clipped(window).Distinct().Take(6).ToList();
                        Check($"gate: the {name} window has no cut-off text ({look})", clipped.Count == 0, string.Join(" | ", clipped));
                        var off = SmokeTheme.Mismatches(window, context.Theme.Palette).Take(5).ToList();
                        Check($"gate: the {name} window follows the theme ({look})", off.Count == 0 && window.BackColor == context.Theme.Palette.Window, string.Join(" | ", off));
                        Check($"gate: the {name} window fits on the screen ({look})", window.Height <= work.Height && window.Width <= work.Width, window.Width + "x" + window.Height + " in " + work.Width + "x" + work.Height);
                        if (shotsDirectory is not null)
                        {
                            _shots.Add(await Capture(shotsDirectory, $"{look}-gate-{name}{ScaleSuffix}.png", window).ConfigureAwait(true));
                        }

                        window.Close();
                    }).ConfigureAwait(true);
                }
            }
            finally
            {
                foreach (var cleanup in Enumerable.Reverse(cleanups))
                {
                    try
                    {
                        cleanup();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        // the folder of this walk is removed next, and it is in the temporary folder: a cleanup that fails is not a failure of the screens
                    }
                }

                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // a folder in the temporary folder that stays is not a failure of the screens
                }
            }
        }

        /// <summary>The recovery of the start gate walk: always available, starts nothing (<paramref name="starts"/> false: the start is refused, as when recover.exe cannot be run).</summary>
        private sealed class ShotRecovery(bool starts, bool elevated = false) : IGateRecovery
        {
            public bool IsAvailable(GateEntry entry) => true;

            public bool IsElevated => elevated;

            public bool Start(GateEntry entry, out string detail)
            {
                detail = "the walk starts nothing";
                return starts;
            }
        }
    }
}
