using System.Windows;
using System.Windows.Controls;

namespace PcMieru.Party;

/// <summary>
/// The window of Parts Life. Empty for now: the board, the story and the settings come with the later steps of DESIGN-C (4 and on). It exists so that the start, the gate and the single
/// resident can be run and shipped as they will be.
/// </summary>
internal sealed class PartyWindow : Window
{
    public PartyWindow(PartyLanguage language)
    {
        Name = "PartyWindow";
        Title = PartyStrings.Title(language);
        Width = 900;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // An empty root panel: a Window with no content at all never raises ContentRendered (seen: the idle priority is reached, the event is not), and the smoke run waits for it.
        Content = new Grid();
        ContentRendered += (_, _) => Rendered = true;
    }

    /// <summary>The window was actually drawn once (not only created): what a smoke run needs to say "ok".</summary>
    public bool Rendered { get; private set; }
}
