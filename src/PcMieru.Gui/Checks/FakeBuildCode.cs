using PcMieru.Checks;
using PcMieru.Checks.Build;
using PcMieru.Core;

namespace PcMieru.Gui;

/// <summary>
/// The configuration code of <c>--fakes</c>, and the screens' tests: the library's sample machine (a made-up PC, passed through the real rules, so
/// the code and the parts are what a real one would give) with a few things added that a real machine rarely has: one note of every kind and
/// three categories that could not be read, each with a different reason. The library cannot be made to give these from outside (a note is made by
/// the length limit and an unread category by a failed read), and the dialog has to be seen with them. The notes and the unread categories are
/// therefore a made-up story laid over the sample parts (a category may be listed both as a part and as unread): they say what the dialog
/// shows, never what a machine gave. Nothing here reads or measures.
/// </summary>
internal sealed class FakeBuildCode : IBuildCodeProvider, IFakeImplementation
{
    /// <summary>One note of each kind, and with them a count, so every sentence of the dialog is on show.</summary>
    public static readonly IReadOnlyList<BuildNote> PreviewNotes =
    [
        new BuildNote(BuildNoteKind.KindsLeftOut, "ssd", 3),
        new BuildNote(BuildNoteKind.MemoryFirstKindOnly, "ram"),
        new BuildNote(BuildNoteKind.CategoryLeftOutForLength, "optical-drive"),
        new BuildNote(BuildNoteKind.OnePartLeftOutForLength, "monitor"),
    ];

    /// <summary>Three categories that could not be read, each for another reason.</summary>
    public static readonly IReadOnlyList<BuildUnread> PreviewUnread =
    [
        new BuildUnread("hdd", CheckReason.Denied),
        new BuildUnread("monitor", CheckReason.Timeout),
        new BuildUnread("optical-drive", CheckReason.Unsupported),
    ];

    private readonly IBuildCodeProvider _sample;
    private readonly IReadOnlyList<BuildNote> _notes;
    private readonly IReadOnlyList<BuildUnread> _unread;

    /// <param name="sample">The provider whose result is taken. Null: the library's sample machine.</param>
    /// <param name="notes">The notes laid over it. Null: <see cref="PreviewNotes"/>.</param>
    /// <param name="unread">The unread categories laid over it. Null: <see cref="PreviewUnread"/>.</param>
    public FakeBuildCode(IBuildCodeProvider? sample = null, IReadOnlyList<BuildNote>? notes = null, IReadOnlyList<BuildUnread>? unread = null)
    {
        _sample = sample ?? BuildCodeProviders.Sample();
        _notes = notes ?? PreviewNotes;
        _unread = unread ?? PreviewUnread;
    }

    public async Task<BuildCodeResult> CreateAsync(CancellationToken ct)
    {
        var result = await _sample.CreateAsync(ct).ConfigureAwait(false);
        return result with { Notes = _notes, Unread = _unread };
    }
}
