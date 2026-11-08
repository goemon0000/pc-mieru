using System.Globalization;
using System.Text;
using PcMieru.Core.Reporting;

namespace PcMieru.Gui;

/// <summary>
/// The diagnostic report: watch the PC for a chosen number of seconds, then show a report that can be copied or saved. It opens
/// as a formatted preview; the Markdown source and the JSON are one click away. The preview is drawn from the same Markdown that
/// Copy and Save give, so the three never differ. Personal data is hidden by default and the page says which it is. The app never sends the report anywhere:
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
    private readonly ToggleButton _viewPreview = new() { Name = "diagnoseViewPreview" };
    private readonly ToggleButton _viewMarkdown = new() { Name = "diagnoseViewMarkdown", Text = "Markdown" };
    private readonly ToggleButton _viewJson = new() { Name = "diagnoseViewJson", Text = "JSON" };
    private readonly FlowLayoutPanel _views = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Name = "diagnoseViews" };
    private readonly CheckBox _redact = new() { AutoSize = true, Name = "diagnoseRedact", Checked = true };
    private readonly Button _copy = new() { AutoSize = true, Enabled = false, Name = "diagnoseCopy" };
    private readonly Button _save = new() { AutoSize = true, Enabled = false, Name = "diagnoseSave" };
    private readonly ToneLabel _intro = new() { AutoSize = false, Name = "diagnoseIntro" };
    private readonly Label _state = new() { AutoSize = true, Name = "diagnoseState", Margin = new Padding(0, 0, 16, 0) };
    private readonly ToneLabel _pii = new() { AutoSize = true, Name = "diagnosePii" };
    private readonly TextBox _text = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Name = "diagnoseText", BorderStyle = BorderStyle.None, Visible = false };
    private readonly ReportPreviewBox _preview = new() { Dock = DockStyle.Fill, Name = "diagnosePreview" };
    private readonly ToolTip _tips = new() { ShowAlways = true };
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };

    private Font? _mono;
    private DiagnoseView _view = DiagnoseView.Preview;
    private CancellationTokenSource? _running;
    private ReportDocument? _document;
    private DateTime _startedUtc;
    private int _total;
    private bool _failed;

    /// <summary>How the last run ended. The line under the text is built from it, so it follows a change of language.</summary>
    private Outcome _outcome;
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
        if (Enum.TryParse<DiagnoseView>(context.Settings.GetValue("diag.view"), out var saved) && Enum.IsDefined(saved))
        {
            _view = saved;
        }

        _viewPreview.Click += (_, _) => SetView(DiagnoseView.Preview);
        _viewMarkdown.Click += (_, _) => SetView(DiagnoseView.Markdown);
        _viewJson.Click += (_, _) => SetView(DiagnoseView.Json);
        _start.MinimumSize = new Size(Px(120), Px(30));
        _copy.MinimumSize = new Size(Px(88), Px(30));
        _save.MinimumSize = new Size(Px(88), Px(30));
        _start.Click += (_, _) => Run(ToggleAsync);
        _copy.Click += (_, _) => CopyReport();
        _save.Click += (_, _) => SaveReport();
        _tick.Tick += (_, _) => ShowProgress();
        _duration.Width = Px(110);
        _intro.SetTone(CellTone.Dim);

        var pad = Px(12);
        foreach (var toggle in new[] { _viewPreview, _viewMarkdown, _viewJson })
        {
            toggle.Margin = new Padding(0, 0, Px(4), 0);
            toggle.Padding = new Padding(Px(6), 0, Px(6), 0);
            toggle.MinimumSize = new Size(0, Px(30));
            _views.Controls.Add(toggle);
        }

        _views.Margin = new Padding(0);
        _viewPreview.TabIndex = 0;
        _viewMarkdown.TabIndex = 1;
        _viewJson.TabIndex = 2;
        _start.TabIndex = 1;
        _duration.TabIndex = 0;
        _views.TabIndex = 2;
        _redact.TabIndex = 3;
        _copy.TabIndex = 4;
        _save.TabIndex = 5;
        _text.TabIndex = 6;
        _preview.TabIndex = 7;

        var bar = PageLayout.Bar("diagnoseBar", pad, [_durationLabel, _duration, _start], [_views, _redact, _copy, _save]);
        _intro.Dock = DockStyle.Top;
        _intro.Height = Px(34);
        _intro.Padding = new Padding(pad, 0, pad, 0);
        _intro.TextAlign = ContentAlignment.MiddleLeft;

        Controls.Add(_text);
        Controls.Add(_preview);
        Controls.Add(PageLayout.Footer("diagnoseFooter", pad, _state, _pii));
        Controls.Add(_intro);
        Controls.Add(bar);
        UpdateMonoFont();
        ShowView();
        FillDurations(DefaultSeconds);
        ApplyLanguage();
    }

    internal ComboBox Duration => _duration;

    internal Button StartButton => _start;

    internal CheckBox RedactBox => _redact;

    internal ToggleButton PreviewView => _viewPreview;

    internal ToggleButton MarkdownView => _viewMarkdown;

    internal ToggleButton JsonView => _viewJson;

    internal Button CopyButton => _copy;

    internal Button SaveButton => _save;

    /// <summary>The text box that is showing the report now: the preview, or the plain text of the Markdown or JSON view.</summary>
    internal TextBoxBase TextView => _view == DiagnoseView.Preview ? _preview : _text;

    internal DiagnoseView View => _view;

    /// <summary>What Copy and Save give now: Markdown in the preview and the Markdown view, JSON in the JSON view.</summary>
    internal string ExportText => CurrentText();

    internal ReportDocument? Document => _document;

    /// <summary>The line under the text (progress or how the run ended).</summary>
    internal string StateText => _state.Text;

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
        if (_running is null)
        {
            ShowOutcome();
        }
        else
        {
            ShowProgress();
        }

        _redact.Text = Strings.Get("gui.diag.redact");
        _tips.SetToolTip(_redact, Strings.Get(Context.Services.Redactor.Enabled ? "gui.diag.redact.tip" : "gui.diag.redact.off"));
        _copy.Text = Strings.Get("gui.common.copy");
        _save.Text = Strings.Get("gui.common.save");
        _intro.Text = Strings.Get("gui.diag.intro");
        _text.AccessibleName = Strings.Get("gui.nav.diagnose");
        _preview.AccessibleName = Strings.Get("gui.nav.diagnose");
        _viewPreview.Text = Strings.Get("gui.diag.view.preview");
        _tips.SetToolTip(_viewPreview, Strings.Get("gui.diag.view.preview.tip"));
        _tips.SetToolTip(_viewMarkdown, Strings.Get("gui.diag.view.markdown.tip"));
        _tips.SetToolTip(_viewJson, Strings.Get("gui.diag.view.json.tip"));
        _duration.AccessibleName = Strings.Get("gui.diag.seconds");
        Render();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateMonoFont();
    }

    public override void SaveState() => Context.Settings.SetValue("diag.view", _view.ToString());

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

    /// <summary>The text of the report as shown and copied. The report builder renders it and marks the document with what was really done: a switch the user turned off must not leave "redacted" on it.</summary>
    internal static string BuildText(ReportDocument document, bool json, bool redact, IReportBuilder reports, Language language) =>
        json
            ? reports.RenderJson(document, redact).ToJsonString(PcMieruJson.Indented)
            : reports.RenderMarkdown(document, language, redact);

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
        _outcome = Outcome.None;
        SetBusy(true);
        try
        {
            _document = await Context.Services.Reports.BuildAsync(new ReportOptions(_total), cancel.Token).ConfigureAwait(true);
            _outcome = Outcome.Done;
        }
        catch (OperationCanceledException)
        {
            _outcome = Outcome.Cancelled;
        }
        catch (Exception ex)
        {
            _failed = true;
            _failure = ex.Message;
            _outcome = Outcome.Failed;
        }
        finally
        {
            _running = null;
            SetBusy(false);
        }

        ShowOutcome();
        Render();
    }

    private void ShowOutcome() => _state.Text = _outcome switch
    {
        Outcome.Done when _document is not null => Strings.Format("gui.diag.done", Fmt.Dict(("seconds", _document.DurationSec.ToString(CultureInfo.CurrentCulture)))),
        Outcome.Cancelled => Strings.Get("gui.diag.cancelled"),
        Outcome.Failed => Strings.Format("gui.diag.failed", Fmt.Dict(("error", _failure ?? string.Empty))),
        _ => string.Empty,
    };

    private enum Outcome
    {
        None,
        Done,
        Cancelled,
        Failed,
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

    private void SetView(DiagnoseView view)
    {
        if (_view == view)
        {
            ShowView();
            return;
        }

        _view = view;
        ShowView();
        Render();
    }

    /// <summary>Which of the two boxes is on screen, and which switch is pressed.</summary>
    private void ShowView()
    {
        _viewPreview.Checked = _view == DiagnoseView.Preview;
        _viewMarkdown.Checked = _view == DiagnoseView.Markdown;
        _viewJson.Checked = _view == DiagnoseView.Json;
        _preview.Visible = _view == DiagnoseView.Preview;
        _text.Visible = _view != DiagnoseView.Preview;
    }

    /// <summary>Shows the report in the chosen form, or the hint while there is none.</summary>
    private void Render()
    {
        var json = _view == DiagnoseView.Json;
        var has = _document is not null;
        _copy.Enabled = has;
        _save.Enabled = has;
        var format = json ? "JSON" : "Markdown";
        _tips.SetToolTip(_copy, Strings.Format("gui.diag.copy.tip", Fmt.Dict(("format", format))));
        _tips.SetToolTip(_save, Strings.Format("gui.diag.save.tip", Fmt.Dict(("format", format))));
        _text.WordWrap = !json;
        _text.ScrollBars = json ? ScrollBars.Both : ScrollBars.Vertical;
        if (_document is { } document)
        {
            // The preview is drawn from the Markdown that Copy and Save give, so what is shown is what is copied.
            var text = BuildText(document, json, Redacting, Context.Services.Reports, Strings.Current);
            if (_view == DiagnoseView.Preview)
            {
                _preview.ShowMarkdown(text);
            }
            else
            {
                _text.Text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
            }

            _pii.SetTone(Redacting ? CellTone.Dim : CellTone.Accent);
            _pii.Text = Strings.Get(Redacting ? "gui.diag.pii.on" : "gui.diag.pii.off");
        }
        else
        {
            var hint = _failed ? Strings.Format("gui.diag.failed", Fmt.Dict(("error", _failure ?? string.Empty))) : Strings.Get("gui.diag.empty");
            if (_view == DiagnoseView.Preview)
            {
                _preview.ShowPlain(hint);
            }
            else
            {
                _text.Text = hint;
            }

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
        _document is { } document ? BuildText(document, _view == DiagnoseView.Json, Redacting, Context.Services.Reports, Strings.Current) : string.Empty;

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

        var json = _view == DiagnoseView.Json;
        var ext = json ? "json" : "md";
        using var dialog = new SaveFileDialog
        {
            Title = Strings.Get("gui.diag.save.title"),
            Filter = Strings.Get(json ? "gui.diag.filter.json" : "gui.diag.filter.md"),
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

/// <summary>The three ways to look at the report. Preview is the default; Copy and Save give Markdown in it.</summary>
internal enum DiagnoseView
{
    Preview,
    Markdown,
    Json,
}

/// <summary>Strings of the Report page.</summary>
internal static class DiagnoseStrings
{
    public static readonly StringEntry[] All =
    [
        new("gui.diag.view.preview", "プレビュー", "Preview"),
        new("gui.diag.view.preview.tip", "読みやすく整えた表示です。コピーと保存は Markdown の文章になります", "A readable view. Copy and Save use the Markdown text"),
        new("gui.diag.view.markdown.tip", "コピー・保存される元の文章です。相談に貼り付けるときはこれです", "The source text that is copied and saved. This is what to paste when asking for help"),
        new("gui.diag.view.json.tip", "プログラムが読むための形式です。人に見せるなら Markdown が向いています", "A form for programs to read. Markdown suits showing it to a person better"),
        new("gui.diag.copy.tip", "{format} の文章をクリップボードへコピーします", "Copies the {format} text to the clipboard"),
        new("gui.diag.save.tip", "{format} の文章をファイルに保存します", "Saves the {format} text to a file"),
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
