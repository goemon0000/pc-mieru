using PcMieru.Core.Siblings;

namespace PcMieru.Gui;

internal static partial class SmokeRun
{
    /// <summary>The Run value and approval value the setting must use. Written out here (not taken from <c>PartyLogon.ValueName</c>) so the run pins the exact name.</summary>
    private const string LogonName = "PCMieru.Party";

    private sealed partial class Runner
    {
        /// <summary>
        /// The setting "Start Parts Life at sign-in" (DESIGN-C 3 item 5) against the in-memory store the run gave the context: off at first, switching on writes exactly the expected name and
        /// quoted path plus <c>--logon</c>, off removes both values, a Windows-side disable, another folder's entry and an unreadable key each show their line (in every look, with a picture), and the
        /// real registry store was never built (it is built by Program.cs of a real start only, and this run is not one).
        /// </summary>
        private async Task PartyLogonRun()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await PartyLogonWalk().ConfigureAwait(true);
            }
            finally
            {
                Timing["partyLogonMs"] = Math.Round(watch.Elapsed.TotalMilliseconds);
            }
        }

        private async Task PartyLogonWalk()
        {
            if (context.LogonStore is not MemoryRunStore store)
            {
                Check("logon: the run holds an in-memory store", false, context.LogonStore?.GetType().Name ?? "none");
                return;
            }

            if (await Open("settings").ConfigureAwait(true) is not SettingsPage settings)
            {
                Check("logon: the settings page opens", false, LastOpen);
                return;
            }

            var expected = "\"" + context.PartyExePath + "\" --logon";
            var box = settings.PartyLogonBox;
            await settings.RefreshPartyLogon().ConfigureAwait(true);
            Check("logon: off by default, the box can be used, no line, nothing written", !box.Checked && box.Enabled && settings.PartyLogonShown == PartyLogonState.Off && settings.PartyLogonLineText.Length == 0 && store.RunNames.Count == 0 && store.ApprovalNames.Count == 0 && store.Calls.All(c => c.Call is RunStoreCall.ReadRun or RunStoreCall.ReadApproval), box.Checked + " / " + box.Enabled + " / " + settings.PartyLogonShown + " / " + string.Join(",", store.Calls));
            await ShootLogon(settings, "off", null).ConfigureAwait(true);

            // --- Switch on: exactly one Run value of the expected name, holding the quoted path of the Party exe next to this exe and the logon argument. ---
            box.Checked = true;
            await settings.PartyLogonPending.ConfigureAwait(true);
            var written = store.ReadRun(LogonName);
            Check("logon: switching on writes exactly the expected value (a string with the quoted path and --logon)", store.RunNames.Count == 1 && store.RunNames[0] == LogonName && written is { Kind: Microsoft.Win32.RegistryValueKind.String } && written.Text == expected && store.ApprovalNames.Count == 0, string.Join(",", store.RunNames) + " / " + written?.Text + " / " + expected);
            Check("logon: after switching on the box is checked and the page read it back as On", box.Checked && box.Enabled && settings.PartyLogonShown == PartyLogonState.On && settings.PartyLogonLineText.Length == 0, box.Checked + " / " + settings.PartyLogonShown + " / " + settings.PartyLogonLineText);
            await ShootLogon(settings, "on", null).ConfigureAwait(true);

            // --- Windows turned it off (the approval stamp says disabled): the box is off and says why; ticking it brings it back. ---
            store.SeedApproval(LogonName, [3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            await settings.RefreshPartyLogon().ConfigureAwait(true);
            Check("logon: a stamp that says disabled shows DisabledInWindows, the box unchecked and its line", !box.Checked && settings.PartyLogonShown == PartyLogonState.DisabledInWindows && settings.PartyLogonLineText == Strings.Get("gui.settings.partylogon.disabled") && !SmokeText.IsPlaceholder(settings.PartyLogonLineText), settings.PartyLogonShown + " / " + settings.PartyLogonLineText);
            await ShootLogon(settings, "disabled", "gui.settings.partylogon.disabled").ConfigureAwait(true);
            box.Checked = true;
            await settings.PartyLogonPending.ConfigureAwait(true);
            Check("logon: ticking the box after a Windows-side disable makes the entry run again", box.Checked && settings.PartyLogonShown == PartyLogonState.On && store.ReadRun(LogonName)?.Text == expected, settings.PartyLogonShown + " / " + store.ReadRun(LogonName)?.Text);

            // --- Switch off: both values are gone and the line is empty. ---
            box.Checked = false;
            await settings.PartyLogonPending.ConfigureAwait(true);
            Check("logon: switching off removes the Run value and its stamp", !box.Checked && settings.PartyLogonShown == PartyLogonState.Off && store.RunNames.Count == 0 && store.ApprovalNames.Count == 0 && settings.PartyLogonLineText.Length == 0, settings.PartyLogonShown + " / " + string.Join(",", store.RunNames) + " / " + string.Join(",", store.ApprovalNames));

            // --- The old Parts Life start is on: ticking the box switches it (never two starts), and the line says so. ---
            var oldCommand = "\"" + Path.Combine(Path.GetPathRoot(context.PartyExePath) ?? "C:\\", "OldPartsLife", "PartsLife.exe") + "\"";
            store.SeedRun(RunSwitch.OldName, oldCommand);
            await settings.RefreshPartyLogon().ConfigureAwait(true);
            box.Checked = true;
            await settings.PartyLogonPending.ConfigureAwait(true);
            Check("logon: ticking the box while the old start is on switches it (the old value is gone, the new one is the expected one) and the line says so, in the good tone", box.Checked && settings.PartyLogonShown == PartyLogonState.On && store.ReadRun(RunSwitch.OldName) is null && store.ReadRun(LogonName)?.Text == expected && settings.PartyLogonLineText == Strings.Get("gui.settings.partylogon.switched") && settings.PartyLogonLineTone == CellTone.Good, box.Checked + " / " + settings.PartyLogonShown + " / " + string.Join(",", store.RunNames) + " / " + settings.PartyLogonLineText);
            await ShootLogon(settings, "switched", "gui.settings.partylogon.switched").ConfigureAwait(true);
            box.Checked = false;
            await settings.PartyLogonPending.ConfigureAwait(true);
            Check("logon: after the switch, switching off leaves no sign-in value of ours and no line", !box.Checked && store.RunNames.Count == 0 && store.ApprovalNames.Count == 0 && settings.PartyLogonLineText.Length == 0, string.Join(",", store.RunNames) + " / " + settings.PartyLogonLineText);

            // --- The switch cannot be finished (the approval cannot be written): it is rolled back, the old start is as it was, and the line says it could not switch. ---
            store.SeedRun(RunSwitch.OldName, oldCommand);
            store.FailOn(RunStoreCall.WriteApproval, new IOException("smoke: the approval cannot be written"));
            await settings.RefreshPartyLogon().ConfigureAwait(true);
            box.Checked = true;
            await settings.PartyLogonPending.ConfigureAwait(true);
            Check("logon: a switch that cannot be finished is rolled back (old value untouched, no new one) and the line says it could not switch", !box.Checked && store.ReadRun(RunSwitch.OldName)?.Text == oldCommand && store.ReadRun(LogonName) is null && settings.PartyLogonLineText == Strings.Get("gui.settings.partylogon.notswitched") && settings.PartyLogonLineTone == CellTone.Warning, box.Checked + " / " + string.Join(",", store.RunNames) + " / " + settings.PartyLogonLineText);
            await ShootLogon(settings, "notswitched", "gui.settings.partylogon.notswitched").ConfigureAwait(true);
            store.Heal();
            store.DeleteRun(RunSwitch.OldName);
            await settings.RefreshPartyLogon().ConfigureAwait(true);
            Check("logon: after the rolled-back switch is cleared the box is off and has no line", !box.Checked && settings.PartyLogonLineText.Length == 0 && store.RunNames.Count == 0, string.Join(",", store.RunNames) + " / " + settings.PartyLogonLineText);

            // --- Another folder's Parts Life is registered: its line, and the entry is not touched by looking. ---
            var elsewhere = "\"" + Path.Combine(Path.GetPathRoot(context.PartyExePath) ?? "C:\\", "Elsewhere", "PCMieru.Party.exe") + "\" --logon";
            store.SeedRun(LogonName, elsewhere);
            await settings.RefreshPartyLogon().ConfigureAwait(true);
            Check("logon: an entry of another folder shows OtherPlacement and its line, the entry stays as it was", !box.Checked && settings.PartyLogonShown == PartyLogonState.OtherPlacement && settings.PartyLogonLineText == Strings.Get("gui.settings.partylogon.other") && store.ReadRun(LogonName)?.Text == elsewhere, settings.PartyLogonShown + " / " + settings.PartyLogonLineText);
            await ShootLogon(settings, "other", "gui.settings.partylogon.other").ConfigureAwait(true);
            store.DeleteRun(LogonName);

            // --- The key cannot be read: its line, the box is not usable, and nothing is written. ---
            store.DenyOn(RunStoreCall.ReadRun);
            var callsBefore = store.Calls.Count(c => c.Call is RunStoreCall.WriteRun or RunStoreCall.DeleteRun or RunStoreCall.WriteApproval or RunStoreCall.DeleteApproval);
            await settings.RefreshPartyLogon().ConfigureAwait(true);
            var writesAfter = store.Calls.Count(c => c.Call is RunStoreCall.WriteRun or RunStoreCall.DeleteRun or RunStoreCall.WriteApproval or RunStoreCall.DeleteApproval);
            Check("logon: an unreadable key shows Unknown and its line, the box is off and cannot be used, nothing was written", !box.Checked && !box.Enabled && settings.PartyLogonShown == PartyLogonState.Unknown && settings.PartyLogonLineText == Strings.Get("gui.settings.partylogon.unknown") && writesAfter == callsBefore, settings.PartyLogonShown + " / " + settings.PartyLogonLineText);
            await ShootLogon(settings, "unknown", "gui.settings.partylogon.unknown").ConfigureAwait(true);
            store.Heal();
            await settings.RefreshPartyLogon().ConfigureAwait(true);
            Check("logon: when the key can be read again the box is usable and off", !box.Checked && box.Enabled && settings.PartyLogonShown == PartyLogonState.Off && settings.PartyLogonLineText.Length == 0, settings.PartyLogonShown + " / " + settings.PartyLogonLineText);

            Check("logon: the real registry store was never built in this run", RegistryRunStore.Constructed == 0, RegistryRunStore.Constructed.ToString());
        }

        /// <summary>The sign-in row of the settings page in one state, in every look: its words, no cut-off text, the theme, and (with --shots) its picture.</summary>
        private async Task ShootLogon(SettingsPage page, string state, string? lineKey)
        {
            await ForEachLook(async look =>
            {
                page.ScrollToPartyLogon();
                await Task.Delay(200).ConfigureAwait(true);
                // The box always has its words; the status line is checked only in a state that expects one (Off and On have none: an empty line there is the right answer, and a line there is a fault).
                var line = page.PartyLogonLineText;
                var lineOk = lineKey is null ? line.Length == 0 : line.Length > 0 && !SmokeText.IsPlaceholder(line);
                var missing = SmokeText.Missing(page).Concat(SmokeText.Missing(form)).Distinct().Take(5).ToList();
                Check($"logon: the sign-in row has its texts, {state} ({look})", missing.Count == 0 && lineOk && !SmokeText.IsPlaceholder(page.PartyLogonBox.Text) && page.PartyLogonBox.Text == Strings.Get("gui.settings.partylogon"), string.Join(" | ", missing) + (lineOk ? string.Empty : " / line: '" + line + "'"));
                if (lineKey is not null)
                {
                    Check($"logon: the line is exactly the words of {state} in this language ({look})", page.PartyLogonLineText == Strings.Get(lineKey), page.PartyLogonLineText + " / " + Strings.Get(lineKey));
                }

                var clipped = SmokeClip.Clipped(form).Distinct().Take(6).ToList();
                Check($"logon: the sign-in row has no cut-off text, {state} ({look})", clipped.Count == 0, string.Join(" | ", clipped));
                var off = SmokeTheme.Mismatches(form, context.Theme.Palette).Take(5).ToList();
                Check($"logon: the sign-in row follows the theme, {state} ({look})", off.Count == 0, string.Join(" | ", off));
                if (shotsDirectory is not null)
                {
                    _shots.Add(await Capture(shotsDirectory, $"{look}-logon-settings-{state}{ScaleSuffix}.png").ConfigureAwait(true));
                }
            }).ConfigureAwait(true);
        }
    }
}
