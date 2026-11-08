using System.Globalization;

namespace PcMieru.Gui;

/// <summary>Whole-PC numbers as a plain two-column list, plus what the observation rules make of the last minute.</summary>
internal sealed class PerformancePage : PageBase
{
    private readonly BufferedListView _metrics = new() { Name = "metricList" };
    private readonly ListBox _observations = new() { Name = "observationList", Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Label _observationsTitle = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 4) };
    private readonly Panel _bottom = new() { Dock = DockStyle.Bottom };
    private List<string> _rowKeys = [];

    public PerformancePage(GuiContext context)
        : base(context, "performance", "gui.nav.performance")
    {
        _metrics.Columns.Add(new ColumnHeader { Width = Px(220) });
        _metrics.Columns.Add(new ColumnHeader { Width = Px(520) });
        _bottom.Height = Px(190);
        _bottom.Controls.Add(_observations);
        _bottom.Controls.Add(_observationsTitle);
        Controls.Add(_metrics);
        Controls.Add(_bottom);
        context.Pump.Sampled += OnSampled;
        ApplyLanguage();
    }

    public override void OnActivated()
    {
        if (Context.Pump.Latest is { } latest)
        {
            OnSampled(latest);
        }
    }

    public override void ApplyLanguage()
    {
        _metrics.Columns[0].Text = Strings.Get("gui.perf.col.metric");
        _metrics.Columns[1].Text = Strings.Get("gui.perf.col.value");
        _observationsTitle.Text = Strings.Get("gui.perf.observations");
        _metrics.AccessibleName = Strings.Get("gui.nav.performance");
        _observations.AccessibleName = Strings.Get("gui.perf.observations");
        _rowKeys = [];
        if (Context.Pump.Latest is { } latest)
        {
            OnSampled(latest);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Context.Pump.Sampled -= OnSampled;
        }

        base.Dispose(disposing);
    }

    internal static List<(string Key, string Label, string Value)> BuildRows(Snapshot snapshot)
    {
        var rows = new List<(string, string, string)>();
        var s = snapshot.System;
        rows.Add(("cpu", Strings.Get("gui.perf.cpu"), Fmt.Percent(s.Cpu.TotalPercent) + "   " + Fmt.Mhz(s.Cpu.CurrentMhz)));

        var used = s.Memory.TotalPhysicalBytes - s.Memory.AvailablePhysicalBytes;
        rows.Add(("memory", Strings.Get("gui.perf.memory"), Fmt.Bytes(used) + " / " + Fmt.Bytes(s.Memory.TotalPhysicalBytes)));
        if (s.Memory.CommitTotalBytes.IsOk || s.Memory.CommitLimitBytes.IsOk)
        {
            rows.Add(("commit", Strings.Get("gui.perf.commit"), Fmt.Bytes(s.Memory.CommitTotalBytes) + " / " + Fmt.Bytes(s.Memory.CommitLimitBytes)));
        }

        foreach (var gpu in s.Gpus)
        {
            var memory = gpu.DedicatedUsedBytes.IsOk ? "   " + Fmt.Bytes(gpu.DedicatedUsedBytes) + " / " + Fmt.Bytes(gpu.DedicatedTotalBytes) : string.Empty;
            var temperature = gpu.TemperatureC.TryGet(out var t) ? "   " + t.ToString("0", CultureInfo.CurrentCulture) + " C" : string.Empty;
            rows.Add(("gpu:" + gpu.Id, gpu.Name, Fmt.Percent(gpu.UtilizationPercent) + memory + temperature));
        }

        foreach (var disk in s.Disks)
        {
            rows.Add(("disk:" + disk.Id, disk.Name, Fmt.Percent(disk.ActivePercent) + "   " + Strings.Get("gui.perf.read") + " " + Fmt.Rate(disk.ReadBytesPerSec) + "   " + Strings.Get("gui.perf.write") + " " + Fmt.Rate(disk.WriteBytesPerSec)));
        }

        foreach (var nic in s.Nics.Where(n => n.IsUp))
        {
            rows.Add(("nic:" + nic.Id, nic.Name, Strings.Get("gui.perf.send") + " " + Fmt.Rate(nic.SendBytesPerSec) + "   " + Strings.Get("gui.perf.receive") + " " + Fmt.Rate(nic.ReceiveBytesPerSec)));
        }

        rows.Add(("uptime", Strings.Get("gui.perf.uptime"), Fmt.Uptime(s.UptimeSec)));
        return rows;
    }

    private void OnSampled(Snapshot snapshot)
    {
        var rows = BuildRows(snapshot);
        var keys = rows.Select(r => r.Key).ToList();
        if (!keys.SequenceEqual(_rowKeys))
        {
            _metrics.BeginUpdate();
            _metrics.Items.Clear();
            foreach (var (key, label, value) in rows)
            {
                _metrics.Items.Add(new ListViewItem([label, value]) { Name = key });
            }

            _metrics.EndUpdate();
            _rowKeys = keys;
        }
        else
        {
            for (var i = 0; i < rows.Count; i++)
            {
                _metrics.Items[i].SubItems[1].Text = rows[i].Value;
            }
        }

        ShowObservations(snapshot);
    }

    private void ShowObservations(Snapshot snapshot)
    {
        var found = Context.Services.Observations.Evaluate(new ObservationInput(snapshot, Context.Pump.History));
        var lines = found.Select(o =>
            Strings.Get("gui.perf.certainty." + o.Certainty.ToString().ToLowerInvariant()) + "  " + Strings.Render(o)).ToList();
        if (lines.Count == 0)
        {
            lines.Add(Strings.Get("gui.perf.none"));
        }

        if (_observations.Items.Count == lines.Count && lines.Select((l, i) => _observations.Items[i].ToString() == l).All(same => same))
        {
            return;
        }

        _observations.BeginUpdate();
        _observations.Items.Clear();
        _observations.Items.AddRange([.. lines]);
        _observations.EndUpdate();
    }
}
