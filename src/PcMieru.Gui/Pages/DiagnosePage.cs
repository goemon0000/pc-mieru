using System.Globalization;
using System.Text;
using PcMieru.Core.Reporting;

namespace PcMieru.Gui;

/// <summary>
/// The diagnostic report: watch the PC for a chosen number of seconds, then show a report (Markdown or JSON) that can be copied
/// or saved. Personal data is hidden by default and the page says which it is. The app never sends the report anywhere:
/// copying and saving are the only ways out, and the user decides where it goes.
/// </summary>
internal sealed class DiagnosePage : PageBase
{
    /// <summary>The choices for "watch for". The builder clamps to 30 seconds.</summary>
    internal static readonly int[] DurationChoices = [5, 10, 20, 30];

    internal const int DefaultSeconds = 10;

    private readonly Label _durationLabel = new() { AutoSize = true, Name = "diagnoseDurationLabel" };
    private readonly ComboBox _duration = new() { DropDownStyle = ComboBoxStyle.DropDownList, Name = "diagnoseDuration", Width = 110 };
    private readonly Button _start = new() { AutoSize = true, Name = "diagnoseStart" };
    private readonly RadioButton _viewMarkdown = new() { AutoSize = true, Name = "diagnoseViewMarkdown", Checked = true, Text = "Markdown" };
    private readonly RadioButton _viewJson = new() { AutoSize = true, Name = "diagnoseViewJson", Text = "JSON" };
    private readonly CheckBox _redact = new() { AutoSize = true, Name = "diagnoseRedact", Checked = true };
    private readonly Button _copy = new() { AutoSize = true, Enabled = false, Name = "diagnoseCopy" };
    private readonly Button _save = new() { AutoSize = true, Enabled = false, Name = "diagnoseSave" };
    private readonly ToneLabel _intro = new() { AutoSize = false, Name = "diagnoseIntro" };
    private readonly Label _state = new() { AutoSize = true, Name = "diagnoseState", Margin = new Padding(0, 0, 16, 0) };
    private readonly ToneLabel _pii = new() { AutoSize = true, Name = "diagnosePii" };
    private readonly TextBox _text = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Name = "diagnoseText", BorderStyle = BorderStyle.None };
    private readonly ToolTip _tips = new() { ShowAlways = true };
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };

    private Font? _mono;
    private CancellationTokenSource? _running;
    private ReportDocument? _document;
    private DateTime _startedUtc;
    private int _total;
    private bool _failed;
    private string? _failure;

    public DiagnosePage(GuiContext context)
        : base(context, "diagnose", "gui.nav.diagnose")
    {
        var redactor = context.Services.Redactor;
        _redact.Checked = redactor.Enabled && context.Settings.GetBool("diag.redact", true);
        _redact.Enabled = redactor.Enabled;
        _redact.CheckedChanged += (_, _) =>
        {
            Context.Settings.SetBool("diag.redact", _redact.Checked);
            Render();
        };
        _viewMarkdown.CheckedChanged += (_, _) => Render();
        _viewJson.CheckedChanged += (_, _) => Render();
        _start.MinimumSize = new Size(Px(120), Px(30));
        _copy.MinimumSize = new Size(Px(88), Px(30));
        _save.MinimumSize = new Size(Px(88), Px(30));
        _start.Click += (_, _) => Run(ToggleAsync);
        _copy.Click += (_, _) => CopyReport();
        _save.Click += (_, _) => SaveReport();
        _tick.Tick += (_, _) => ShowProgress();
        _duration.Width = Px(110);
        _intro.SetTone(CellTone.Dim);

        _start.TabIndex = 1;
        _duration.TabIndex = 0;
        _viewMarkdown.TabIndex = 2;
        _viewJson.TabIndex = 3;
        _redact.TabIndex = 4;
        _copy.TabIndex = 5;
        _save.TabIndex = 6;
        _text.TabIndex = 7;

        var pad = Px(12);
        var bar = PageLayout.Bar("diagnoseBar", pad, [_durationLabel, _duration, _start], [_viewMarkdown, _viewJson, _redact, _copy, _save]);
        _intro.Dock = DockStyle.Top;
        _intro.Height = Px(34);
        _intro.Padding = new Padding(pad, 0, pad, 0);
        _intro.TextAlign = ContentAlignment.MiddleLeft;

        Controls.Add(_text);
        Controls.Add(PageLayout.Footer("diagnoseFooter", pad, _state, _pii));
        Controls.Add(_intro);
        Controls.Add(bar);
        UpdateMonoFont();
        FillDurations(DefaultSeconds);
        ApplyLanguage();
    }

    internal ComboBox Duration => _duration;

    internal Button StartButton => _start;

    internal CheckBox RedactBox => _redact;

    internal RadioButton MarkdownView => _viewMarkdown;

    internal RadioButton JsonView => _viewJson;

    internal Button CopyButton => _copy;

    internal Button SaveButton => _save;

    internal TextBox TextView => _text;

    internal ReportDocument? Document => _document;

    internal bool IsRunning => _running is not null;

    /// <summary>The seconds chosen in the selector.</summary>
    internal int Seconds => _duration.SelectedIndex >= 0 ? DurationChoices[_duration.SelectedIndex] : DefaultSeconds;

    internal bool Redacting => _redact.Checked && Context.Services.Redactor.Enabled;

    public override void OnActivated()
    {
        if (_running is not null)
        {
            _tick.Start();
        }
    }

    public override void OnDeactivated() => _tick.Stop();

    public override void RefreshNow() => Run(ToggleAsync);

    public override void ApplyLanguage()
    {
        _durationLabel.Text = Strings.Get("gui.diag.seconds");
        FillDurations(Seconds);
        _start.Text = Strings.Get(_running is null ? "gui.diag.start" : "gui.diag.cancel");
        _redact.Text = Strings.Get("gui.diag.redact");
        _tips.SetToolTip(_redact, Strings.Get(Context.Services.Redactor.Enabled ? "gui.diag.redact.tip" : "gui.diag.redact.off"));
        _copy.Text = Strings.Get("gui.common.copy");
        _save.Text = Strings.Get("gui.common.save");
        _intro.Text = Strings.Get("gui.diag.intro");
        _text.AccessibleName = Strings.Get("gui.nav.diagnose");
        _duration.AccessibleName = Strings.Get("gui.diag.seconds");
        Render();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateMonoFont();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _running?.Cancel();
            _tick.Dispose();
            _tips.Dispose();
            _mono?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>The text of the report as shown and copied. Marks the document with what was really done: a switch the user turned off must not leave "redacted" on it.</summary>
    internal static string BuildText(ReportDocument document, bool json, bool redact, IRedactor redactor, Language language)
    {
        var shown = document with { Redacted = redact && redactor.Enabled };
        if (json)
        {
            var node = PcMieruJson.ToNode(shown);
            if (redact)
            {
                redactor.RedactJson(node);
            }

            return node?.ToJsonString(PcMieruJson.Indented) ?? string.Empty;
        }

        var markdown = ReportMarkdown.Render(shown, language);
        return redact ? redactor.RedactText(markdown) : markdown;
    }

    private async Task ToggleAsync()
    {
        if (_running is not null)
        {
            _running.Cancel();
            return;
        }

        using var cancel = new CancellationTokenSource();
        _running = cancel;
        _total = Seconds;
        _startedUtc = DateTime.UtcNow;
        _failed = false;
        _failure = null;
        SetBusy(true);
        try
        {
            _document = await Context.Services.Reports.BuildAsync(new ReportOptions(_total), cancel.Token).ConfigureAwait(true);
            _state.Text = Strings.Format("gui.diag.done", Fmt.Dict(("seconds", _document.DurationSec.ToString(CultureInfo.CurrentCulture))));
        }
        catch (OperationCanceledException)
        {
            _state.Text = Strings.Get("gui.diag.cancelled");
        }
        catch (Exception ex)
        {
            _failed = true;
            _failure = ex.Message;
            _state.Text = Strings.Format("gui.diag.failed", Fmt.Dict(("error", ex.Message)));
        }
        finally
        {
            _running = null;
            SetBusy(false);
        }

        Render();
    }

    private void SetBusy(bool busy)
    {
        _duration.Enabled = !busy;
        _start.Text = Strings.Get(busy ? "gui.diag.cancel" : "gui.diag.start");
        if (busy)
        {
            ShowProgress();
            _tick.Start();
        }
        else
        {
            _tick.Stop();
        }
    }

    private void ShowProgress()
    {
        if (_running is null)
        {
            return;
        }

        var elapsed = Math.Min((int)(DateTime.UtcNow - _startedUtc).TotalSeconds, _total);
        _state.Text = Strings.Format("gui.diag.running", Fmt.Dict(
            ("elapsed", elapsed.ToString(CultureInfo.CurrentCulture)),
            ("total", _total.ToString(CultureInfo.CurrentCulture))));
    }

    private void FillDurations(int select)
    {
        _duration.BeginUpdate();
        _duration.Items.Clear();
        foreach (var seconds in DurationChoices)
        {
            _duration.Items.Add(Strings.Format("gui.diag.sec", Fmt.Dict(("seconds", seconds.ToString(CultureInfo.CurrentCulture)))));
        }

        var index = Array.IndexOf(DurationChoices, select);
        _duration.SelectedIndex = index >= 0 ? index : Array.IndexOf(DurationChoices, DefaultSeconds);
        _duration.EndUpdate();
    }

    /// <summary>Shows the report in the chosen form, or the hint while there is none.</summary>
    private void Render()
    {
        var json = _viewJson.Checked;
        var has = _document is not null;
        _copy.Enabled = has;
        _save.Enabled = has;
        _text.WordWrap = !json;
        _text.ScrollBars = json ? ScrollBars.Both : ScrollBars.Vertical;
        if (_document is { } document)
        {
            _text.Text = BuildText(document, json, Redacting, Context.Services.Redactor, Strings.Current).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
            _pii.SetTone(Redacting ? CellTone.Dim : CellTone.Accent);
            _pii.Text = Strings.Get(Redacting ? "gui.diag.pii.on" : "gui.diag.pii.off");
        }
        else
        {
            _text.Text = _failed ? Strings.Format("gui.diag.failed", Fmt.Dict(("error", _failure ?? string.Empty))) : Strings.Get("gui.diag.empty");
            _pii.Text = string.Empty;
        }
    }

    private void UpdateMonoFont()
    {
        var previous = _mono;
        _mono = new Font("Consolas", Font.SizeInPoints, FontStyle.Regular, GraphicsUnit.Point);
        _text.Font = _mono;
        previous?.Dispose();
    }

    private string CurrentText() =>
        _document is { } document ? BuildText(document, _viewJson.Checked, Redacting, Context.Services.Redactor, Strings.Current) : string.Empty;

    private void CopyReport()
    {
        if (_document is null)
        {
            return;
        }

        Context.CopyText(CurrentText(), Redacting ? "gui.diag.copied" : "gui.diag.copied.plain");
    }

    private void SaveReport()
    {
        if (_document is null)
        {
            return;
        }

        var ext = _viewJson.Checked ? "json" : "md";
        using var dialog = new SaveFileDialog
        {
            Title = Strings.Get("gui.diag.save.title"),
            Filter = Strings.Get(_viewJson.Checked ? "gui.diag.filter.json" : "gui.diag.filter.md"),
            FileName = "pcmieru-report-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + "." + ext,
            DefaultExt = ext,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
        {
            SaveTo(dialog.FileName);
        }
    }

    /// <summary>Writes the report as shown (same form, same redaction) as UTF-8. A write that fails goes to the status bar.</summary>
    internal bool SaveTo(string path)
    {
        try
        {
            File.WriteAllText(path, CurrentText(), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Context.Report("gui.status.failed", ("error", ex.Message));
            return false;
        }

        Context.Report("gui.diag.saved", ("path", path));
        return true;
    }
}

/// <summary>Strings of the Report page.</summary>
internal static class DiagnoseStrings
{
    public static readonly StringEntry[] All =
    [
        new("gui.diag.intro", "PC の様子をしばらく見て、困りごとの相談に貼り付けられるレポートを作ります。このアプリから外へ送ることはありません。", "Watches the PC for a while and writes a report you can paste when asking for help. This app never sends it anywhere."),
        new("gui.diag.seconds", "観察する長さ", "Watch for"),
        new("gui.diag.sec", "{seconds} 秒", "{seconds} s"),
        new("gui.diag.start", "レポートを作る", "Generate report"),
        new("gui.diag.cancel", "中止", "Cancel"),
        new("gui.diag.running", "観察しています... {elapsed} / {total} 秒", "Watching... {elapsed} / {total} s"),
        new("gui.diag.done", "{seconds} 秒間の観察が終わりました", "Finished watching for {seconds} s"),
        new("gui.diag.cancelled", "中止しました", "Cancelled"),
        new("gui.diag.failed", "レポートを作れませんでした: {error}", "Could not make the report: {error}"),
        new("gui.diag.empty", "「レポートを作る」を押すと、ここに出ます。", "Press Generate report and it will appear here."),
        new("gui.diag.redact", "個人情報を伏せる", "Hide personal info"),
        new("gui.diag.redact.tip", "シリアル番号・MAC アドレス・IP アドレス・PC 名・ユーザー名・コマンドラインを伏せます。表示も、コピーも、保存も同じ内容になります", "Hides serial numbers, MAC and IP addresses, the PC name, the user name and command lines. What you see, copy and save is the same"),
        new("gui.diag.redact.off", "この起動では、個人情報を伏せる機能が切ってあります", "Hiding personal info is turned off for this run"),
        new("gui.diag.pii.on", "個人情報は伏せています", "Personal info is hidden"),
        new("gui.diag.pii.off", "個人情報は伏せていません。貼り付ける前に中身を確かめてください", "Personal info is not hidden. Check it before you paste"),
        new("gui.diag.copied", "レポートをコピーしました（個人情報は伏せています）", "Report copied (personal info is hidden)"),
        new("gui.diag.copied.plain", "レポートをコピーしました（個人情報は伏せていません）", "Report copied (personal info is not hidden)"),
        new("gui.diag.saved", "保存しました: {path}", "Saved: {path}"),
        new("gui.diag.save.title", "レポートを保存", "Save the report"),
        new("gui.diag.filter.md", "Markdown (*.md)|*.md|すべてのファイル (*.*)|*.*", "Markdown (*.md)|*.md|All files (*.*)|*.*"),
        new("gui.diag.filter.json", "JSON (*.json)|*.json|すべてのファイル (*.*)|*.*", "JSON (*.json)|*.json|All files (*.*)|*.*"),
    ];
}
