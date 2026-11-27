using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using PcMieru.Wire;

namespace PcMieru.Core.Siblings;

/// <summary>What a receiver does on one of the two messages. Fixed on purpose: a message carries no argument and no path, so the action never depends on who sent it.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<MieruMessageAction>))]
public enum MieruMessageAction
{
    /// <summary>Not a message this receiver acts on.</summary>
    None,

    /// <summary>The main window: show the checks page and bring the window to the front.</summary>
    ShowChecks,

    /// <summary>The Parts Life window: read the language key of gui.ini again and follow it.</summary>
    ReloadLanguage,
}

/// <summary>
/// The only two window messages between PCMieru.exe and PCMieru.Party.exe (DESIGN-C 2.2). No pipe and no standing channel. They are registered by name
/// (RegisterWindowMessage), posted to the top-level window of a sibling (<see cref="SiblingWindows.Notify"/>) with wParam and lParam both 0, and a receiver
/// ignores both parameters: the receiver does not check the sender, so what it does on the message is one fixed action (<see cref="ForMainWindow"/>, <see cref="ForParty"/>).
/// </summary>
public static partial class MieruMessages
{
    public const string OpenChecksName = "PCMieru.OpenChecks.v1";

    public const string LanguageChangedName = "PCMieru.LanguageChanged.v1";

    /// <summary>The id of <see cref="OpenChecksName"/> in this session (0 when Windows could not register it: nothing is sent and nothing matches).</summary>
    public static uint OpenChecks { get; } = Register(OpenChecksName);

    /// <summary>The id of <see cref="LanguageChangedName"/> in this session (0 when Windows could not register it).</summary>
    public static uint LanguageChanged { get; } = Register(LanguageChangedName);

    /// <summary>What the main window does on <paramref name="message"/>: only the open-checks message does anything. <paramref name="openChecks"/> is its registered id.</summary>
    public static MieruMessageAction ForMainWindow(uint message, uint openChecks) =>
        message != 0 && message == openChecks ? MieruMessageAction.ShowChecks : MieruMessageAction.None;

    /// <summary>What the Parts Life window does on <paramref name="message"/>: only the language message does anything. <paramref name="languageChanged"/> is its registered id.</summary>
    public static MieruMessageAction ForParty(uint message, uint languageChanged) =>
        message != 0 && message == languageChanged ? MieruMessageAction.ReloadLanguage : MieruMessageAction.None;

    /// <summary><see cref="ForMainWindow(uint, uint)"/> with the real registered id.</summary>
    public static MieruMessageAction ForMainWindow(uint message) => ForMainWindow(message, OpenChecks);

    /// <summary><see cref="ForParty(uint, uint)"/> with the real registered id.</summary>
    public static MieruMessageAction ForParty(uint message) => ForParty(message, LanguageChanged);

    private static uint Register(string name) => RegisterWindowMessage(name);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string name);
}
