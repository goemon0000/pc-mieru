namespace PcMieru.Gui;

/// <summary>
/// The network meter on the Processes page: the button that starts and stops it, and the redraw when its state or the
/// network unit changes. The meter reports from any thread; everything here moves the call to the UI thread first, and
/// starting it (which waits for the UAC prompt) never runs on the UI thread.
/// </summary>
internal sealed partial class ProcessesPage
{
    /// <summary>The button as the page shows it now (for the smoke run and tests).</summary>
    internal Button MeterButton => _meter;

    /// <summary>Puts the button on the meter's current state: text, tip, shield, whether it is there and can be pressed.</summary>
    internal void ApplyMeterState()
    {
        var state = Context.NetMeter.State;
        var button = NetMeterUi.Button(state.Status);
        _meter.Text = Strings.Get(button.TextKey);
        _meter.Enabled = button.Enabled;
        _meter.Visible = button.Visible;
        _meter.Image = button.Shield ? Shield() : null;
        _tips.SetToolTip(_meter, NetMeterUi.ButtonTip(state));
    }

    /// <summary>
    /// The shield the UAC prompt carries, on the button. A flat-styled button cannot take the system's own shield flag
    /// (the theme paints the button itself), so the stock icon is put on it as an image.
    /// </summary>
    private Bitmap? Shield()
    {
        if (_shield is not null)
        {
            return _shield;
        }

        try
        {
            using var icon = SystemIcons.GetStockIcon(StockIconId.Shield, Px(16));
            _shield = icon.ToBitmap();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // No icon from this Windows: the text still says what the button does.
        }

        return _shield;
    }

    private void OnMeterClick()
    {
        switch (Context.NetMeter.State.Status)
        {
            case NetMeterStatus.Running:
                Run(Context.StopNetMeterAsync);
                break;
            case NetMeterStatus.Off or NetMeterStatus.Declined or NetMeterStatus.Failed:
                Run(StartMeterAsync);
                break;
        }
    }

    private async Task StartMeterAsync()
    {
        var result = await Context.StartNetMeterAsync().ConfigureAwait(true);
        ApplyMeterState();
        switch (result.Status)
        {
            case NetMeterStatus.Declined:
                Context.ReportKey("gui.proc.net.tip.declined");
                break;
            case NetMeterStatus.Failed:
                Context.Report(NetMeterUi.ButtonTip(result).Replace(Environment.NewLine, " - ", StringComparison.Ordinal));
                break;
        }
    }

    /// <summary>The meter changed state, on whatever thread it was. The button and the cells follow on the UI thread.</summary>
    private void OnMeterStateChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(AfterMeterStateChanged);
        }
        catch (InvalidOperationException)
        {
            // The window is closing; there is nothing left to update.
        }
    }

    private void AfterMeterStateChanged()
    {
        if (IsDisposed)
        {
            return;
        }

        ApplyMeterState();
        _list.Invalidate();
        if (Visible && Context.Pump.Latest is not null && !Context.Pump.Paused)
        {
            // New values (or the dash and its reason) show now, not at the next sample.
            _ = Context.Pump.TickAsync();
        }
    }

    /// <summary>The unit changed: every cell and the detail pane write the rates again, at once.</summary>
    private void OnNetworkUnitChanged()
    {
        _pane.NetworkUnit = Context.Settings.NetworkUnit;
        if (_selectedKey is { } key && _model.Find(key) is { } info)
        {
            _pane.UpdateNumbers(info);
        }

        _list.Invalidate();
    }
}
