using PcMieru.Core.Siblings;

namespace PcMieru.Gui;

internal static partial class SmokeRun
{
    /// <summary>The command lines the smoke run's stand-in for the Parts Life launcher was asked to start (it starts nothing; a run that presses nothing leaves this empty).</summary>
    private static readonly List<string> PartyStarts = [];

    /// <summary>The files the scratch roaming folder holds when the run begins (relative to it): the old app's save and nothing else.</summary>
    private static readonly string[] PartyScratchFiles = [Path.Combine("PartsLife", "state.json")];

    /// <summary>
    /// A folder of its own under the temporary folder, standing for the roaming application-data folder, with a dummy save of the old app (<c>{}</c>): the smoke run looks only here, never at the
    /// real one (DESIGN-C 4.1 items 1-2). Null when it could not be made (the checks then say so).
    /// </summary>
    private static string? MakePartyScratch()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "pcmieru-smoke-roaming-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(root, "PartsLife"));
            File.WriteAllText(Path.Combine(root, "PartsLife", "state.json"), "{}");
            return root;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void RemovePartyScratch(string? root)
    {
        try
        {
            if (root is not null && Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception)
        {
            // a temporary folder that stays is not a failure of the run
        }
    }

    private sealed partial class Runner
    {
        /// <summary>
        /// The notice that old Parts Life progress is waiting (DESIGN-C 4.1 items 1-2), seen against a scratch roaming folder that holds a dummy save of the old app: the hint says so, the Parts Life page
        /// says so in its line (in every look), the notice of the first start only LOGS in an unattended run and chooses nothing, the notice window is shown in every look, and nothing was written under the
        /// scratch folder (and so nothing under the real roaming folder, which the run never names).
        /// </summary>
        private async Task PartyImportRun()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await PartyImportWalk().ConfigureAwait(true);
            }
            finally
            {
                Timing["partyImportMs"] = Math.Round(watch.Elapsed.TotalMilliseconds);
            }
        }

        private async Task PartyImportWalk()
        {
            if (context.PartyRoamingRoot is not { } root)
            {
                Check("party import: the run has a scratch roaming folder to look in", false, "none could be made");
                return;
            }

            var hint = PartyImportHint.Check(root);
            Check("party import: the hint finds the dummy save of the old app in the scratch folder", hint == PartyImportHintState.FromPartsLife, hint.ToString());
            Check("party import: the hint finds nothing in a folder that is not there", PartyImportHint.Check(Path.Combine(root, "no-such-folder")) == PartyImportHintState.None);

            // --- the Parts Life page: its line, in every look ---
            await Open("overview").ConfigureAwait(true);
            if (await Open("party").ConfigureAwait(true) is not PartyPage page)
            {
                Check("party import: the Parts Life page opens", false, LastOpen);
                return;
            }

            await Until(() => page.HintPending.IsCompleted, 4000).ConfigureAwait(true);
            Check("party import: the page looked once and found the old save", page.HintPending.IsCompleted && page.Hint == PartyImportHintState.FromPartsLife, page.Hint.ToString());
            await ForEachLook(async look =>
            {
                await Open("party").ConfigureAwait(true);
                var line = page.WaitingLabel;
                Check($"party import: the page says that old progress is waiting ({look})", line.Visible && line.Text == Strings.Get("gui.party.import.waiting") && !SmokeText.IsPlaceholder(line.Text), line.Text);
                var missing = SmokeText.Missing(page).Distinct().Take(5).ToList();
                Check($"party import: the page has no missing strings ({look})", missing.Count == 0, string.Join(" | ", missing));
                var clipped = SmokeClip.Clipped(page).Distinct().Take(6).ToList();
                Check($"party import: the page's line is not cut off ({look})", clipped.Count == 0, string.Join(" | ", clipped));
            }).ConfigureAwait(true);

            // --- the notice of the first start: in an unattended run it only logs ---
            var logged = Dialogs.Log.Count;
            var pageBefore = form.CurrentPage?.Id;
            var starts = PartyStarts.Count;
            await form.NoticePartyImportAsync().ConfigureAwait(true);
            var entry = Dialogs.Log.Count > logged ? Dialogs.Log[^1] : string.Empty;
            Check("party import: in an unattended run the notice only logs and chooses nothing", Dialogs.Log.Count == logged + 1 && entry.Contains(PartyImportDialog.LeadText(PartyImportHintState.FromPartsLife), StringComparison.Ordinal) && !context.Settings.PartyImportLater && form.CurrentPage?.Id == pageBefore && PartyStarts.Count == starts, $"log {Dialogs.Log.Count - logged}, later {context.Settings.PartyImportLater}, page {pageBefore} -> {form.CurrentPage?.Id}, starts {PartyStarts.Count - starts}");
            Check("party import: the notice window asks the owner in an unattended run nothing (the answer is 'not answered')", PartyImportDialog.Show(form, context.Theme, PartyImportHintState.FromPartsLife) == PartyImportAnswer.Dismissed && Dialogs.Log.Count == logged + 2);

            await ShootPartyImport().ConfigureAwait(true);

            // --- nothing was written under the scratch folder ---
            var found = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(root, p)).Order(StringComparer.Ordinal).ToList();
            var expected = new[] { "PartsLife", PartyScratchFiles[0] }.Order(StringComparer.Ordinal).ToList();
            var content = File.Exists(Path.Combine(root, PartyScratchFiles[0])) ? File.ReadAllText(Path.Combine(root, PartyScratchFiles[0])) : "(missing)";
            Check("party import: the run wrote nothing under the scratch roaming folder (the dummy save is as it was, no folder of the new window was made)", found.SequenceEqual(expected) && content == "{}", string.Join(", ", found) + " / " + content);
            Check("party import: the run started no program", PartyStarts.Count == 0, string.Join(" | ", PartyStarts));
        }

        /// <summary>The notice window in every look, shown without taking the focus: the text names the old app, the button that opens Parts Life is the default, nothing is missing or cut off, and it fits the screen.</summary>
        private async Task ShootPartyImport()
        {
            var work = Screen.FromControl(form).WorkingArea;
            await ForEachLook(async look =>
            {
                using var dialog = new PartyImportDialog(context.Theme, PartyImportHintState.FromPartsLife);
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

                Check($"party import: the notice says which old app was found and opens Parts Life by default ({look})", dialog.LeadLine == PartyImportDialog.LeadText(PartyImportHintState.FromPartsLife) && dialog.AcceptButton == dialog.OpenButton && dialog.OpenButton.Text == Strings.Get("gui.party.open") && dialog.LaterButton.Text == Strings.Get("gui.party.import.later") && dialog.Text == Strings.Get("gui.party.import.title"), dialog.LeadLine);
                var missing = SmokeText.Missing(dialog).Distinct().Take(5).ToList();
                Check($"party import: the notice has no missing strings ({look})", missing.Count == 0 && !SmokeText.IsPlaceholder(dialog.LeadLine), string.Join(" | ", missing));
                var clipped = SmokeClip.Clipped(dialog).Distinct().Take(6).ToList();
                Check($"party import: the notice has no cut-off text ({look})", clipped.Count == 0, string.Join(" | ", clipped));
                Check($"party import: the notice fits on the screen and follows the theme ({look})", dialog.Height <= work.Height && dialog.Width <= work.Width && dialog.BackColor == context.Theme.Palette.Window, dialog.Width + "x" + dialog.Height + " in " + work.Width + "x" + work.Height);
                if (shotsDirectory is not null)
                {
                    _shots.Add(await Capture(shotsDirectory, $"{look}-party-import{ScaleSuffix}.png", dialog).ConfigureAwait(true));
                }

                dialog.Close();
            }).ConfigureAwait(true);

            using var former = new PartyImportDialog(context.Theme, PartyImportHintState.FromPartsParty);
            Check("party import: the notice for the former name says so", former.LeadLine == PartyImportDialog.LeadText(PartyImportHintState.FromPartsParty) && former.LeadLine != PartyImportDialog.LeadText(PartyImportHintState.FromPartsLife) && !SmokeText.IsPlaceholder(former.LeadLine), former.LeadLine);
        }
    }
}
