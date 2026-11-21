using System.Diagnostics;
using System.Globalization;
using System.Text;
using PcMieru.Checks;
using PcMieru.Checks.Build;

namespace PcMieru.Gui.Tests;

/// <summary>
/// "構成コードをコピー（/my-config 用）" on the hardware page (PCB1): the button beside the share button, the read that runs off the UI thread,
/// the dialog that shows everything before anything leaves the app, Copy and Save, a failed read, and the words. The provider is always the
/// sample or a test's own: <c>BuildCodeProviders.Windows()</c> is never made here, nothing reads this PC, and the dialog never opens in a test
/// (<see cref="Dialogs.AutoAnswer"/>); the ones the tests look at are built directly. Files go to a temporary folder.
/// </summary>
public class BuildCodeUiTests
{
    private sealed class Rig : IDisposable
    {
        private readonly IDisposable _locale;
        private readonly ThemeService _theme = new(ThemeMode.Light);

        public Rig(Language language = Language.En)
        {
            _locale = Harness.Locale(language);
            Services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            Pump = new SnapshotPump(Services);
            Context = new GuiContext(Services, Pump, _theme, new GuiSettings());
            Dialogs.AutoAnswer = false; // dialogs never open and never wait for a click
            Dialogs.Log.Clear();
        }

        public PcMieruServices Services { get; }

        public SnapshotPump Pump { get; }

        public GuiContext Context { get; }

        public ThemeService Theme => _theme;

        public void Dispose()
        {
            Dialogs.AutoAnswer = null;
            Dialogs.Log.Clear();
            Pump.Dispose();
            Services.Dispose();
            _theme.Dispose();
            _locale.Dispose();
        }
    }

    /// <summary>A provider that answers when the test says so, and remembers on which thread it was asked.</summary>
    private sealed class GateProvider(BuildCodeResult result) : IBuildCodeProvider
    {
        private int _calls;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public int ThreadId { get; private set; }

        public bool HadSynchronizationContext { get; private set; }

        public async Task<BuildCodeResult> CreateAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            ThreadId = Environment.CurrentManagedThreadId;
            HadSynchronizationContext = SynchronizationContext.Current is not null;
            Entered.TrySetResult();
            await Gate.Task.ConfigureAwait(false);
            return result;
        }
    }

    private sealed class ThrowingProvider(Exception error, bool synchronous) : IBuildCodeProvider
    {
        public Task<BuildCodeResult> CreateAsync(CancellationToken ct) =>
            synchronous ? throw error : Task.FromException<BuildCodeResult>(error);
    }

    private static readonly Dictionary<string, StringEntry> Registered = GuiStrings.All.ToDictionary(static e => e.Key, StringComparer.Ordinal);

    private static string Word(string key, Language language) => language == Language.Ja ? Registered[key].Ja : Registered[key].En;

    private static async Task<BuildCodeResult> PreviewResult() => await new FakeBuildCode().CreateAsync(CancellationToken.None);

    // The page's continuations come back through the message loop, so the test thread has to run it while it waits.
    private static void PumpUntil(Func<bool> done, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < 10_000)
        {
            Application.DoEvents();
            Thread.Sleep(2);
        }

        Assert.True(done(), what);
    }

    private static void Finish(Task task)
    {
        PumpUntil(() => task.IsCompleted, "the action did not finish");
        task.GetAwaiter().GetResult();
    }

    private static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var inner in AllControls(child))
            {
                yield return inner;
            }
        }
    }

    /// <summary>The sentence a note must be, worked out from the registered words (not through the code under test).</summary>
    private static string ExpectedNote(BuildNote note, Language language)
    {
        var template = Word(BuildCodeStrings.NotePrefix + CheckFormat.Snake(note.Kind.ToString()), language);
        var max = note.Kind == BuildNoteKind.KindsLeftOut ? BuildCode.MaxPerCategory : BuildCode.MaxLength;
        return template
            .Replace("{category}", Word(BuildCodeStrings.CategoryPrefix + note.Category.Replace('-', '_'), language), StringComparison.Ordinal)
            .Replace("{count}", note.Count.ToString("N0", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{max}", max.ToString("N0", CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static string ExpectedUnread(BuildUnread unread, Language language) =>
        Word(BuildCodeStrings.CategoryPrefix + unread.Category.Replace('-', '_'), language) + ": " + Word(BuildCodeStrings.ReasonPrefix + CheckFormat.Snake(unread.Reason.ToString()), language);

    // ---------------------------------------------------------------- the button

    [Theory]
    [InlineData(Language.Ja, "構成コードをコピー（/my-config 用）")]
    [InlineData(Language.En, "Copy configuration code (for /my-config)")]
    public void The_button_sits_beside_the_share_button_in_the_same_bar_at_the_same_size(Language language, string text)
    {
        using var rig = new Rig(language);
        using var page = new HardwarePage(rig.Context);
        using var host = new Panel { Size = new Size(900, 600) };
        page.Dock = DockStyle.Fill;
        host.Controls.Add(page);
        host.PerformLayout();
        page.PerformLayout();

        var share = page.ShareButton;
        var code = page.BuildCodeButton;
        Assert.Equal("partsBuildCode", code.Name);
        Assert.Equal(text, code.Text);
        Assert.Contains(code, AllControls(page));
        Assert.Same(share.Parent, code.Parent);
        Assert.Equal("partsShareBar", code.Parent!.Name);
        Assert.Equal(share.MinimumSize, code.MinimumSize);
        Assert.Equal(share.Height, code.Height);
        Assert.Equal(share.Top, code.Top);
        Assert.True(code.Left >= share.Right, $"the code button starts at {code.Left}, the share button ends at {share.Right}");
        Assert.True(code.Enabled);
    }

    [Fact]
    public void The_button_text_follows_a_language_change()
    {
        using var rig = new Rig(Language.En);
        using var page = new HardwarePage(rig.Context);

        Strings.Current = Language.Ja;
        page.ApplyLanguage();

        Assert.Equal("構成コードをコピー（/my-config 用）", page.BuildCodeButton.Text);
    }

    [Theory]
    [InlineData(Language.En)]
    [InlineData(Language.Ja)]
    public void Both_buttons_fit_in_their_bar_at_the_smallest_window(Language language)
    {
        using var rig = new Rig(language);
        using var page = new HardwarePage(rig.Context);
        // The window is at least 760 logical pixels wide and the navigation list takes 188 of them.
        var scale = page.DeviceDpi / 96.0;
        using var host = new Panel { Size = new Size((int)Math.Round((760 - 188) * scale), (int)Math.Round(520 * scale)) };
        page.Dock = DockStyle.Fill;
        host.Controls.Add(page);
        host.PerformLayout();
        page.PerformLayout();

        var bar = page.BuildCodeButton.Parent!;
        bar.PerformLayout();
        var needed = ((TableLayoutPanel)bar).GetPreferredSize(new Size(0, 0)).Width;
        Assert.True(needed <= host.ClientSize.Width, $"the bar needs {needed} px but the page is {host.ClientSize.Width} px wide ({language})");
        Assert.True(page.BuildCodeButton.Right <= bar.ClientSize.Width, $"the code button ends at {page.BuildCodeButton.Right} in a bar {bar.ClientSize.Width} px wide");
        Assert.True(page.BuildCodeButton.Left >= page.ShareButton.Right, "the two buttons do not overlap");
    }

    // ---------------------------------------------------------------- the press

    [Fact]
    public async Task With_fakes_the_window_has_the_sample_provider_and_never_the_windows_one()
    {
        using var rig = new Rig(Language.En);

        Assert.IsType<FakeBuildCode>(rig.Context.BuildCode);
        var result = await rig.Context.BuildCode.CreateAsync(CancellationToken.None);
        Assert.StartsWith("PCB1.", result.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_press_goes_through_the_click_handler_and_the_dialog_gets_the_providers_code()
    {
        using var rig = new Rig(Language.En);
        using var page = new HardwarePage(rig.Context);

        page.BuildCodeButton.PerformClick();
        PumpUntil(() => Dialogs.Log.Count > 0, "the dialog was not asked for");

        var entry = Assert.Single(Dialogs.Log);
        Assert.StartsWith(Strings.Get("gui.hw.pcb1.title") + " | PCB1.", entry, StringComparison.Ordinal);
        PumpUntil(() => page.BuildCodeButton.Enabled, "the button did not come back");
    }

    [Fact]
    public async Task The_provider_runs_on_a_pool_thread_and_the_button_stays_off_until_it_has_answered()
    {
        using var rig = new Rig(Language.En);
        using var page = new HardwarePage(rig.Context);
        var result = await PreviewResult();
        var provider = new GateProvider(result);
        page.BuildCodeProvider = provider;
        var testThread = Environment.CurrentManagedThreadId;
        Assert.True(page.BuildCodeButton.Enabled);

        var first = page.BuildCodeAsync();

        Assert.False(first.IsCompleted);
        Assert.False(page.BuildCodeButton.Enabled, "the button is off while the code is made");
        PumpUntil(() => provider.Entered.Task.IsCompleted, "the provider was not asked");
        Assert.NotEqual(testThread, provider.ThreadId);
        Assert.False(provider.HadSynchronizationContext, "the provider must not run in the UI thread's context");
        Assert.False(first.IsCompleted);
        Assert.False(page.BuildCodeButton.Enabled);
        Assert.Empty(Dialogs.Log);

        // A second press while one is running does nothing.
        var second = page.BuildCodeAsync();
        Assert.True(second.IsCompleted);
        Assert.Equal(1, provider.Calls);

        provider.Gate.SetResult();
        Finish(first);

        Assert.True(page.BuildCodeButton.Enabled, "the button comes back when the dialog is closed");
        var entry = Assert.Single(Dialogs.Log);
        Assert.EndsWith(" | " + result.Code, entry, StringComparison.Ordinal);
        Assert.Equal(1, provider.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_provider_that_throws_opens_no_dialog_and_the_status_never_carries_the_exception_text(bool synchronous)
    {
        using var rig = new Rig(Language.En);
        using var page = new HardwarePage(rig.Context);
        var secret = @"C:\Users\someone\SECRET-NAME is not allowed";
        var error = new InvalidOperationException(secret);
        page.BuildCodeProvider = new ThrowingProvider(error, synchronous);
        var statuses = new List<string>();
        rig.Context.StatusMessage += statuses.Add;

        Finish(page.BuildCodeAsync());

        Assert.Empty(Dialogs.Log);
        var status = Assert.Single(statuses);
        Assert.Equal(Strings.Format("gui.hw.pcb1.failed", Fmt.Dict(("detail", "InvalidOperationException 0x80131509"))), status);
        Assert.DoesNotContain("SECRET", status, StringComparison.Ordinal);
        Assert.DoesNotContain("someone", status, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, status, StringComparison.Ordinal);
        Assert.True(page.BuildCodeButton.Enabled, "the button comes back after a failure");

        // And it can be pressed again once the provider works.
        page.BuildCodeProvider = null;
        Finish(page.BuildCodeAsync());
        Assert.Single(Dialogs.Log);
    }

    // ---------------------------------------------------------------- the dialog

    [Theory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    public async Task The_dialog_shows_the_parts_the_code_the_notes_and_the_unread_categories_of_the_result(Language language)
    {
        using var rig = new Rig(language);
        var result = await PreviewResult();
        using var dialog = new BuildCodeDialog(rig.Theme, result, _ => true);

        // The parts: category, name, details and count, in the order the code writes them.
        var expected = BuildConfig.Categories.SelectMany(c => result.Config[c].Select(p => (Category: c, Part: p))).ToList();
        Assert.True(expected.Count >= 5, "the sample has parts to show");
        Assert.Equal(expected.Count, dialog.PartLines.Count);
        Assert.Equal(expected.Count, dialog.PartsList.VirtualListSize);
        for (var i = 0; i < expected.Count; i++)
        {
            var (category, part) = expected[i];
            var line = dialog.PartLines[i];
            Assert.Equal(Word(BuildCodeStrings.CategoryPrefix + category.Replace('-', '_'), language), line.Category);
            Assert.Equal(part.Name, line.Name);
            Assert.Equal(part.Q.ToString(CultureInfo.InvariantCulture), line.Count);
        }

        // The code: exactly the provider's, in a box that cannot be edited.
        Assert.StartsWith("PCB1.", result.Code, StringComparison.Ordinal);
        Assert.Equal(result.Code, dialog.CodeText);
        Assert.True(AllControls(dialog).OfType<TextBox>().Single(static t => t.Name == "pcb1Code").ReadOnly);

        // The notes: one sentence per note, in the language, every kind of the preview.
        Assert.Equal(Enum.GetValues<BuildNoteKind>().Length, result.Notes.Select(static n => n.Kind).Distinct().Count());
        var noteLines = dialog.NotesText.Split('\n');
        Assert.Equal(result.Notes.Count, noteLines.Length);
        for (var i = 0; i < result.Notes.Count; i++)
        {
            Assert.Equal("・" + ExpectedNote(result.Notes[i], language), noteLines[i]);
            Assert.DoesNotContain("{", noteLines[i], StringComparison.Ordinal);
        }

        // The categories that could not be read, each with its reason.
        Assert.True(result.Unread.Count >= 3);
        var unreadLines = dialog.UnreadText.Split('\n');
        Assert.Equal(result.Unread.Count, unreadLines.Length);
        for (var i = 0; i < result.Unread.Count; i++)
        {
            Assert.Equal("・" + ExpectedUnread(result.Unread[i], language), unreadLines[i]);
        }

        Assert.Equal(string.Empty, dialog.Result);
    }

    [Fact]
    public async Task A_result_with_nothing_left_out_and_nothing_unread_says_so()
    {
        using var rig = new Rig(Language.En);
        var result = await PreviewResult();
        using var plain = new BuildCodeDialog(rig.Theme, result with { Notes = [], Unread = [] }, _ => true);
        using var empty = new BuildCodeDialog(rig.Theme, result with { Config = new BuildConfig(), Notes = [], Unread = [] }, _ => true);

        Assert.Equal(string.Empty, plain.NotesText);
        Assert.Equal(Strings.Get("gui.hw.pcb1.unread.none"), plain.UnreadText);
        Assert.DoesNotContain(AllControls(plain), static c => c.Name == "pcb1NotesHead");
        Assert.Empty(empty.PartLines);
        Assert.Contains(AllControls(empty), static c => c.Name == "pcb1PartsNone");
    }

    [Fact]
    public async Task The_dialog_has_copy_save_and_close_and_nothing_that_opens_a_site()
    {
        using var rig = new Rig(Language.En);
        var result = await PreviewResult();
        using var dialog = new BuildCodeDialog(rig.Theme, result, _ => true);

        var buttons = AllControls(dialog).OfType<Button>().Select(static b => b.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["pcb1Close", "pcb1Copy", "pcb1Save"], buttons);
        Assert.Empty(AllControls(dialog).OfType<LinkLabel>());
        Assert.Empty(AllControls(dialog).OfType<WebBrowser>());
        Assert.Equal("pc-config.json", BuildCodeDialog.DefaultFileName);
    }

    [Fact]
    public async Task Copy_puts_exactly_the_code_string_on_the_clipboard()
    {
        using var rig = new Rig(Language.Ja);
        var result = await PreviewResult();
        Assert.NotEqual(result.Code, result.Json);
        var copied = new List<string>();
        using var dialog = new BuildCodeDialog(rig.Theme, result, text =>
        {
            copied.Add(text);
            return true;
        });

        Assert.Empty(copied);
        dialog.CopyNow();

        Assert.Equal([result.Code], copied);
        Assert.Equal(Strings.Get("gui.common.copied"), dialog.Result);
    }

    [Fact]
    public async Task A_busy_clipboard_is_said_so_and_is_not_a_crash()
    {
        using var rig = new Rig(Language.Ja);
        var result = await PreviewResult();
        using var dialog = new BuildCodeDialog(rig.Theme, result, _ => false);

        dialog.CopyNow();

        Assert.Equal(Strings.Get("gui.hw.pcb1.copy.failed"), dialog.Result);
        Assert.Contains("クリップボード", dialog.Result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_writes_exactly_the_json_of_the_result_as_utf8_without_a_byte_order_mark()
    {
        using var rig = new Rig(Language.En);
        var result = await PreviewResult();
        var folder = Path.Combine(Path.GetTempPath(), "pcmieru-pcb1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "pc-config.json");
            using var dialog = new BuildCodeDialog(rig.Theme, result, _ => true);
            dialog.SavePicker = _ => path;

            Finish(dialog.SaveAsync());

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(new UTF8Encoding(false).GetBytes(result.Json), bytes);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no byte order mark");
            Assert.Equal(Strings.Get("gui.hw.pcb1.saved"), dialog.Result);
            Assert.True(dialog.SaveButton.Enabled);
            Assert.Equal([path], Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_cancelled_save_writes_nothing_and_says_nothing()
    {
        using var rig = new Rig(Language.En);
        var result = await PreviewResult();
        using var dialog = new BuildCodeDialog(rig.Theme, result, _ => true);
        var asked = 0;
        dialog.SavePicker = _ =>
        {
            asked++;
            return null;
        };

        Finish(dialog.SaveAsync());

        Assert.Equal(1, asked);
        Assert.Equal(string.Empty, dialog.Result);
        Assert.True(dialog.SaveButton.Enabled);
    }

    [Fact]
    public async Task A_failed_save_says_the_type_and_the_code_of_the_error_and_never_its_message_or_the_path()
    {
        using var rig = new Rig(Language.En);
        var result = await PreviewResult();
        var marker = "no-such-folder-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(Path.GetTempPath(), marker, "pc-config.json");
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        using var dialog = new BuildCodeDialog(rig.Theme, result, _ => true);
        dialog.SavePicker = _ => path;

        Finish(dialog.SaveAsync());

        Assert.StartsWith("Could not save (DirectoryNotFoundException 0x", dialog.Result, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, dialog.Result, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath(), dialog.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Could not find", dialog.Result, StringComparison.Ordinal);
        Assert.True(dialog.SaveButton.Enabled, "the save button comes back after a failure");
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)), "nothing was created");
    }

    [Fact]
    public async Task Under_the_test_hook_the_dialog_only_logs_and_does_not_keep_the_code_otherwise()
    {
        using var rig = new Rig(Language.En);
        var result = await PreviewResult();

        BuildCodeDialog.Open(null, rig.Theme, result, _ => true);

        Assert.Equal([Strings.Get("gui.hw.pcb1.title") + " | " + result.Code], Dialogs.Log);
    }

    // ---------------------------------------------------------------- the words

    [Fact]
    public void Every_category_note_kind_and_reason_the_dialog_can_show_has_words_in_both_languages()
    {
        var keys = BuildConfig.Categories.Select(static c => BuildCodeStrings.CategoryPrefix + c.Replace('-', '_'))
            .Concat(Enum.GetValues<BuildNoteKind>().Select(static k => BuildCodeStrings.NotePrefix + CheckFormat.Snake(k.ToString())))
            .Concat(Enum.GetValues<CheckReason>().Select(static r => BuildCodeStrings.ReasonPrefix + CheckFormat.Snake(r.ToString())))
            .Concat([BuildCodeStrings.NotePrefix + "unknown"])
            .ToArray();
        Assert.True(keys.Length >= BuildConfig.Categories.Count + Enum.GetValues<BuildNoteKind>().Length + Enum.GetValues<CheckReason>().Length);

        var missing = new List<string>();
        foreach (var key in keys)
        {
            if (!Registered.TryGetValue(key, out var entry) || string.IsNullOrWhiteSpace(entry.Ja) || string.IsNullOrWhiteSpace(entry.En) || entry.Ja == "[" + key + "]" || entry.En == "[" + key + "]")
            {
                missing.Add(key);
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_registered_pcb1_word_is_written_in_both_languages_with_the_same_placeholders()
    {
        Assert.NotEmpty(BuildCodeStrings.All);
        foreach (var entry in BuildCodeStrings.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Ja), entry.Key + " ja");
            Assert.False(string.IsNullOrWhiteSpace(entry.En), entry.Key + " en");
            Assert.Equal(Placeholders(entry.Ja), Placeholders(entry.En));
        }

        static string[] Placeholders(string text) =>
            [.. System.Text.RegularExpressions.Regex.Matches(text, @"\{(\w+)\}").Select(static m => m.Groups[1].Value).Order(StringComparer.Ordinal)];
    }

    [Theory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    public void Every_note_kind_makes_a_sentence_of_its_own_with_no_placeholder_left(Language language)
    {
        using var _ = Harness.Locale(language);
        var sentences = new List<string>();
        foreach (var kind in Enum.GetValues<BuildNoteKind>())
        {
            var text = BuildCodeText.Note(new BuildNote(kind, "ssd", 3));
            Assert.DoesNotContain("{", text, StringComparison.Ordinal);
            Assert.DoesNotContain("}", text, StringComparison.Ordinal);
            Assert.DoesNotContain("[gui.", text, StringComparison.Ordinal);
            Assert.StartsWith(Word("gui.hw.pcb1.cat.ssd", language), text, StringComparison.Ordinal);
            sentences.Add(text);
        }

        Assert.Equal(Enum.GetValues<BuildNoteKind>().Length, sentences.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    public void A_note_kind_or_category_this_version_has_no_words_for_is_still_said_and_never_dropped(Language language)
    {
        using var _ = Harness.Locale(language);

        var note = BuildCodeText.Note(new BuildNote((BuildNoteKind)999, "ssd", 1));
        Assert.Contains("999", note, StringComparison.Ordinal);
        Assert.StartsWith(Word("gui.hw.pcb1.cat.ssd", language), note, StringComparison.Ordinal);
        Assert.Equal("future-part", BuildCodeText.Category("future-part"));
        Assert.Equal("future-part: " + Word("gui.hw.pcb1.reason.timeout", language), BuildCodeText.UnreadLine(new BuildUnread("future-part", CheckReason.Timeout)));
    }

    [Fact]
    public void The_preview_notes_show_every_kind_and_the_preview_unread_show_every_reason_but_one_at_least_three()
    {
        Assert.Equal(
            Enum.GetValues<BuildNoteKind>().Order().ToArray(),
            FakeBuildCode.PreviewNotes.Select(static n => n.Kind).Distinct().Order().ToArray());
        Assert.True(FakeBuildCode.PreviewUnread.Select(static u => u.Reason).Distinct().Count() >= 3);
        Assert.All(FakeBuildCode.PreviewNotes, static n => Assert.Contains(n.Category, BuildConfig.Categories));
        Assert.All(FakeBuildCode.PreviewUnread, static u => Assert.Contains(u.Category, BuildConfig.Categories));
    }
}
