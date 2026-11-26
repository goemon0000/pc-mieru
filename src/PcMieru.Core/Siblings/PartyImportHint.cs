using System.Text.Json.Serialization;
using PcMieru.Wire;

namespace PcMieru.Core.Siblings;

/// <summary>What <see cref="PartyImportHint.Check"/> found.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<PartyImportHintState>))]
public enum PartyImportHintState
{
    /// <summary>Nothing to say: a save of the new window is already there, or no old save is.</summary>
    None,

    /// <summary>The old app's save (<c>%APPDATA%\PartsLife\state.json</c>) is there and the new window has none yet.</summary>
    FromPartsLife,

    /// <summary>Only the old app's former name's save (<c>%APPDATA%\PartsParty\state.json</c>) is there and the new window has none yet.</summary>
    FromPartsParty,
}

/// <summary>
/// DESIGN-C 4.1 items 1-2, the main window's side: it only NOTICES that old progress is waiting, so the window can send the user to PCMieru.Party.exe, whose first start does the
/// import, its three choices and every check. Nothing here imports, parses or opens anything: it asks whether three files exist under the roaming folder. The roaming folder is
/// injected (a test or a smoke run passes a temporary one; only <see cref="RealRoamingRoot"/> names the real one, and only a real start calls it). Any failure answers
/// <see cref="PartyImportHintState.None"/>: it is a hint, and the Party window checks again for real.
/// </summary>
public static class PartyImportHint
{
    private const string SaveFile = "state.json";

    /// <summary>The roaming application-data folder of this user (the real one). Tests and smoke runs never call this.</summary>
    public static string RealRoamingRoot() => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>
    /// A save of the new window (<c>PCMieru\party\state.json</c>) means "already there, nothing to say"; otherwise the old app's save, then the former name's. Existence only: no file is opened.
    /// </summary>
    public static PartyImportHintState Check(string roamingRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(roamingRoot))
            {
                return PartyImportHintState.None;
            }

            if (File.Exists(Path.Combine(roamingRoot, "PCMieru", "party", SaveFile)))
            {
                return PartyImportHintState.None;
            }

            if (File.Exists(Path.Combine(roamingRoot, "PartsLife", SaveFile)))
            {
                return PartyImportHintState.FromPartsLife;
            }

            return File.Exists(Path.Combine(roamingRoot, "PartsParty", SaveFile)) ? PartyImportHintState.FromPartsParty : PartyImportHintState.None;
        }
        catch (Exception)
        {
            return PartyImportHintState.None;
        }
    }
}
