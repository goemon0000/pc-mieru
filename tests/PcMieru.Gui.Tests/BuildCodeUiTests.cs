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

    /// <summary>A provider whose task ends cancelled, on a token of its own (never the one the page gave it).</summary>
    private sealed class CancelledTaskProvider : IBuildCodeProvider
    {
        public Task<BuildCodeResult> CreateAsync(CancellationToken ct)
        {
            using var own = new CancellationTokenSource();
            own.Cancel();
            return Task.FromCanceled<BuildCodeResult>(own.Token);
        }
    }

    /// <summary>A provider that waits for the token it is given (the page's own) and says when it is waiting.</summary>
    private sealed class WaitingProvider : IBuildCodeProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BuildCodeResult> CreateAsync(CancellationToken ct)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            throw new InvalidOperationException("the wait ended without a cancel");
        }
    }

    /// <summary>A provider that notes the thread it runs on and then fails.</summary>
    private sealed class ThreadNotingProvider : IBuildCodeProvider
    {
        public int ThreadId { get; private set; }

        public Task<BuildCodeResult> CreateAsync(CancellationToken ct)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            throw new InvalidOperationException("this text must not be shown");
        }
    }

    private static readonly Dictionary<string, StringEntry> Registered = GuiStrings.All.ToDictionary(static e => e.Key, StringComparer.Ordinal);

    private static string Word(string key, Language language) => language == Language.Ja ? Registered[key].Ja : Registered[key].En;

    private static async Task<BuildCodeResult> PreviewResult() => await new FakeBuildCode().CreateAsync(CancellationToken.None);

    /// <summary>The large sample the real rules cut to the limit: it has notes and unread categories, every one of them the library's own.</summary>
    private static async Task<BuildCodeResult> TrimmedResult() => await new FakeBuildCode(SampleVariant.Trimmed).CreateAsync(CancellationToken.None);

    /// <summary>The mark in front of a line of a list, as it must be written (worked out here, not through the code under test).</summary>
    private static string ExpectedBullet(Language language) => language == Language.Ja ? "・" : "• ";

    /// <summary>The details of a part as the dialog must write them, worked out here: the interface, the capacity with a thousands comma, the speed, the video memory and the maker.</summary>
    private static string ExpectedDetail(BuildPart part, Language language)
    {
        var items = new List<string>();
        if (!string.IsNullOrEmpty(part.Type))
        {
            items.Add(part.Type.ToLowerInvariant() switch { "nvme" => "NVMe", "sata" => "SATA", "usb" => "USB", "sas" => "SAS", _ => part.Type.ToUpperInvariant() });
        }

        if (part.Cap is { } cap)
        {
            items.Add(cap.ToString("N0", CultureInfo.InvariantCulture) + " GB");
        }

        if (part.Speed is { } speed)
        {
            items.Add(speed.ToString(CultureInfo.InvariantCulture) + " MT/s");
        }

        if (part.Vram is { } vram)
        {
            items.Add("VRAM " + vram.ToString(CultureInfo.InvariantCulture) + " GB");
        }

        if (!string.IsNullOrEmpty(part.Mfr))
        {
            items.Add(part.Mfr);
        }

        return string.Join(language == Language.Ja ? " ・ " : " · ", items);
    }

    /// <summary>The contrast of two colours as WCAG 2 defines it (1 to 21).</summary>
    internal static double ContrastRatio(Color a, Color b)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color color) => (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));

        var (high, low) = (Luminance(a), Luminance(b));
        if (high < low)
        {
            (high, low) = (low, high);
        }

        return (high + 0.05) / (low + 0.05);
    }

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

    [Fact]
    public void A_page_can_be_disposed_twice_as_a_control_can_be_by_its_parent_and_its_owner()
    {
        using var rig = new Rig(Language.En);
        var page = new HardwarePage(rig.Context);

        page.Dispose();
        page.Dispose();

        Assert.True(page.IsDisposed);
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
        // The trimmed sample: a result the real rules cut to the limit, so it has notes and unread categories that the library wrote.
        var result = await TrimmedResult();
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
            Assert.Equal(ExpectedDetail(part, language), line.Detail);
            Assert.Equal(part.Q.ToString(CultureInfo.InvariantCulture), line.Count);
        }

        // The code: exactly the provider's, in a box that cannot be edited.
        Assert.StartsWith("PCB1.", result.Code, StringComparison.Ordinal);
        Assert.Equal(result.Code, dialog.CodeText);
        Assert.True(AllControls(dialog).OfType<TextBox>().Single(static t => t.Name == "pcb1Code").ReadOnly);

        // The notes: one sentence per note the library wrote, in the language, in its order (the dialog adds none of its own).
        Assert.True(result.Notes.Count >= 3);
        var noteLines = dialog.NotesText.Split('\n');
        Assert.Equal(result.Notes.Count, noteLines.Length);
        for (var i = 0; i < result.Notes.Count; i++)
        {
            Assert.Equal(ExpectedBullet(language) + ExpectedNote(result.Notes[i], language), noteLines[i]);
            Assert.DoesNotContain("{", noteLines[i], StringComparison.Ordinal);
        }

        // The categories that could not be read, each with its reason.
        Assert.True(result.Unread.Count >= 3);
        var unreadLines = dialog.UnreadText.Split('\n');
        Assert.Equal(result.Unread.Count, unreadLines.Length);
        for (var i = 0; i < result.Unread.Count; i++)
        {
            Assert.Equal(ExpectedBullet(language) + ExpectedUnread(result.Unread[i], language), unreadLines[i]);
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

    [Theory]
    [InlineData(Language.Ja, "コピーも保存もできません")]
    [InlineData(Language.En, "nothing to copy or save")]
    public async Task A_configuration_with_no_part_has_nothing_to_copy_or_save_and_the_dialog_says_so(Language language, string said)
    {
        using var rig = new Rig(language);
        var result = await PreviewResult();
        var copied = new List<string>();
        var asked = 0;
        using var empty = new BuildCodeDialog(rig.Theme, result with { Config = new BuildConfig(), Notes = [], Unread = [] }, text =>
        {
            copied.Add(text);
            return true;
        });
        empty.SavePicker = _ =>
        {
            asked++;
            return Path.Combine(Path.GetTempPath(), "pcmieru-never-written-" + Guid.NewGuid().ToString("N") + ".json");
        };

        // Both buttons are off, the line says why, and nothing happens when the handlers are run anyway.
        Assert.False(empty.CopyButton.Enabled, "Copy is off for an empty configuration");
        Assert.False(empty.SaveButton.Enabled, "Save is off for an empty configuration");
        var none = AllControls(empty).OfType<Label>().Single(static c => c.Name == "pcb1PartsNone");
        Assert.Equal(Word("gui.hw.pcb1.parts.none", language), none.Text);
        Assert.Contains(said, none.Text, StringComparison.Ordinal);
        Assert.Same(AllControls(empty).OfType<Button>().Single(static b => b.Name == "pcb1Close"), empty.AcceptButton);
        empty.CopyNow();
        Finish(empty.SaveAsync());
        Assert.Empty(copied);
        Assert.Equal(0, asked);
        Assert.Equal(string.Empty, empty.Result);

        // A configuration with parts: both are on and there is no such line.
        using var full = new BuildCodeDialog(rig.Theme, result, _ => true);
        Assert.True(full.CopyButton.Enabled);
        Assert.True(full.SaveButton.Enabled);
        Assert.DoesNotContain(AllControls(full), static c => c.Name == "pcb1PartsNone");
        Assert.Same(full.CopyButton, full.AcceptButton);
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
            // Nothing is added at the end: the file ends where the provider's JSON ends (a closing brace), with no line break after it.
            Assert.EndsWith("}", result.Json, StringComparison.Ordinal);
            Assert.Equal((byte)'}', bytes[^1]);
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

    // ---------------------------------------------------------------- the sample the dialog shows (DESIGN-A §7: the Gui writes no note of its own)

    [Fact]
    public async Task The_fake_gives_exactly_the_library_sample_and_lays_nothing_over_it()
    {
        foreach (var variant in Enum.GetValues<SampleVariant>())
        {
            var fake = await new FakeBuildCode(variant).CreateAsync(CancellationToken.None);
            var library = await BuildCodeProviders.Sample(variant).CreateAsync(CancellationToken.None);

            Assert.Equal(library.Code, fake.Code);
            Assert.Equal(library.Json, fake.Json);
            Assert.Equal(library.Notes, fake.Notes);
            Assert.Equal(library.Unread, fake.Unread);
        }

        // The plain sample was read in full; the trimmed one says what the rules did and which categories were not read, and nothing else.
        var plain = await new FakeBuildCode().CreateAsync(CancellationToken.None);
        Assert.Empty(plain.Notes);
        Assert.Empty(plain.Unread);
        var trimmed = await TrimmedResult();
        Assert.Equal(
            new[]
            {
                (BuildNoteKind.KindsLeftOut, "ssd", 2),
                (BuildNoteKind.CategoryLeftOutForLength, "optical-drive", 0),
                (BuildNoteKind.CategoryLeftOutForLength, "hdd", 0),
                (BuildNoteKind.MemoryFirstKindOnly, "ram", 0),
            },
            trimmed.Notes.Select(static n => (n.Kind, n.Category, n.Count)).ToArray());
        Assert.Equal(
            new[] { ("gpu", CheckReason.Timeout), ("motherboard", CheckReason.Denied), ("monitor", CheckReason.Unsupported) },
            trimmed.Unread.Select(static u => (u.Category, u.Reason)).ToArray());
    }

    [Fact]
    public void The_fakes_source_makes_no_note_and_no_unread_entry()
    {
        // The object tests above cannot see a note that is made and then thrown away; the source can be read.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PcMieru.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var path = Path.Combine(dir.FullName, "src", "PcMieru.Gui", "Checks", "FakeBuildCode.cs");
        var text = File.ReadAllText(path);
        Assert.Contains("BuildCodeProviders.Sample(", text, StringComparison.Ordinal);
        foreach (var banned in new[] { "new BuildNote", "new BuildUnread", "BuildNoteKind.", " with {", "Notes =", "Unread =" })
        {
            Assert.DoesNotContain(banned, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_name_the_column_cuts_has_a_tooltip_with_the_whole_name_and_one_that_fits_has_none()
    {
        using var rig = new Rig(Language.En);
        var trimmed = await TrimmedResult();
        using var dialog = new BuildCodeDialog(rig.Theme, trimmed, _ => true);
        var grid = dialog.PartsList;

        var cut = 0;
        for (var row = 0; row < dialog.PartLines.Count; row++)
        {
            var tip = grid.CellTip?.Invoke(row, 1);
            if (grid.IsCut(row, 1))
            {
                cut++;
                Assert.Equal(dialog.PartLines[row].Name, tip);
            }
            else
            {
                Assert.Null(tip);
            }
        }

        // The sample's names are long on purpose: the column cuts them, which is what the tooltip is for.
        Assert.True(cut > 0, "no name of the trimmed sample is cut by its column, so the tooltip is not exercised");

        // The plain sample's names fit.
        using var plain = new BuildCodeDialog(rig.Theme, await PreviewResult(), _ => true);
        for (var row = 0; row < plain.PartLines.Count; row++)
        {
            Assert.False(plain.PartsList.IsCut(row, 1), plain.PartLines[row].Name);
            Assert.False(plain.PartsList.IsCut(row, 2), plain.PartLines[row].Detail);
            Assert.Null(plain.PartsList.CellTip?.Invoke(row, 1));
        }
    }

    [Theory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    public async Task The_code_box_is_whole_lines_high_and_the_dialog_gives_it_its_own_height(Language language)
    {
        using var rig = new Rig(language);
        var trimmed = await TrimmedResult();
        using var dialog = new BuildCodeDialog(rig.Theme, trimmed, _ => true);
        var box = dialog.CodeBox;

        // N lines of the code's font and the air round them: no part of a line at the bottom.
        var air = dialog.LogicalToDeviceUnits(BuildCodeDialog.CodeBoxPadding);
        Assert.Equal(box.Font.Height, dialog.CodeLineHeight);
        Assert.InRange(dialog.CodeLinesShown, 3, BuildCodeDialog.CodeLines);
        Assert.Equal((dialog.CodeLinesShown * dialog.CodeLineHeight) + air, dialog.CodeBoxClientHeight);
        Assert.Equal(dialog.CodeBoxClientHeight, box.ClientSize.Height);
        Assert.Equal(0, (box.ClientSize.Height - air) % dialog.CodeLineHeight);
        Assert.Equal(dialog.CodeLinesShown, (box.ClientSize.Height - air) / dialog.CodeLineHeight);
    }

    [Fact]
    public async Task A_screen_too_low_for_the_dialog_takes_rows_from_the_list_first_then_lines_from_the_code_box_and_a_roomy_one_gives_them_back()
    {
        using var rig = new Rig(Language.En);
        using var dialog = new BuildCodeDialog(rig.Theme, await TrimmedResult(), _ => true);

        // As much room as it wants: ten rows of the list, five lines of the box.
        var full = dialog.FitToHeight(int.MaxValue);
        Assert.Equal(10, dialog.PartLines.Count);
        Assert.Equal(10, dialog.PartRowsShown);
        Assert.Equal(BuildCodeDialog.CodeLines, dialog.CodeLinesShown);

        // One pixel short: exactly one row goes, and what it was worth is the height of a row.
        var oneRowLess = dialog.FitToHeight(full - 1);
        Assert.Equal(9, dialog.PartRowsShown);
        var rowHeight = full - oneRowLess;
        Assert.True(rowHeight > 0, "one row fewer made the window lower");

        // Room for the window less two rows and a pixel: three rows go, and the box keeps its lines.
        var available = full - (2 * rowHeight) - 1;
        var fitted = dialog.FitToHeight(available);
        Assert.Equal(7, dialog.PartRowsShown);
        Assert.Equal(BuildCodeDialog.CodeLines, dialog.CodeLinesShown);
        Assert.True(fitted <= available, $"{fitted} px in {available} px");

        // No room at all: the list and the box stop at their least, and the window is as low as it can be.
        var least = dialog.FitToHeight(0);
        Assert.Equal(3, dialog.PartRowsShown);
        Assert.Equal(3, dialog.CodeLinesShown);
        Assert.True(least < full);
        Assert.Equal(dialog.CodeBoxClientHeight, dialog.CodeBox.ClientSize.Height);

        // A roomier screen: everything comes back.
        Assert.Equal(full, dialog.FitToHeight(int.MaxValue));
        Assert.Equal(10, dialog.PartRowsShown);
        Assert.Equal(BuildCodeDialog.CodeLines, dialog.CodeLinesShown);
    }

    [Theory]
    [InlineData(1.0f)]
    [InlineData(1.5f)]
    public async Task The_parts_list_ends_on_a_whole_row_below_its_own_header_also_with_a_bigger_font(float factor)
    {
        using var rig = new Rig(Language.En);
        var basis = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
        using var font = new Font(basis.FontFamily, basis.SizeInPoints * factor);
        using var dialog = new BuildCodeDialog(rig.Theme, await TrimmedResult(), _ => true);
        dialog.Font = font;
        _ = dialog.Handle;
        var grid = dialog.PartsList;
        _ = grid.Handle;

        // The native list lays its rows out against its header only in a window that is on show (as the smoke run shows it, without taking the focus).
        dialog.PreventActivation();
        dialog.Show();
        Application.DoEvents();
        dialog.PerformLayout();
        Application.DoEvents();

        foreach (var room in new[] { int.MaxValue, 0 })
        {
            dialog.FitToHeight(room);
            var rows = dialog.PartRowsShown;
            Assert.InRange(rows, 1, dialog.PartLines.Count);

            // The header, N whole rows and the border: what is under the header is N rows high, as the list measures a row, and no more.
            var body = grid.ClientSize.Height - grid.HeaderHeight;
            Assert.Equal(rows * grid.MeasuredRowHeight, body);
            Assert.Equal(grid.HeightForRows(rows), grid.Height);
            Assert.Equal(rows, grid.RowsThatFit(grid.Height));

            // One pixel less would take a row away; one row's height more would show another whole row.
            Assert.Equal(Math.Max(rows - 1, 0), grid.RowsThatFit(grid.Height - 1));
            Assert.Equal(rows + 1, grid.RowsThatFit(grid.Height + grid.MeasuredRowHeight));

            // Where the native list puts the rows (the first under the header, the last ending at the bottom, no part of another below) is the
            // smoke run's check (WholeRowsProblem on the window that is really on show): a list in a window no message loop has run for is
            // not laid out like that one, and the test would be pinning the test's own window.
        }
    }

    [Theory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    public void The_opening_text_says_where_Copy_and_Save_put_the_code_and_that_nothing_is_sent(Language language)
    {
        using var _ = Harness.Locale(language);
        var lead = Strings.Get("gui.hw.pcb1.lead");

        // The two buttons are named as they are on the buttons, and each is said to put the content in one place.
        Assert.Contains(Strings.Get("gui.common.copy"), lead, StringComparison.Ordinal);
        Assert.Contains(Strings.Get("gui.hw.pcb1.save"), lead, StringComparison.Ordinal);
        var words = language == Language.Ja
            ? new[] { "クリップボード", "読める形のファイル", "選んだ場所", "何も送信しません", "シリアル番号", "MAC アドレス", "PC の名前" }
            : new[] { "on the clipboard", "readable JSON file", "a place you choose", "sends nothing to the site or anywhere else", "serial numbers", "MAC addresses", "the PC's name" };
        foreach (var word in words)
        {
            Assert.Contains(word, lead, StringComparison.Ordinal);
        }

        // The sentences that read as contradicting each other are gone.
        Assert.DoesNotContain("Nothing is sent", lead, StringComparison.Ordinal);
        Assert.DoesNotContain("leaves this window", lead, StringComparison.Ordinal);
        Assert.DoesNotContain("画面の外に出", lead, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_columns_of_the_list_follow_what_the_rows_need_when_the_font_and_the_window_grow()
    {
        using var rig = new Rig(Language.En);
        var basis = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
        using var bigger = new Font(basis.FontFamily, basis.SizeInPoints * 1.5f);
        using var dialog = new BuildCodeDialog(rig.Theme, await PreviewResult(), _ => true);
        _ = dialog.Handle;
        var grid = dialog.PartsList;

        // The font grows by half and the window with it (what a denser screen does): nothing is cut, whatever the columns were before.
        dialog.Font = bigger;
        grid.Width = grid.Width * 3 / 2;
        for (var row = 0; row < dialog.PartLines.Count; row++)
        {
            for (var column = 0; column < grid.Columns.Count; column++)
            {
                Assert.False(grid.IsCut(row, column), $"row {row} column {column}: {grid.Cells!(row, column).Text} in {grid.Columns[column].Width} px");
            }
        }
    }

    // ---------------------------------------------------------------- how the numbers and the words are written

    [Theory]
    [InlineData("nvme", "NVMe")]
    [InlineData("NVME", "NVMe")]
    [InlineData("sata", "SATA")]
    [InlineData("Usb", "USB")]
    [InlineData("sas", "SAS")]
    [InlineData("scsi", "SCSI")]
    [InlineData("raid", "RAID")]
    public void A_storage_interface_is_written_the_way_it_is_on_the_box_and_an_unknown_one_in_capitals(string type, string shown)
    {
        using var _ = Harness.Locale(Language.En);

        Assert.Equal(shown, BuildCodeText.TypeName(type));
        Assert.Equal(shown, BuildCodeText.Detail(new BuildPart("Some drive", Type: type)));
    }

    [Theory]
    [InlineData(Language.En, "USB · 2,000 GB · 4800 MT/s · VRAM 12 GB · Acme")]
    [InlineData(Language.Ja, "USB ・ 2,000 GB ・ 4800 MT/s ・ VRAM 12 GB ・ Acme")]
    public void The_details_are_separated_by_the_languages_own_mark(Language language, string expected)
    {
        using var _ = Harness.Locale(language);

        Assert.Equal(expected, BuildCodeText.Detail(new BuildPart("Some part", Cap: 2000, Speed: 4800, Type: "usb", Mfr: "Acme", Vram: 12)));
        Assert.Equal(string.Empty, BuildCodeText.Detail(new BuildPart("Only a name")));
    }

    [Fact]
    public async Task The_capacity_is_written_with_the_invariant_culture_whatever_the_current_one_is()
    {
        using var rig = new Rig(Language.En);
        var trimmed = await TrimmedResult();
        // Set after the rig: the rig puts the invariant culture on the thread. A German computer writes 2.000 for two thousand.
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        Assert.Equal("2.000", 2000.ToString("N0", CultureInfo.CurrentCulture));
        using var dialog = new BuildCodeDialog(rig.Theme, trimmed, _ => true);

        var disks = dialog.PartLines.Where(static l => l.Category == "SSD").ToList();
        Assert.NotEmpty(disks);
        Assert.All(disks, static l => Assert.EndsWith(" · 2,000 GB", l.Detail, StringComparison.Ordinal));
        Assert.Contains(disks, static l => l.Detail == "NVMe · 2,000 GB");
        Assert.Contains(disks, static l => l.Detail == "SATA · 2,000 GB");
        Assert.DoesNotContain(dialog.PartLines, static l => l.Detail.Contains("2.000", StringComparison.Ordinal));

        // The same for the counts a note names (the notes say "2", the limit says "6,000").
        Assert.Contains("6,000", BuildCodeText.Note(new BuildNote(BuildNoteKind.CategoryLeftOutForLength, "hdd")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    public void The_same_one_part_note_repeated_for_one_category_is_one_line_with_a_count(Language language)
    {
        using var _ = Harness.Locale(language);
        var one = new BuildNote(BuildNoteKind.OnePartLeftOutForLength, "ssd");
        var gpu = new BuildNote(BuildNoteKind.OnePartLeftOutForLength, "gpu");
        var kinds = new BuildNote(BuildNoteKind.KindsLeftOut, "ssd", 2);
        var memory = new BuildNote(BuildNoteKind.MemoryFirstKindOnly, "ram");
        string Many(string category, int count) => Word("gui.hw.pcb1.note.one_part_left_out_for_length_many", language)
            .Replace("{category}", Word("gui.hw.pcb1.cat." + category, language), StringComparison.Ordinal)
            .Replace("{count}", count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        // Three of the same: one line that counts them.
        Assert.Equal([Many("ssd", 3)], BuildCodeText.NoteLines([one, one, one]));
        // One: the sentence of the single note, exactly as the note itself reads.
        Assert.Equal([ExpectedNote(one, language)], BuildCodeText.NoteLines([one]));
        Assert.Equal(BuildCodeText.Note(one), Assert.Single(BuildCodeText.NoteLines([one])));
        Assert.NotEqual(Many("ssd", 1), BuildCodeText.NoteLines([one])[0]);

        // Mixed: the repeated one stands where it first came, each category has its own count, every other note is one line of its own, in order.
        var lines = BuildCodeText.NoteLines([kinds, one, gpu, one, memory, one, gpu]);
        Assert.Equal(
            [ExpectedNote(kinds, language), Many("ssd", 3), Many("gpu", 2), ExpectedNote(memory, language)],
            lines);

        // The same note of another kind is never merged, and a list of no note is no line.
        Assert.Equal([ExpectedNote(kinds, language), ExpectedNote(kinds, language)], BuildCodeText.NoteLines([kinds, kinds]));
        Assert.Empty(BuildCodeText.NoteLines([]));
    }

    [Fact]
    public async Task The_dialog_shows_a_repeated_one_part_note_as_one_line_and_leaves_the_notes_of_the_result_alone()
    {
        using var rig = new Rig(Language.En);
        var result = await PreviewResult();
        var one = new BuildNote(BuildNoteKind.OnePartLeftOutForLength, "ssd");
        var notes = new[] { one, one, one };
        using var dialog = new BuildCodeDialog(rig.Theme, result with { Notes = notes }, _ => true);

        Assert.Equal("• SSD: 3 parts were left out to fit", dialog.NotesText);
        Assert.Equal(3, notes.Length);
    }

    // ---------------------------------------------------------------- a read that is not ours to cancel, and one that is

    [Theory]
    [InlineData(0, "OperationCanceledException 0x8013153B")]
    [InlineData(1, "OperationCanceledException 0x8013153B")]
    [InlineData(2, "TaskCanceledException 0x8013153B")]
    public void A_provider_that_gives_up_on_a_token_of_its_own_is_a_failed_read_with_the_fixed_text(int shape, string detail)
    {
        using var rig = new Rig(Language.En);
        using var page = new HardwarePage(rig.Context);
        var secret = @"C:\Users\someone\SECRET-NAME was cancelled by its own timer";
        using var own = new CancellationTokenSource();
        own.Cancel();
        page.BuildCodeProvider = shape switch
        {
            0 => new ThrowingProvider(new OperationCanceledException(secret, own.Token), synchronous: true),
            1 => new ThrowingProvider(new OperationCanceledException(secret, own.Token), synchronous: false),
            _ => new CancelledTaskProvider(),
        };
        var statuses = new List<string>();
        rig.Context.StatusMessage += statuses.Add;

        Finish(page.BuildCodeAsync());

        // The page's own token was never cancelled, so this is a failure: the status says so, no dialog opens, and the button is back.
        Assert.Empty(Dialogs.Log);
        var status = Assert.Single(statuses);
        Assert.Equal(Strings.Format("gui.hw.pcb1.failed", Fmt.Dict(("detail", detail))), status);
        Assert.DoesNotContain("SECRET", status, StringComparison.Ordinal);
        Assert.True(page.BuildCodeButton.Enabled, "the button comes back after a failure");
    }

    [Fact]
    public void A_read_that_the_page_itself_cancels_ends_quietly_with_no_word_and_no_dialog()
    {
        Ui.Run(ui =>
        {
            using var rig = new Rig(Language.En);
            var page = new HardwarePage(rig.Context);
            var provider = new WaitingProvider();
            page.BuildCodeProvider = provider;
            var statuses = new List<string>();
            rig.Context.StatusMessage += statuses.Add;

            var run = page.BuildCodeAsync();
            Assert.True(ui.Until(() => provider.Entered.Task.IsCompleted), "the provider was not asked");
            Assert.False(run.IsCompleted);

            // The page goes away while the read runs: its own token is cancelled, the read ends, and nothing is said or opened.
            page.Dispose();
            Assert.True(ui.Until(() => run.IsCompleted), "the read did not end after the page was disposed");
            run.GetAwaiter().GetResult();

            Assert.Empty(statuses);
            Assert.Empty(Dialogs.Log);

            // A press on a page that is gone does nothing at all (the cancel source is disposed with the page).
            Assert.True(page.BuildCodeAsync().IsCompleted);
            Assert.Empty(statuses);
        });
    }

    [Fact]
    public void The_continuation_of_the_read_runs_on_the_context_the_page_was_pressed_on_and_the_read_on_another_thread()
    {
        Ui.Run(ui =>
        {
            using var rig = new Rig(Language.En);
            using var page = new HardwarePage(rig.Context);
            var provider = new ThreadNotingProvider();
            page.BuildCodeProvider = provider;
            var statusThreads = new List<int>();
            rig.Context.StatusMessage += _ => statusThreads.Add(Environment.CurrentManagedThreadId);

            var run = page.BuildCodeAsync();
            Assert.True(ui.Until(() => run.IsCompleted), "the read did not end");
            run.GetAwaiter().GetResult();

            // The read itself went to the pool; what follows it (the status line, the button) came back to the thread the context belongs to.
            Assert.NotEqual(ui.OwnerThread, provider.ThreadId);
            Assert.Equal([ui.OwnerThread], statusThreads);
            Assert.True(page.BuildCodeButton.Enabled);
        });
    }

    // ---------------------------------------------------------------- the clipboard of the shell

    [Fact]
    public void A_clipboard_that_fails_says_the_type_and_the_code_of_the_error_and_never_its_message()
    {
        using var rig = new Rig(Language.En);
        var statuses = new List<string>();
        rig.Context.StatusMessage += statuses.Add;
        var written = new List<string>();

        // It works: the text goes to the writer, the status says "copied".
        rig.Context.ClipboardWriter = written.Add;
        Assert.True(rig.Context.CopyText("PCB1.abc"));
        Assert.Equal(["PCB1.abc"], written);
        Assert.Equal([Strings.Get("gui.common.copied")], statuses);
        statuses.Clear();

        // It is busy (a COM error is what the clipboard throws): the fixed text and the code, never the OS's message (it can carry a path or a name).
        rig.Context.ClipboardWriter = _ => throw new System.Runtime.InteropServices.ExternalException(@"secret C:\Users\someone\path message", unchecked((int)0x800401D0));
        Assert.False(rig.Context.CopyText("PCB1.abc"));
        var status = Assert.Single(statuses);
        Assert.Equal(Strings.Format("gui.status.failed", Fmt.Dict(("error", "ExternalException 0x800401D0"))), status);
        Assert.DoesNotContain("secret", status, StringComparison.Ordinal);
        Assert.DoesNotContain("someone", status, StringComparison.Ordinal);
        statuses.Clear();

        // The other error the clipboard raises (the thread is not a single-threaded one).
        rig.Context.ClipboardWriter = _ => throw new ThreadStateException("secret thread message");
        Assert.False(rig.Context.CopyText("PCB1.abc"));
        status = Assert.Single(statuses);
        Assert.StartsWith("Failed: ThreadStateException 0x", status, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", status, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the fonts of the dialog

    [Fact]
    public async Task The_fonts_of_the_heads_and_the_code_box_stay_alive_when_they_are_made_again_and_follow_the_dialogs_font()
    {
        // The smoke run at 150% found it: the Font property keeps the font it has when the new one is equal in value, so a dialog that made its
        // fonts again at the same scale disposed the font its code box was still holding, and the first line height asked of it threw.
        using var rig = new Rig(Language.En);
        var result = await PreviewResult();
        using var grown = new Font("Segoe UI", 13.5f);
        using var dialog = new BuildCodeDialog(rig.Theme, result, _ => true);
        _ = dialog.Handle;

        dialog.RefreshFonts();
        dialog.RefreshFonts();
        Assert.True(dialog.CodeBox.Font.Height > 0);
        Assert.True(dialog.CodeLineHeight > 0);
        var heads = AllControls(dialog).OfType<Label>().Where(static l => l.Name.EndsWith("Head", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(heads);
        foreach (var head in heads)
        {
            Assert.True(head.Font.Height > 0, head.Name);
        }

        // The window goes to a higher scale: the box has a font of its own at the new size, still a tenth over the dialog's (the same visual size).
        var before = dialog.CodeBox.Font.SizeInPoints;
        dialog.Font = grown;
        Assert.True(dialog.CodeBox.Font.SizeInPoints > before, "the code box font follows the dialog font");
        Assert.Equal(grown.SizeInPoints * 1.1f, dialog.CodeBox.Font.SizeInPoints, 0.05f);
        Assert.True(dialog.CodeLineHeight > 0);
        Assert.All(heads, static head => Assert.True(head.Font.Height > 0, head.Name));
        Assert.True(dialog.FitToHeight(int.MaxValue) > 0);
    }

    // ---------------------------------------------------------------- the dim text of a selected row

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_dim_text_of_a_selected_row_has_at_least_the_contrast_of_normal_text_on_the_selection(bool dark)
    {
        var mode = dark ? ThemeMode.Dark : ThemeMode.Light;
        using var theme = new ThemeService(mode);
        var palette = theme.Palette;

        // WCAG 2 AA asks 4.5:1 for normal text. The computed colour is the one every grid draws a dim cell in on a selected row.
        var dim = GridView.SelectedDim(palette);
        var contrast = ContrastRatio(dim, palette.Selection);
        Assert.True(contrast >= 4.5, $"dim text on the selection is {contrast:F2}:1 in the {mode} theme");
        Assert.True(GridView.SelectedDimAmount >= 0.7, "the dim colour stays mostly the selection's own text colour");
        Assert.NotEqual(palette.SelectionText, dim);
        Assert.Equal(255, dim.A);
        // The text colour itself reads at least as well as its dimmed form: the dim form is a step down, never an improvement.
        Assert.True(ContrastRatio(palette.SelectionText, palette.Selection) >= contrast);
    }

    [Fact]
    public void The_old_quarter_blend_would_fail_the_contrast_the_test_asks_for()
    {
        // A test that nothing can fail proves nothing: the blend the grid had before (a quarter of the text colour) is under the line in the light theme.
        using var theme = new ThemeService(ThemeMode.Light);
        var palette = theme.Palette;
        Color Quarter(Color a, Color b) => Color.FromArgb((int)((a.R * 0.25) + (b.R * 0.75)), (int)((a.G * 0.25) + (b.G * 0.75)), (int)((a.B * 0.25) + (b.B * 0.75)));

        var old = Quarter(palette.SelectionText, palette.Selection);
        Assert.True(ContrastRatio(old, palette.Selection) < 4.5, ContrastRatio(old, palette.Selection).ToString("F2", CultureInfo.InvariantCulture));
    }
}
