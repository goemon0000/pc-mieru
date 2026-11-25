using System.Globalization;
using PcMieru.Wire.Gate;

namespace PcMieru.Party;

/// <summary>
/// The few texts of the shell of Parts Life (the window itself has none yet). Plain switch expressions, no resource table: the texts that exist before the first window are all here.
/// The refusals say what is wrong and what to do in one or two sentences and name no file and no folder, like the start gate window of PCMieru.exe (src/PcMieru.Gui/Update/StartGateScreen.cs).
/// </summary>
internal static class PartyStrings
{
    /// <summary>--lang wins; else the UI language of the OS (Japanese or, for everything else, English).</summary>
    public static PartyLanguage Resolve(PartyLanguage? chosen) =>
        chosen ?? (string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "ja", StringComparison.OrdinalIgnoreCase) ? PartyLanguage.Ja : PartyLanguage.En);

    public static string Title(PartyLanguage language) => language == PartyLanguage.Ja ? "パーツぐらし" : "Parts Life";

    public static string AlreadyRunning(PartyLanguage language) =>
        language == PartyLanguage.Ja ? "もう一方のパーツぐらしが動いています。" : "Another Parts Life is already running.";

    /// <summary>The text of a start that the gate refused: what is wrong in the language of the window, then the fixed English sentence of the gate (what a console program would say).</summary>
    public static string GateRefused(PartyLanguage language, GateEntry entry)
    {
        var text = (language, entry.Outcome) switch
        {
            (PartyLanguage.Ja, GateOutcome.Busy) => "PCミエルのファイルが使用中です。しばらく待ってから、もう一度試してください。",
            (PartyLanguage.En, GateOutcome.Busy) => "PC Mieru's files are in use right now. Wait a moment and try again.",
            (PartyLanguage.Ja, GateOutcome.RecoveryPending or GateOutcome.RecoveryFailed) =>
                "前回の更新が途中で止まったため、パーツぐらしを開けません。PCミエルを開いて、画面の案内に従ってください。",
            (PartyLanguage.En, GateOutcome.RecoveryPending or GateOutcome.RecoveryFailed) =>
                "The last update did not finish, so Parts Life cannot open. Open PC Mieru and follow what it says.",
            (PartyLanguage.Ja, _) =>
                "起動に必要なファイルを確認できないため、パーツぐらしを開けません。ダウンロードした ZIP をもう一度新しいフォルダーに展開し、そこから開いてください。",
            _ =>
                "Parts Life cannot open because a file it needs could not be checked. Unzip the downloaded ZIP file into a new folder and open it from there.",
        };
        return text + Environment.NewLine + Environment.NewLine + entry.Reason;
    }
}
