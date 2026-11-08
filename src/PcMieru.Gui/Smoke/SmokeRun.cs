using System.Text.Json.Nodes;

namespace PcMieru.Gui;

internal delegate void CheckFn(string name, bool ok, string detail = "");

/// <summary>
/// `PCMieru.exe --smoke out.json [--shots dir]`: opens the real window off screen, lets the sampler run, walks every
/// page in both languages and both themes, checks that no text is a missing-key placeholder, optionally saves a PNG
/// per page, writes a JSON verdict and exits (0 = all checks passed). It exists because a window cannot be unit
/// tested for "it opens and shows things", and a published folder must prove that once before release.
/// </summary>
internal static class SmokeRun
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    /// <summary>Exceptions that reached Application.ThreadException during the run. Any entry fails the smoke run.</summary>
    public static System.Collections.Concurrent.ConcurrentQueue<string> UnhandledErrors { get; } = new();

    public static int Run(MainForm form, GuiContext context, string outputPath, string? shotsDirectory)
    {
        var checks = new JsonArray();
        var shots = new JsonArray();
        var exitCode = 1;

        void Check(string name, bool ok, string detail = "") =>
            checks.Add(new JsonObject { ["name"] = name, ["ok"] = ok, ["detail"] = detail });

        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-24000, -24000);

        using var watchdog = new System.Windows.Forms.Timer { Interval = (int)Budget.TotalMilliseconds };
        watchdog.Tick += (_, _) =>
        {
            Check("finished in time", false, "the smoke run did not finish within " + Budget.TotalSeconds + " s");
            exitCode = 2;
            form.Close();
        };
        watchdog.Start();

        form.Shown += async (_, _) =>
        {
            try
            {
                await Script(form, context, Check, shots, shotsDirectory).ConfigureAwait(true);
                exitCode = checks.All(c => (bool)c!["ok"]!) ? 0 : 1;
            }
            catch (Exception ex)
            {
                Check("no exception", false, ex.GetType().Name + ": " + ex.Message);
                exitCode = 1;
            }
            finally
            {
                form.Close();
            }
        };

        Application.Run(form);
        Check("no unhandled exception on the UI thread", UnhandledErrors.IsEmpty, string.Join(" || ", UnhandledErrors.Take(3)));
        exitCode = checks.All(c => (bool)c!["ok"]!) ? exitCode : Math.Max(exitCode, 1);
        Write(outputPath, form, context, checks, shots, exitCode);
        return exitCode;
    }

    private static async Task Script(MainForm form, GuiContext context, CheckFn check, JsonArray shots, string? shotsDirectory)
    {
        await Task.Delay(TimeSpan.FromSeconds(2.5)).ConfigureAwait(true);
        check("sampler produced snapshots", context.Pump.Latest is not null && context.Pump.History.Count >= 2, context.Pump.History.Count + " samples");

        var processes = form.PageAt(0) as ProcessesPage;
        check("process list is filled", processes is { VisibleRowCount: > 0 }, (processes?.VisibleRowCount ?? 0) + " rows");

        foreach (var language in new[] { Language.Ja, Language.En })
        {
            context.SetLanguage(language);
            context.SetTheme(language == Language.Ja ? ThemeMode.Light : ThemeMode.Dark);
            await Task.Delay(200).ConfigureAwait(true);
            check($"theme applies ({language})", form.BackColor == context.Theme.Palette.Window && context.Theme.IsDark == (language == Language.En));

            for (var i = 0; i < form.PageCount; i++)
            {
                form.ShowPage(i);
                await Task.Delay(500).ConfigureAwait(true);
                var page = form.CurrentPage;
                var label = $"{language}/{PageCatalog.All[i].Id}";
                check($"page opens ({label})", page is { Visible: true, IsHandleCreated: true });
                if (page is null)
                {
                    continue;
                }

                var missing = SmokeText.Missing(page).Concat(SmokeText.Missing(form)).Distinct().Take(5).ToList();
                check($"no missing strings ({label})", missing.Count == 0, string.Join(" | ", missing));
                check($"title is translated ({label})", page.Title == Strings.Get(page.TitleKey, language) && !page.Title.StartsWith('['));
                check($"navigation follows the page ({label})", form.SelectedNavIndex == i, "selected " + form.SelectedNavIndex);
                var off = SmokeTheme.Mismatches(form, context.Theme.Palette).Take(5).ToList();
                check($"colours follow the theme ({label})", off.Count == 0, string.Join(" | ", off));
                if (shotsDirectory is not null)
                {
                    shots.Add(Save(form, shotsDirectory, $"{language.ToString().ToLowerInvariant()}-{(context.Theme.IsDark ? "dark" : "light")}-{PageCatalog.All[i].Id}.png"));
                }
            }
        }

        check("window title mentions sample data", !context.Services.UsesFakeData || form.Text.Contains(Strings.Get("core.fake.notice"), StringComparison.Ordinal), form.Text);
    }

    private static string Save(Control control, string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        using var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    private static void Write(string path, MainForm form, GuiContext context, JsonArray checks, JsonArray shots, int exitCode)
    {
        var report = new JsonObject
        {
            ["ok"] = exitCode == 0,
            ["version"] = AppInfo.Version,
            ["dpi"] = form.DeviceDpi,
            ["sampleData"] = context.Services.UsesFakeData,
            ["fakeAreas"] = new JsonArray(context.Services.FakeAreas.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["checks"] = checks,
            ["screenshots"] = shots,
        };
        File.WriteAllText(path, report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
    }
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

/// <summary>Collects the visible texts of a control tree and flags the ones that are "[key]" placeholders.</summary>
internal static class SmokeText
{
    public static IEnumerable<string> Missing(Control root) => Texts(root).Where(IsPlaceholder);

    public static bool IsPlaceholder(string text) =>
        text.Contains("[gui.", StringComparison.Ordinal) || text.Contains("[core.", StringComparison.Ordinal) || text.Contains("[cli.", StringComparison.Ordinal);

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
