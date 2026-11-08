using System.Globalization;
using PcMieru.Core.Reporting;

namespace PcMieru.Gui;

/// <summary>"Why is it slow": watch for a few seconds, list what the rules found, copy the redacted report.</summary>
internal sealed class DiagnosePage : PageBase
{
    private readonly NumericUpDown _seconds = new() { Minimum = 1, Maximum = 30, Value = 5, Name = "diagnoseSeconds", Width = 70 };
    private readonly Label _secondsLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button _start = new() { AutoSize = true, Name = "diagnoseStart" };
    private readonly Button _copy = new() { AutoSize = true, Enabled = false, Name = "diagnoseCopy" };
    private readonly Label _state = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ListBox _results = new() { Dock = DockStyle.Fill, IntegralHeight = false, Name = "diagnoseResults" };
    private CancellationTokenSource? _running;
    private ReportDocument? _document;

    public DiagnosePage(GuiContext context)
        : base(context, "diagnose", "gui.nav.diagnose")
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(Px(8)), WrapContents = true };
        bar.Controls.AddRange([_secondsLabel, _seconds, _start, _copy, _state]);
        _start.Click += (_, _) => Run(ToggleAsync);
        _copy.Click += (_, _) => CopyReport();
        Controls.Add(_results);
        Controls.Add(bar);
        ApplyLanguage();
    }

    public override void OnDeactivated() => _running?.Cancel();

    public override void ApplyLanguage()
    {
        _secondsLabel.Text = Strings.Get("gui.diag.seconds");
        _start.Text = Strings.Get(_running is null ? "gui.diag.start" : "gui.diag.cancel");
        _copy.Text = Strings.Get("gui.diag.copy");
        _results.AccessibleName = Strings.Get("gui.nav.diagnose");
        ShowResults();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _running?.Cancel();
        }

        base.Dispose(disposing);
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
        _start.Text = Strings.Get("gui.diag.cancel");
        _state.Text = Strings.Get("gui.diag.running");
        _seconds.Enabled = false;
        try
        {
            _document = await Context.Services.Reports.BuildAsync(new ReportOptions((int)_seconds.Value), cancel.Token).ConfigureAwait(true);
            _state.Text = Strings.Format("gui.diag.done", new Dictionary<string, string>
            {
                ["seconds"] = _document.DurationSec.ToString(CultureInfo.CurrentCulture),
            });
        }
        catch (OperationCanceledException)
        {
            _state.Text = Strings.Get("gui.diag.cancelled");
        }
        finally
        {
            _running = null;
            _start.Text = Strings.Get("gui.diag.start");
            _seconds.Enabled = true;
        }

        _copy.Enabled = _document is not null;
        ShowResults();
    }

    private void ShowResults()
    {
        _results.BeginUpdate();
        _results.Items.Clear();
        if (_document is null)
        {
            _results.Items.Add(Strings.Get("gui.diag.hint"));
        }
        else if (_document.Summary.Observations.Count == 0)
        {
            _results.Items.Add(Strings.Get("gui.perf.none"));
        }
        else
        {
            foreach (var o in _document.Summary.Observations)
            {
                _results.Items.Add(Strings.Get("gui.perf.certainty." + o.Certainty.ToString().ToLowerInvariant()) + "  " + Strings.Render(o));
            }
        }

        _results.EndUpdate();
    }

    private void CopyReport()
    {
        if (_document is null)
        {
            return;
        }

        var text = Context.Services.Redactor.RedactText(ReportMarkdown.Render(_document, Strings.Current));
        Clipboard.SetText(text);
        Context.Report(Strings.Get("gui.diag.copied"));
    }
}
