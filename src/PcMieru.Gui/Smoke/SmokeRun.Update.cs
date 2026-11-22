using System.Security.AccessControl;
using System.Security.Principal;
using PcMieru.Update.Feed;
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
            Check("update: the walk went through the window's own controller with no real request", NoHttp(), HttpCounts());
        }

        /// <summary>The settings section of the update check in one state, in every look: its words, no cut-off text, the theme, and (with --shots) its picture.</summary>
        private async Task ShootUpdateSettings(SettingsPage page, string state)
        {
            await ForEachLook(async look =>
            {
                page.ScrollToUpdateSection();
                await Task.Delay(200).ConfigureAwait(true);
                var texts = new[] { page.UpdateStatusText, page.UpdateCandidateText, page.UpdateLastText, page.UpdatePlacementText, page.UpdateCheckNowReasonText, page.UpdateNotesReasonText, page.UpdateModeBox.Text, page.UpdateCheckNowButton.Text, page.UpdateNotesButton.Text, page.ThemeBox.Text };
                var missing = SmokeText.Missing(page).Concat(SmokeText.Missing(form)).Distinct().Take(5).ToList();
                Check($"update: the settings section has its texts, {state} ({look})", missing.Count == 0 && !texts.Any(SmokeText.IsPlaceholder), string.Join(" | ", missing));
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

                var states = new (string Name, GateOutcome Outcome, string Exe)[]
                {
                    ("busy", GateOutcome.Busy, busy),
                    ("recoverypending", GateOutcome.RecoveryPending, pending),
                    ("recoveryfailed", GateOutcome.RecoveryFailed, failed),
                    ("missinggate", GateOutcome.MissingGate, missing),
                    ("denied", GateOutcome.Denied, denied),
                };
                foreach (var (name, outcome, exe) in states)
                {
                    await ForEachLook(async look =>
                    {
                        var entry = InstallGate.EnterStartup(exe);
                        Check($"gate: the {name} state is the real outcome of the gate ({look})", entry.Outcome == outcome, entry.Outcome + ": " + entry.Detail);
                        using var window = new StartGateWindow(context.Theme, entry, () => InstallGate.EnterStartup(exe));
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
    }
}
