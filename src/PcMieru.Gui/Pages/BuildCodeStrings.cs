using System.Globalization;
using PcMieru.Checks;
using PcMieru.Checks.Build;

namespace PcMieru.Gui;

/// <summary>
/// Every word of the configuration code (PCB1) on the hardware page: the button, the dialog that shows what will leave the app, and the
/// words for what the Checks library gives as codes (a note kind, a category, the reason a category could not be read). The library never
/// writes text (DESIGN-A 9): a note is a kind plus a category and a count, and the screen turns it into a sentence here.
/// The categories, the note kinds and the reasons are written out one by one on purpose (no loop over the enum): a member that is added
/// without a sentence must make the coverage test fail, not get a sentence made for it.
/// Keys are "gui.hw.pcb1.*".
/// </summary>
internal static class BuildCodeStrings
{
    /// <summary>The key of the word for a category ("optical-drive" is written "optical_drive" in the key).</summary>
    public const string CategoryPrefix = "gui.hw.pcb1.cat.";

    /// <summary>The key of the sentence of a note kind.</summary>
    public const string NotePrefix = "gui.hw.pcb1.note.";

    /// <summary>The key of the words for why a category could not be read.</summary>
    public const string ReasonPrefix = "gui.hw.pcb1.reason.";

    public static readonly StringEntry[] All =
    [
        new("gui.hw.pcb1", "構成コードをコピー（/my-config 用）", "Copy configuration code (for /my-config)"),
        new("gui.hw.pcb1.tip", "この PC の部品の型番と数から、構成コードを作ります。内容を確認してから、コピーまたは保存できます。シリアル番号・MAC・PC の名前などは含みません", "Builds a configuration code from the model names and counts of this PC's parts. You can read it before copying or saving. Serial numbers, MAC addresses, the PC's name and similar are not included"),
        new("gui.hw.pcb1.title", "構成コード（/my-config 用）", "Configuration code (for /my-config)"),
        new("gui.hw.pcb1.lead", "このコードには、下の部品の名前と数が入っています。シリアル番号・MAC・PC の名前などは入りません。何も送信しません。コピーか保存を押した時だけ、内容がこの画面の外に出ます。", "The code holds the names and counts of the parts below. Serial numbers, MAC addresses, the PC's name and similar are not included. Nothing is sent. The content leaves this window only when you press Copy or Save."),
        new("gui.hw.pcb1.failed", "構成コードを作れませんでした（{detail}）", "Could not make the configuration code ({detail})"),

        new("gui.hw.pcb1.parts", "コードに入る部品", "Parts in the code"),
        new("gui.hw.pcb1.parts.none", "コードに入る部品がないので、コピーも保存もできません", "No parts are in the code, so there is nothing to copy or save"),
        new("gui.hw.pcb1.col.category", "種類", "Category"),
        new("gui.hw.pcb1.col.name", "名前", "Name"),
        new("gui.hw.pcb1.col.detail", "詳細", "Details"),
        new("gui.hw.pcb1.col.count", "数", "Count"),
        new("gui.hw.pcb1.code", "コード（コピーされる内容）", "Code (what Copy puts on the clipboard)"),
        new("gui.hw.pcb1.notes", "コードから省いた内容", "Left out of the code"),
        new("gui.hw.pcb1.unread", "読み取れなかった種類", "Categories that could not be read"),
        new("gui.hw.pcb1.unread.none", "すべての種類を読み取れました", "Every category was read"),

        new("gui.hw.pcb1.save", "JSON を保存", "Save JSON"),
        new("gui.hw.pcb1.save.title", "構成コードの JSON を保存", "Save the configuration code as JSON"),
        new("gui.hw.pcb1.saved", "保存しました", "Saved"),
        new("gui.hw.pcb1.save.failed", "保存できませんでした（{detail}）", "Could not save ({detail})"),
        new("gui.hw.pcb1.copy.failed", "コピーできませんでした（クリップボードが使用中です）", "Could not copy (the clipboard is busy)"),

        // A note names its category first, so the word for the category can start a sentence in both languages.
        new(NotePrefix + "kinds_left_out", "{category}: {max} 種類までなので、残りの {count} 種類を省きました", "{category}: only {max} kinds fit, so {count} were left out"),
        new(NotePrefix + "category_left_out_for_length", "{category}: 長さの上限（{max} 字）に収めるため、省きました", "{category}: left out to stay within {max} characters"),
        new(NotePrefix + "memory_first_kind_only", "{category}: 長さの上限（{max} 字）に収めるため、1 種類目だけにしました", "{category}: only the first kind was kept, to stay within {max} characters"),
        new(NotePrefix + "one_part_left_out_for_length", "{category}: 長さの上限に収めるため、1 つ省きました", "{category}: one part was left out to fit"),
        // The same note repeated for one category is one line with a count on the screen (the notes themselves stay one each).
        new(NotePrefix + "one_part_left_out_for_length_many", "{category}: 長さの上限に収めるため、{count} 個省きました", "{category}: {count} parts were left out to fit"),
        new(NotePrefix + "unknown", "{category}: 省いた内容があります（{kind}）", "{category}: something was left out ({kind})"),

        new(CategoryPrefix + "cpu", "CPU", "CPU"),
        new(CategoryPrefix + "gpu", "GPU", "GPU"),
        new(CategoryPrefix + "motherboard", "マザーボード", "Motherboard"),
        new(CategoryPrefix + "ram", "メモリ", "Memory"),
        new(CategoryPrefix + "ssd", "SSD", "SSD"),
        new(CategoryPrefix + "hdd", "HDD", "HDD"),
        new(CategoryPrefix + "monitor", "モニター", "Monitor"),
        new(CategoryPrefix + "optical_drive", "光学ドライブ", "Optical drive"),

        new(ReasonPrefix + "unsupported", "この PC では読み取れません", "Cannot be read on this PC"),
        new(ReasonPrefix + "denied", "権限がなく読み取れませんでした", "Not allowed to read it"),
        new(ReasonPrefix + "timeout", "時間内に答えがありませんでした", "No answer within the time limit"),
        new(ReasonPrefix + "error", "読み取りに失敗しました", "Reading failed"),
        new(ReasonPrefix + "partial", "一部しか読み取れませんでした", "Only part could be read"),
    ];
}

/// <summary>The words of the PCB1 dialog, made from the codes the Checks library gives. Nothing here reads or measures.</summary>
internal static class BuildCodeText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The word for a category. A category this version has no word for shows as itself (the coverage test makes sure none of <see cref="BuildConfig.Categories"/> is missing).</summary>
    public static string Category(string category) =>
        CheckFormat.Label(BuildCodeStrings.CategoryPrefix + category.Replace('-', '_')) ?? category;

    /// <summary>The sentence for a note. A kind this version has no sentence for is said as such, never dropped.</summary>
    public static string Note(BuildNote note)
    {
        var key = BuildCodeStrings.NotePrefix + CheckFormat.Snake(note.Kind.ToString());
        var values = Fmt.Dict(
            ("category", Category(note.Category)),
            ("count", note.Count.ToString("N0", Inv)),
            ("max", MaxFor(note.Kind)));
        return CheckFormat.Label(key) is not null
            ? Strings.Format(key, values)
            : Strings.Format(BuildCodeStrings.NotePrefix + "unknown", Fmt.Dict(("category", Category(note.Category)), ("kind", note.Kind.ToString())));
    }

    /// <summary>
    /// The lines of the notes on the screen. The same kind for the same category, repeated, is one line with the count (the last-resort cut
    /// takes one part at a time, so a tight configuration gives the same note again and again); every other note is one line of its own.
    /// The notes themselves are not changed: this is only how the screen says them.
    /// </summary>
    public static IReadOnlyList<string> NoteLines(IEnumerable<BuildNote> notes)
    {
        var lines = new List<string>();
        var onePart = new Dictionary<string, int>(StringComparer.Ordinal);
        var onePartAt = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var note in notes)
        {
            if (note.Kind == BuildNoteKind.OnePartLeftOutForLength)
            {
                onePart[note.Category] = onePart.GetValueOrDefault(note.Category) + 1;
                if (!onePartAt.ContainsKey(note.Category))
                {
                    onePartAt[note.Category] = lines.Count;
                    lines.Add(string.Empty);
                }

                continue;
            }

            lines.Add(Note(note));
        }

        foreach (var (category, count) in onePart)
        {
            lines[onePartAt[category]] = count == 1
                ? Note(new BuildNote(BuildNoteKind.OnePartLeftOutForLength, category))
                : Strings.Format(BuildCodeStrings.NotePrefix + "one_part_left_out_for_length_many", Fmt.Dict(("category", Category(category)), ("count", count.ToString("N0", Inv))));
        }

        return lines;
    }

    /// <summary>The mark that starts a line of a list: "・" in Japanese, "• " (with its space) in English.</summary>
    public static string Bullet => Strings.Current == Language.Ja ? "・" : "• ";

    /// <summary>What stands between the details of a part: " ・ " in Japanese, " · " in English.</summary>
    public static string DetailSeparator => Strings.Current == Language.Ja ? " ・ " : " · ";

    /// <summary>The name of a storage interface as it is written on the box: NVMe, SATA, USB; one that has no usual spelling is upper-cased.</summary>
    public static string TypeName(string type) => type.ToLowerInvariant() switch
    {
        "nvme" => "NVMe",
        "sata" => "SATA",
        "usb" => "USB",
        "sas" => "SAS",
        _ => type.ToUpperInvariant(),
    };

    /// <summary>Why a category could not be read.</summary>
    public static string Reason(CheckReason reason) =>
        CheckFormat.Label(BuildCodeStrings.ReasonPrefix + CheckFormat.Snake(reason.ToString())) ?? reason.ToString();

    /// <summary>"Memory: Timed out": one line of the list of categories that could not be read.</summary>
    public static string UnreadLine(BuildUnread unread) => Category(unread.Category) + ": " + Reason(unread.Reason);

    /// <summary>
    /// What a part carries besides its name, as it goes into the code: the kind or interface, the capacity, the speed, the video memory and the
    /// maker. Numbers and units only, so the line is the same in both languages.
    /// </summary>
    public static string Detail(BuildPart part)
    {
        var items = new List<string>();
        if (!string.IsNullOrEmpty(part.Type))
        {
            items.Add(TypeName(part.Type));
        }

        if (part.Cap is { } cap)
        {
            items.Add(cap.ToString("N0", Inv) + " GB");
        }

        if (part.Speed is { } speed)
        {
            items.Add(speed.ToString(Inv) + " MT/s");
        }

        if (part.Vram is { } vram)
        {
            items.Add("VRAM " + vram.ToString(Inv) + " GB");
        }

        if (!string.IsNullOrEmpty(part.Mfr))
        {
            items.Add(part.Mfr);
        }

        return string.Join(DetailSeparator, items);
    }

    // The limit a sentence names is the library's own constant, not a number copied here.
    private static string MaxFor(BuildNoteKind kind) => kind == BuildNoteKind.KindsLeftOut
        ? BuildCode.MaxPerCategory.ToString("N0", Inv)
        : BuildCode.MaxLength.ToString("N0", Inv);
}
