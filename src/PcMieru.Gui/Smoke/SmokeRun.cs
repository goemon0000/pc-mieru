using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace PcMieru.Gui;

internal delegate void CheckFn(string name, bool ok, string detail = "");

/// <summary>
/// `PCMieru.exe --smoke out.json [--shots dir] [--onscreen] [--dpi 150]`: opens the real window (off screen unless
/// <c>--onscreen</c>), lets the sampler run, walks every page in both languages and both themes, checks that no text is a
/// missing-key placeholder or is cut off, exercises the main behaviour of each page with whatever data the services give
/// (sample data with <c>--fakes</c>), optionally saves a PNG of the real window per page, writes a JSON verdict and exits
/// (0 = all checks passed). It exists because a window cannot be unit tested for "it opens and shows things", and a
/// published folder must prove that once before release.
/// </summary>
internal static class SmokeRun
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(90);

    /// <summary>Exceptions that reached Application.ThreadException during the run. Any entry fails the smoke run.</summary>
    public static System.Collections.Concurrent.ConcurrentQueue<string> UnhandledErrors { get; } = new();

    public static int Run(MainForm form, GuiContext context, string outputPath, string? shotsDirectory, bool onScreen, int dpiPercent, DateTime startedAt)
    {
        var checks = new JsonArray();
        var runner = new Runner(form, context, checks, shotsDirectory, onScreen, dpiPercent, startedAt);
        var exitCode = 1;

        // A dialog must never wait for a click in an unattended run; "no" is the safe answer.
        Dialogs.AutoAnswer = false;
        form.ShowInTaskbar = false;
        form.PreventActivation();
        if (onScreen)
        {
            form.TopMost = true; // not activated, so it would otherwise sit behind whatever is open
        }
        else
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-24000, -24000);
        }

        using var watchdog = new System.Windows.Forms.Timer { Interval = (int)Budget.TotalMilliseconds };
        watchdog.Tick += (_, _) =>
        {
            runner.Check("finished in time", false, "the smoke run did not finish within " + Budget.TotalSeconds + " s");
            exitCode = 2;
            form.Close();
        };
        watchdog.Start();

        form.Shown += async (_, _) =>
        {
            runner.Timing["shownMs"] = Ms(startedAt);
            try
            {
                await runner.Script().ConfigureAwait(true);
                exitCode = checks.All(c => (bool)c!["ok"]!) ? 0 : 1;
            }
            catch (Exception ex)
            {
                runner.Check("no exception", false, ex.GetType().Name + ": " + ex.Message);
                exitCode = 1;
            }
            finally
            {
                form.Close();
            }
        };

        Application.Run(form);
        runner.Check("no unhandled exception on the UI thread", UnhandledErrors.IsEmpty, string.Join(" || ", UnhandledErrors.Take(3)));
        exitCode = checks.All(c => (bool)c!["ok"]!) ? exitCode : Math.Max(exitCode, 1);
        runner.Write(outputPath, exitCode);
        return exitCode;
    }

    private static double Ms(DateTime since) => Math.Round((DateTime.Now - since).TotalMilliseconds);

    private sealed class Runner(MainForm form, GuiContext context, JsonArray checks, string? shotsDirectory, bool onScreen, int dpiPercent, DateTime startedAt)
    {
        private readonly JsonArray _shots = [];

        public JsonObject Timing { get; } = [];

        private string LastOpen { get; set; } = string.Empty;

        public void Check(string name, bool ok, string detail = "") =>
            checks.Add(new JsonObject { ["name"] = name, ["ok"] = ok, ["detail"] = detail });

        public async Task Script()
        {
            if (dpiPercent > 0)
            {
                await EmulateDpi().ConfigureAwait(true);
            }

            // First data on the page the app opens on.
            var overview = form.CurrentPage as OverviewPage;
            await Until(() => overview is { Apps.Count: > 0 }, 6000).ConfigureAwait(true);
            Timing["firstRowsMs"] = Ms(startedAt);
            Timing["sinceProcessStartMs"] = Math.Round((DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds);
            await Until(() => context.Pump.History.Count >= 2, 5000).ConfigureAwait(true);
            Check("sampler produced snapshots", context.Pump.Latest is not null && context.Pump.History.Count >= 2, context.Pump.History.Count + " samples");
            Check("first page shows rows", overview is { Apps.Count: > 0 }, (overview?.Apps.Count ?? 0) + " apps");

            var processes = await Open("processes").ConfigureAwait(true) as ProcessesPage;
            await Until(() => processes is { VisibleRowCount: > 0 }, 5000).ConfigureAwait(true);
            Timing["processRowsMs"] = Ms(startedAt);
            Check("process list is filled", processes is { VisibleRowCount: > 0 }, (processes?.VisibleRowCount ?? 0) + " rows, selected " + (processes?.List.SelectedIndices.Count ?? -1) + ", row " + (processes?.List.SelectedRow ?? -2));

            foreach (var language in new[] { Language.Ja, Language.En })
            {
                foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
                {
                    context.SetLanguage(language);
                    context.SetTheme(theme);
                    await Task.Delay(150).ConfigureAwait(true);
                    var pass = $"{language}/{theme}";
                    Check($"theme applies ({pass})", form.BackColor == context.Theme.Palette.Window && context.Theme.IsDark == (theme == ThemeMode.Dark));
                    await WalkPages(language, theme).ConfigureAwait(true);
                }
            }

            await Behaviour().ConfigureAwait(true);
            Check("window title mentions sample data", !context.Services.UsesFakeData || form.Text.Contains(Strings.Get("core.fake.notice"), StringComparison.Ordinal), form.Text);
        }

        private async Task WalkPages(Language language, ThemeMode theme)
        {
            for (var i = 0; i < form.PageCount; i++)
            {
                var id = PageCatalog.All[i].Id;
                var label = $"{language}/{theme}/{id}";
                var page = await Open(id).ConfigureAwait(true);
                Check($"page opens ({label})", page is { Visible: true, IsHandleCreated: true }, LastOpen);
                if (page is null)
                {
                    continue;
                }

                Check($"page has its data ({label})", page.IsReady);
                var missing = SmokeText.Missing(page).Concat(SmokeText.Missing(form)).Distinct().Take(5).ToList();
                Check($"no missing strings ({label})", missing.Count == 0, string.Join(" | ", missing));
                Check($"title is translated ({label})", page.Title == Strings.Get(page.TitleKey, language) && !page.Title.StartsWith('['));
                Check($"navigation follows the page ({label})", form.SelectedNavIndex == i, "selected " + form.SelectedNavIndex);
                var off = SmokeTheme.Mismatches(form, context.Theme.Palette).Take(5).ToList();
                Check($"colours follow the theme ({label})", off.Count == 0, string.Join(" | ", off));
                var clipped = SmokeClip.Clipped(form).Distinct().Take(6).ToList();
                Check($"no cut-off text ({label})", clipped.Count == 0, string.Join(" | ", clipped));
                if (shotsDirectory is not null)
                {
                    var scale = dpiPercent > 0 ? "@" + dpiPercent : string.Empty;
                    _shots.Add(Capture(shotsDirectory, $"{language.ToString().ToLowerInvariant()}-{theme.ToString().ToLowerInvariant()}-{id}{scale}.png"));
                }
            }
        }

        /// <summary>Opens a page, waits for its first data and lets it paint once.</summary>
        private async Task<PageBase?> Open(string id)
        {
            form.ShowPage(id);
            var page = form.CurrentPage;
            var trace = $"asked {id}, got {page?.Id}, nav {form.SelectedNavIndex}";
            if (page is not null)
            {
                await Until(() => page.IsReady, 4000).ConfigureAwait(true);
            }

            await Task.Delay(250).ConfigureAwait(true);
            LastOpen = trace + $"; later current {form.CurrentPage?.Id}, nav {form.SelectedNavIndex}";
            return page;
        }

        private static async Task Until(Func<bool> condition, int timeoutMs)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.ElapsedMilliseconds < timeoutMs)
            {
                await Task.Delay(20).ConfigureAwait(true);
            }
        }

        /// <summary>What each page does with data, checked in whichever language and theme the walk ended on.</summary>
        private async Task Behaviour()
        {
            if (await Open("overview").ConfigureAwait(true) is OverviewPage overview)
            {
                Check("overview: cards carry values", overview.CpuCard.ValueText != Fmt.Dash && overview.MemoryCard.ValueText != Fmt.Dash, overview.CpuCard.ValueText + " / " + overview.MemoryCard.ValueText);
                Check("overview: heavy apps are listed", overview.Apps.Count is > 0 and <= 5, overview.Apps.Count + " apps");
            }

            if (await Open("performance").ConfigureAwait(true) is PerformancePage performance)
            {
                Check("performance: one entry per device", performance.List.Items.Count >= 3, performance.List.Items.Count + " entries");
                Check("performance: a panel is shown", performance.Current is not null);
                Check("performance: graphs exist", performance.Graphs.Length > 0);
            }

            if (await Open("startup").ConfigureAwait(true) is StartupPage startup)
            {
                Check("startup: items are listed", startup.Grid.VirtualListSize > 0, startup.Grid.VirtualListSize + " rows");
            }

            if (await Open("services").ConfigureAwait(true) is ServicesPage services)
            {
                var all = services.Grid.VirtualListSize;
                Check("services: services are listed", all > 0, all + " rows");
                services.FilterBox.Text = "zzzz-no-such-service";
                await Until(() => services.Grid.VirtualListSize == 0, 2000).ConfigureAwait(true);
                Check("services: the filter narrows the list", services.Grid.VirtualListSize == 0, services.Grid.VirtualListSize + " rows left");
                services.FilterBox.Text = string.Empty;
                await Until(() => services.Grid.VirtualListSize == all, 2000).ConfigureAwait(true);
                Check("services: clearing the filter restores the list", services.Grid.VirtualListSize == all, services.Grid.VirtualListSize + " of " + all);
            }

            if (await Open("users").ConfigureAwait(true) is UsersPage users)
            {
                Check("users: sessions are listed", users.Rows.Count > 0, users.Rows.Count + " rows");
            }

            if (await Open("hardware").ConfigureAwait(true) is HardwarePage hardware)
            {
                Check("hardware: all categories are in the tree", hardware.Nodes.Count == 11, hardware.Nodes.Count + " categories");
                Check("hardware: properties are listed", hardware.LineCount > 0, hardware.LineCount + " lines");
            }

            if (await Open("settings").ConfigureAwait(true) is SettingsPage settings)
            {
                var before = context.Pump.IntervalMs;
                settings.IntervalBox.SelectedIndex = Array.IndexOf(GuiSettings.IntervalChoices, 5000);
                Check("settings: the interval reaches the sampler", context.Pump.IntervalMs == 5000, context.Pump.IntervalMs + " ms");
                settings.IntervalBox.SelectedIndex = Array.IndexOf(GuiSettings.IntervalChoices, before);
                settings.PauseBox.Checked = true;
                Check("settings: pause reaches the sampler", context.Pump.Paused);
                settings.PauseBox.Checked = false;
                Check("settings: resume reaches the sampler", !context.Pump.Paused);
            }

            await ReportRun().ConfigureAwait(true);
        }

        /// <summary>Generates a report for the shortest duration, then checks the text, the redaction default and both formats.</summary>
        private async Task ReportRun()
        {
            if (await Open("diagnose").ConfigureAwait(true) is not DiagnosePage diagnose)
            {
                return;
            }

            Check("report: redaction is on by default", diagnose.RedactBox.Checked);
            diagnose.Duration.SelectedIndex = 0;
            diagnose.StartButton.PerformClick();
            await Until(() => diagnose.Document is not null, 20000).ConfigureAwait(true);
            Check("report: a report was generated", diagnose.Document is not null && diagnose.TextView.Text.Length > 100, diagnose.TextView.Text.Length + " characters");
            Check("report: it is not running any more", !diagnose.IsRunning);
            Check("report: no missing strings in the text", !SmokeText.IsPlaceholder(diagnose.TextView.Text), string.Empty);
            if (shotsDirectory is not null)
            {
                var scale = dpiPercent > 0 ? "@" + dpiPercent : string.Empty;
                _shots.Add(Capture(shotsDirectory, $"{Strings.Current.ToString().ToLowerInvariant()}-{(context.Theme.IsDark ? "dark" : "light")}-diagnose-report{scale}.png"));
            }
        }

        /// <summary>
        /// The system DPI of the test PC is 96, so a "150 %" window is made by sending the message Windows sends when a
        /// window moves to a denser monitor. WinForms then rescales fonts and layout exactly as it does there.
        /// </summary>
        private async Task EmulateDpi()
        {
            var target = 96 * dpiPercent / 100;
            var scale = (double)target / form.DeviceDpi;
            var rect = new NativeRect
            {
                Left = form.Left,
                Top = form.Top,
                Right = form.Left + (int)(form.Width * scale),
                Bottom = form.Top + (int)(form.Height * scale),
            };
            var memory = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRect>());
            try
            {
                Marshal.StructureToPtr(rect, memory, false);
                SendMessage(form.Handle, 0x02E0, (IntPtr)((target << 16) | target), memory);
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }

            await Task.Delay(600).ConfigureAwait(true);
            Check($"dpi emulation reached {target}", form.DeviceDpi == target, "device dpi " + form.DeviceDpi);
        }

        /// <summary>
        /// The window as the user sees it, title bar and frame included: PrintWindow asks the compositor for the real
        /// content (so it also works off screen), CopyFromScreen reads the pixels when the window is on screen. This is
        /// how the dark native headers and the dark title bar are verified; DrawToBitmap paints through a different path.
        /// </summary>
        private string Capture(string directory, string fileName)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            GetWindowRect(form.Handle, out var window);
            var width = window.Right - window.Left;
            var height = window.Bottom - window.Top;
            using var full = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(full))
            {
                if (onScreen)
                {
                    graphics.CopyFromScreen(window.Left, window.Top, 0, 0, new Size(width, height));
                }
                else
                {
                    var hdc = graphics.GetHdc();
                    try
                    {
                        if (!PrintWindow(form.Handle, hdc, 2))
                        {
                            Check("window capture: " + fileName, false, "PrintWindow failed");
                        }
                    }
                    finally
                    {
                        graphics.ReleaseHdc(hdc);
                    }
                }
            }

            // Windows 10/11 frames carry an invisible resize border; crop to the visible frame.
            var frame = new NativeRect();
            var crop = new Rectangle(0, 0, width, height);
            if (DwmGetWindowAttribute(form.Handle, 9, out frame, Marshal.SizeOf<NativeRect>()) == 0 && frame.Right > frame.Left)
            {
                crop = Rectangle.Intersect(crop, new Rectangle(frame.Left - window.Left, frame.Top - window.Top, frame.Right - frame.Left, frame.Bottom - frame.Top));
            }

            using var cropped = full.Clone(crop, PixelFormat.Format32bppArgb);
            cropped.Save(path, ImageFormat.Png);
            return path;
        }

        public void Write(string path, int exitCode)
        {
            var report = new JsonObject
            {
                ["ok"] = exitCode == 0,
                ["version"] = AppInfo.Version,
                ["dpi"] = form.DeviceDpi,
                ["sampleData"] = context.Services.UsesFakeData,
                ["fakeAreas"] = new JsonArray(context.Services.FakeAreas.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
                ["timing"] = Timing,
                ["checks"] = checks,
                ["screenshots"] = _shots,
            };
            File.WriteAllText(path, report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);
}

/// <summary>Panels and labels take their background from the page, so after any theme change they must all equal the window colour.</summary>
internal static class SmokeTheme
{
    public static IEnumerable<string> Mismatches(Control root, Palette palette)
    {
        foreach (Control child in root.Controls)
        {
            var expectedText = child is ToneLabel tone ? tone.ExpectedColor(palette) : palette.Text;
            if (child is Label or Panel && (child.BackColor != palette.Window || child.ForeColor != expectedText))
            {
                yield return $"{child.GetType().Name}:{child.Name}";
            }

            foreach (var inner in Mismatches(child, palette))
            {
                yield return inner;
            }
        }
    }
}

/// <summary>
/// Finds text that does not fit where it is drawn (Japanese is wider than the English the layout was first tried with):
/// a label, button or check box whose wrapped text is taller than the control, a combo box whose text is wider than its
/// field, a column header wider than its column, a control that sticks out of its parent.
/// </summary>
internal static class SmokeClip
{
    private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

    public static IEnumerable<string> Clipped(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (!child.Visible || child.Width <= 0 || child.Height <= 0)
            {
                continue;
            }

            if (Problem(child) is { } problem)
            {
                yield return problem;
            }

            foreach (var inner in Clipped(child))
            {
                yield return inner;
            }
        }
    }

    private static string? Problem(Control c)
    {
        var name = $"{c.GetType().Name}:{c.Name}";
        switch (c)
        {
            case Label { AutoEllipsis: true }:
                break;
            case Label or ButtonBase when c.Text.Length > 0 && !c.AutoSize:
            {
                var glyph = c is CheckBox or RadioButton ? c.LogicalToDeviceUnits(20) : c is Button ? c.LogicalToDeviceUnits(10) : 0;
                var width = Math.Max(1, c.ClientSize.Width - c.Padding.Horizontal - glyph);
                var wanted = TextRenderer.MeasureText(c.Text, c.Font, new Size(width, 100000), Wrap);
                if (wanted.Height > c.ClientSize.Height - c.Padding.Vertical + 2)
                {
                    return $"{name} \"{Short(c.Text)}\" needs {wanted.Height}px, has {c.ClientSize.Height - c.Padding.Vertical}px";
                }

                break;
            }

            case ComboBox combo:
            {
                var room = combo.Width - combo.LogicalToDeviceUnits(30);
                foreach (var item in combo.Items)
                {
                    var text = item.ToString() ?? string.Empty;
                    if (TextRenderer.MeasureText(text, combo.Font).Width > Math.Max(room, combo.DropDownWidth - combo.LogicalToDeviceUnits(10)))
                    {
                        return $"{name} item \"{Short(text)}\" is wider than the list";
                    }
                }

                if (combo.SelectedItem?.ToString() is { } selected && TextRenderer.MeasureText(selected, combo.Font).Width > room)
                {
                    return $"{name} shows \"{Short(selected)}\" wider than its field";
                }

                break;
            }

            case ListView list:
                foreach (ColumnHeader column in list.Columns)
                {
                    if (column.Width > 0 && column.Text.Length > 0 && TextRenderer.MeasureText(column.Text, list.Font).Width + list.LogicalToDeviceUnits(14) > column.Width)
                    {
                        return $"{name} header \"{Short(column.Text)}\" is wider than its column ({column.Width}px)";
                    }
                }

                break;
        }

        if (c.Parent is { } parent && c.Dock == DockStyle.None && parent is not ScrollableControl { AutoScroll: true } && c.Visible
            && (c.Right > parent.ClientSize.Width + 1 || c.Bottom > parent.ClientSize.Height + 1))
        {
            return $"{name} sticks out of {parent.Name} ({c.Right}x{c.Bottom} in {parent.ClientSize.Width}x{parent.ClientSize.Height})";
        }

        return null;
    }

    private static string Short(string text) => text.Length <= 28 ? text : text[..28] + "...";
}

/// <summary>Collects the visible texts of a control tree and flags the ones that are "[key]" placeholders.</summary>
internal static class SmokeText
{
    public static IEnumerable<string> Missing(Control root) => Texts(root).Where(IsPlaceholder);

    public static bool IsPlaceholder(string text) =>
        text.Contains("[gui.", StringComparison.Ordinal)
        || text.Contains("[core.", StringComparison.Ordinal)
        || text.Contains("[cli.", StringComparison.Ordinal)
        || text.Contains("[hw.", StringComparison.Ordinal)
        || text.Contains("[mgmt.", StringComparison.Ordinal);

    public static IEnumerable<string> Texts(Control control)
    {
        yield return control.Text;
        yield return control.AccessibleName ?? string.Empty;
        switch (control)
        {
            case ListView { VirtualMode: false } list:
                foreach (ColumnHeader column in list.Columns)
                {
                    yield return column.Text;
                }

                foreach (ListViewItem item in list.Items)
                {
                    foreach (ListViewItem.ListViewSubItem sub in item.SubItems)
                    {
                        yield return sub.Text;
                    }
                }

                break;
            case ListView list:
                foreach (ColumnHeader column in list.Columns)
                {
                    yield return column.Text;
                }

                break;
            case ListBox box:
                foreach (var item in box.Items)
                {
                    yield return item.ToString() ?? string.Empty;
                }

                break;
            case ComboBox combo:
                foreach (var item in combo.Items)
                {
                    yield return item.ToString() ?? string.Empty;
                }

                break;
            case TreeView tree:
                foreach (var text in NodeTexts(tree.Nodes))
                {
                    yield return text;
                }

                break;
        }

        foreach (Control child in control.Controls)
        {
            foreach (var text in Texts(child))
            {
                yield return text;
            }
        }
    }

    private static IEnumerable<string> NodeTexts(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node.Text;
            foreach (var text in NodeTexts(node.Nodes))
            {
                yield return text;
            }
        }
    }
}
