using System.Diagnostics;
using System.Text.Json;
using PcMieru.Core.Hardware.Share;

namespace PcMieru.Gui.Tests;

/// <summary>
/// "型番だけコピー（サイト用）" on the hardware page: the button, the dialog that shows the exact text before anything is copied, and the
/// strings. The dialog never opens in a test (<see cref="Dialogs.AutoAnswer"/>); its controls are built and read directly.
/// </summary>
public class PartsShareUiTests
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

    // The page's continuations come back through the message loop, so the test thread has to run it while it waits.
    private static void Finish(Task task)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.ElapsedMilliseconds < 10_000)
        {
            Application.DoEvents();
            Thread.Sleep(2);
        }

        Assert.True(task.IsCompleted, "the action did not finish");
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

    private const string Note = "シリアル番号・MAC・PC の名前などは含みません";

    [Fact]
    public void The_new_strings_are_registered_in_both_languages_and_the_note_says_what_the_coordinator_asked()
    {
        var keys = new[] { "gui.hw.share", "gui.hw.share.tip", "gui.hw.share.lead", "gui.hw.share.note", "gui.hw.share.json", "gui.hw.share.failed", "gui.hw.share.error" };
        var registered = GuiStrings.All.ToDictionary(e => e.Key, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            Assert.True(registered.TryGetValue(key, out var entry), key + " is not registered");
            Assert.False(string.IsNullOrWhiteSpace(entry.Ja), key + " ja");
            Assert.False(string.IsNullOrWhiteSpace(entry.En), key + " en");
        }

        Assert.Equal("型番だけコピー（サイト用）", registered["gui.hw.share"].Ja);
        Assert.Equal("Copy model numbers (for website)", registered["gui.hw.share"].En);
        Assert.Equal(Note, registered["gui.hw.share.note"].Ja);
    }

    [Fact]
    public async Task The_dialog_shows_exactly_the_text_the_copy_button_will_put_on_the_clipboard()
    {
        using var rig = new Rig(Language.Ja);
        var json = await PartsShare.CreateJsonAsync((category, ct) => rig.Services.Hardware.GetAsync(category, ct), AppInfo.Version, includeUsb: false);
        var copied = new List<string>();
        using var dialog = new PartsShareDialog(rig.Theme, json, text =>
        {
            copied.Add(text);
            return true;
        });

        Assert.Equal(json, dialog.ShownText);
        Assert.Equal(Note, dialog.Note);
        Assert.Equal(string.Empty, dialog.Result);

        dialog.CopyNow();

        Assert.Equal([json], copied);
        Assert.Equal(Strings.Get("gui.common.copied"), dialog.Result);
        using var parsed = JsonDocument.Parse(copied[0]);
        Assert.Equal("pcm-parts/1", parsed.RootElement.GetProperty("format").GetString());
    }

    [Fact]
    public void A_long_document_is_shown_in_full_and_copied_in_full()
    {
        using var rig = new Rig();
        // Far longer than a text box's default input limit; the person must be able to read all of what is copied.
        var json = "{\n" + string.Concat(Enumerable.Range(0, 4000).Select(i => $"  \"k{i}\": \"{new string('x', 40)}\",\n")) + "  \"end\": 1\n}";
        string? copied = null;
        using var dialog = new PartsShareDialog(rig.Theme, json, text =>
        {
            copied = text;
            return true;
        });

        Assert.True(json.Length > 100_000);
        Assert.Equal(json, dialog.ShownText);
        dialog.CopyNow();
        Assert.Equal(json, copied);
    }

    [Fact]
    public void A_busy_clipboard_is_said_so_and_is_not_a_crash()
    {
        using var rig = new Rig(Language.Ja);
        using var dialog = new PartsShareDialog(rig.Theme, "{}", _ => false);

        dialog.CopyNow();

        Assert.Equal(Strings.Get("gui.hw.share.failed"), dialog.Result);
        Assert.Contains("クリップボード", dialog.Result, StringComparison.Ordinal);
    }

    [Fact]
    public void The_dialog_has_a_copy_and_a_close_button_and_nothing_that_opens_a_site()
    {
        using var rig = new Rig(Language.En);
        using var dialog = new PartsShareDialog(rig.Theme, "{}", _ => true);

        var buttons = AllControls(dialog).OfType<Button>().Select(b => b.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["shareClose", "shareCopy"], buttons);
        Assert.Empty(AllControls(dialog).OfType<LinkLabel>());
        Assert.Empty(AllControls(dialog).OfType<WebBrowser>());
        var shown = AllControls(dialog).OfType<TextBox>().Single();
        Assert.True(shown.ReadOnly, "the shown text is for reading; what is copied must be what is shown");
    }

    [Theory]
    [InlineData(Language.Ja, "型番だけコピー（サイト用）")]
    [InlineData(Language.En, "Copy model numbers (for website)")]
    public void The_page_has_the_button_with_the_text_of_the_language(Language language, string text)
    {
        using var rig = new Rig(language);
        using var page = new HardwarePage(rig.Context);

        Assert.Equal("partsShare", page.ShareButton.Name);
        Assert.Equal(text, page.ShareButton.Text);
        Assert.Contains(page.ShareButton, AllControls(page));
    }

    [Fact]
    public void The_button_text_follows_a_language_change()
    {
        using var rig = new Rig(Language.En);
        using var page = new HardwarePage(rig.Context);

        Strings.Current = Language.Ja;
        page.ApplyLanguage();

        Assert.Equal("型番だけコピー（サイト用）", page.ShareButton.Text);
    }

    [Fact]
    public void Pressing_the_button_collects_the_document_and_shows_it_without_copying_anything()
    {
        using var rig = new Rig(Language.En);
        using var page = new HardwarePage(rig.Context);

        Finish(page.ShareAsync());

        var entry = Assert.Single(Dialogs.Log);
        Assert.StartsWith(Strings.Get("gui.hw.share") + " | ", entry, StringComparison.Ordinal);
        var json = entry[(Strings.Get("gui.hw.share").Length + 3)..];
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        Assert.Equal("pcm-parts/1", root.GetProperty("format").GetString());
        Assert.Equal("PCMieru " + AppInfo.Version, root.GetProperty("app").GetString());
        Assert.False(root.TryGetProperty("usb", out _), "USB ids are only for the command line's --include-usb");
        foreach (var secret in new[] { "SN-FAKE-0000", "S6B0NG0R000000X", "ZFN00000", "00-00-5E", "192.0.2", "Fake BIOS Vendor", "USB Composite Device" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_failure_message_is_registered_with_its_placeholder_in_both_languages()
    {
        var registered = GuiStrings.All.ToDictionary(e => e.Key, StringComparer.Ordinal);
        Assert.True(registered.TryGetValue("gui.hw.share.error", out var entry));
        Assert.Contains("{error}", entry.Ja, StringComparison.Ordinal);
        Assert.Contains("{error}", entry.En, StringComparison.Ordinal);
    }

    [Fact]
    public void While_the_list_is_collected_the_button_is_off_and_a_second_press_does_nothing()
    {
        using var rig = new Rig(Language.En);
        using var page = new HardwarePage(rig.Context);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        page.ShareReader = async (category, ct) =>
        {
            Interlocked.Increment(ref reads);
            await gate.Task;
            return await rig.Services.Hardware.GetAsync(category, ct);
        };
        Assert.True(page.ShareButton.Enabled);

        var first = page.ShareAsync();
        Assert.False(first.IsCompleted);
        Assert.False(page.ShareButton.Enabled, "the button stays off while the list is collected");
        var readsOfTheFirstPress = reads;
        Assert.Equal(PartsShare.Categories(includeUsb: false).Count, readsOfTheFirstPress);

        var second = page.ShareAsync();
        Assert.True(second.IsCompleted, "a press while one is running returns at once");
        Assert.Equal(readsOfTheFirstPress, reads);

        gate.SetResult(true);
        Finish(first);

        Assert.True(page.ShareButton.Enabled, "the button comes back when the dialog is closed");
        Assert.Equal(readsOfTheFirstPress, reads);
        Assert.Single(Dialogs.Log);
    }

    [Fact]
    public void A_collection_failure_is_said_in_a_dialog_and_the_button_comes_back()
    {
        using var rig = new Rig(Language.En);
        using var page = new HardwarePage(rig.Context);
        page.ShareReader = (_, _) => Task.FromException<PcMieru.Core.Hardware.Model.HardwareInfo>(new InvalidOperationException("the service is not answering"));

        Finish(page.ShareAsync());

        var entry = Assert.Single(Dialogs.Log);
        Assert.StartsWith(Strings.Get("gui.hw.share") + " | ", entry, StringComparison.Ordinal);
        Assert.Contains("the service is not answering", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("{error}", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("pcm-parts/1", entry, StringComparison.Ordinal);
        Assert.True(page.ShareButton.Enabled);

        // And it can be pressed again.
        page.ShareReader = null;
        Finish(page.ShareAsync());
        Assert.Equal(2, Dialogs.Log.Count);
        Assert.Contains("pcm-parts/1", Dialogs.Log[1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_document_is_not_kept_in_the_dialog_log_when_the_dialog_really_opens()
    {
        // Dialogs.Log lives as long as the app. Only the test hook (AutoAnswer) writes the document there; a real dialog does not.
        using var rig = new Rig(Language.En);
        Dialogs.AutoAnswer = null;
        var closer = new System.Windows.Forms.Timer { Interval = 20 };
        var clock = Stopwatch.StartNew();
        var closed = false;
        closer.Tick += (_, _) =>
        {
            foreach (var form in Application.OpenForms.OfType<PartsShareDialog>().ToList())
            {
                closed = true;
                form.Close();
            }

            if (clock.ElapsedMilliseconds > 8_000)
            {
                closer.Stop();
            }
        };
        closer.Start();
        try
        {
            PartsShareDialog.Open(null, rig.Theme, "{\n  \"format\": \"pcm-parts/1\"\n}", _ => true);
        }
        finally
        {
            closer.Stop();
            closer.Dispose();
            Dialogs.AutoAnswer = false;
        }

        Assert.True(closed, "the dialog was shown and closed by the test");
        Assert.Empty(Dialogs.Log);
    }

    [Fact]
    public void The_button_press_goes_through_the_click_handler()
    {
        using var rig = new Rig(Language.En);
        using var page = new HardwarePage(rig.Context);

        page.ShareButton.PerformClick();
        var clock = Stopwatch.StartNew();
        while (Dialogs.Log.Count == 0 && clock.ElapsedMilliseconds < 10_000)
        {
            Application.DoEvents();
            Thread.Sleep(2);
        }

        Assert.Single(Dialogs.Log);
    }

    [Theory]
    [InlineData(Language.En)]
    [InlineData(Language.Ja)]
    public void The_share_button_has_its_own_row_under_the_bar_and_fits_at_the_smallest_window(Language language)
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

        var bars = AllControls(page).OfType<TableLayoutPanel>().ToDictionary(c => c.Name, c => c);
        var main = bars["partsBar"];
        var own = bars["partsShareBar"];

        // The first bar is as wide as it was before the button existed: it is not given the button.
        Assert.DoesNotContain(page.ShareButton, main.Controls.Cast<Control>());
        Assert.Equal(
            new[] { "partsCopyJson", "partsCopyText", "partsRedact", "partsReload" },
            main.Controls.Cast<Control>().Select(c => c.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Same(own, page.ShareButton.Parent);

        // The button's row is under the first bar, inside the page, and the button is not cut off at the right.
        own.PerformLayout();
        var needed = own.GetPreferredSize(new Size(0, 0)).Width;
        Assert.True(needed <= host.ClientSize.Width, $"the share row needs {needed} px but the page is {host.ClientSize.Width} px wide ({language})");
        Assert.True(own.Top >= main.Bottom, $"the share row starts at {own.Top}, the first bar ends at {main.Bottom}");
        Assert.True(page.ShareButton.Right <= own.ClientSize.Width, $"the share button ends at {page.ShareButton.Right} in a row {own.ClientSize.Width} px wide");
    }
}
