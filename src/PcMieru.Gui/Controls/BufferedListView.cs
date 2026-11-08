namespace PcMieru.Gui;

/// <summary>A details-view ListView that does not flicker when it is redrawn every second.</summary>
internal class BufferedListView : ListView
{
    public BufferedListView()
    {
        DoubleBuffered = true;
        View = View.Details;
        FullRowSelect = true;
        HideSelection = false;
        MultiSelect = false;
        BorderStyle = BorderStyle.FixedSingle;
        Dock = DockStyle.Fill;
    }
}
