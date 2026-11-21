using System.Globalization;
using System.Text;
using PcMieru.Checks;
using PcMieru.Checks.Build;
using PcMieru.Core;
using PcMieru.Core.Hardware.Share;

namespace PcMieru.Cli.Commands;

/// <summary>
/// <c>pcmi parts --share</c>: the model numbers of this PC as "pcm-parts/1" JSON (see PartsShare), for pasting into the web tool.
/// Model numbers only; the document never holds a serial number, MAC, IP address, PC or user name, path, date or version string.
/// <para>
/// <c>pcmi parts --code</c>: the configuration code (PCB1) of this PC, one line on stdout, for the /my-config page. <c>-o file</c> also writes the
/// same content as readable JSON (UTF-8, no BOM, no trailing newline: exactly what the provider made). What was left out of the code, and what
/// could not be read, is said on stderr in the user's language. A PC with nothing to put in the code gives no code and exit 1.
/// </para>
/// </summary>
public sealed class PartsCommand : ICliCommand
{
    private const string NotePrefix = "cli.parts.note.";

    public string Name => "parts";

    public string Usage => "pcmi parts --share [--include-usb] [-o file] | --code [-o file]";

    public IReadOnlyList<OptionSpec> Options { get; } =
    [
        new("share"),
        new("code"),
        new("include-usb"),
        new("output", 'o', TakesValue: true),
    ];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.parts", "型番だけの一覧（--share）と構成コード（--code）。シリアル番号・MAC・PC の名前などは含まない", "Model numbers only (--share) or the configuration code (--code); no serial numbers, MAC addresses or PC name"),
        new("cli.parts.needShare", "型番の一覧は --share、構成コードは --code を付けてください（pcmi parts --share / pcmi parts --code）", "Add --share for the model numbers or --code for the configuration code (pcmi parts --share / pcmi parts --code)"),
        new("cli.parts.shareAndCode", "--share と --code は同時に使えません。どちらか一方を付けてください", "--share and --code cannot be used together; give one of them"),
        new("cli.parts.emptyConfig", "コードに入る部品を読み取れなかったため、コードは出していません", "No parts could be read for the code, so no code was made"),
        new("cli.parts.outputFailed", "{file} に書き込めなかったため、コードは出していません（{reason}）", "Could not write {file}, so no code was printed ({reason})"),
        new("cli.parts.shareOutputFailed", "{file} に書き込めませんでした（{reason}）", "Could not write {file} ({reason})"),
        new("cli.parts.output.denied", "権限がありません", "access denied"),
        new("cli.parts.output.error", "書き込みに失敗しました", "the write failed"),
        new("cli.parts.unread", "{category}: 読み取れませんでした（{reason}）", "{category}: not read ({reason})"),

        // What was left out of the code. The meaning is the one of the app's window; the words are this command's own.
        new(NotePrefix + "kinds_left_out", "{category}: {max} 種類までのため、残り {count} 種類を省きました", "{category}: only {max} models fit; {count} more were left out"),
        new(NotePrefix + "category_left_out_for_length", "{category}: 長さの上限（{max} 字）に収めるため、省きました", "{category}: left out to stay within {max} characters"),
        new(NotePrefix + "memory_first_kind_only", "{category}: 長さの上限（{max} 字）に収めるため、1 種類目だけを残しました", "{category}: only the first model was kept, to stay within {max} characters"),
        new(NotePrefix + "one_part_left_out_for_length", "{category}: 長さの上限に収めるため、1 つ省きました", "{category}: one part was left out to fit"),
        new(NotePrefix + "one_part_left_out_for_length_many", "{category}: 長さの上限に収めるため、{count} 個省きました", "{category}: {count} parts were left out to fit"),
        new(NotePrefix + "unknown", "{category}: 省いた内容があります（{kind}）", "{category}: something was left out ({kind})"),

        // The eight categories of the code, and why one could not be read.
        new("cli.parts.category.cpu", "CPU", "CPU"),
        new("cli.parts.category.gpu", "GPU", "GPU"),
        new("cli.parts.category.motherboard", "マザーボード", "Motherboard"),
        new("cli.parts.category.ram", "メモリ", "Memory"),
        new("cli.parts.category.ssd", "SSD", "SSD"),
        new("cli.parts.category.hdd", "HDD", "HDD"),
        new("cli.parts.category.monitor", "モニター", "Monitor"),
        new("cli.parts.category.optical_drive", "光学ドライブ", "Optical drive"),
        new("cli.parts.reason.unsupported", "この PC では読み取れません", "cannot be read on this PC"),
        new("cli.parts.reason.denied", "権限がありません", "access denied"),
        new("cli.parts.reason.timeout", "時間内に答えがありませんでした", "no answer in time"),
        new("cli.parts.reason.error", "読み取りに失敗しました", "the read failed"),
        new("cli.parts.reason.partial", "一部しか読み取れませんでした", "only part could be read"),
    ];

    public async Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(0, 0);
        var share = args.Has("share");
        var code = args.Has("code");
        if (share && code)
        {
            throw new ArgException(context.Text("cli.parts.shareAndCode"));
        }

        if (!share && !code)
        {
            throw new ArgException(context.Text("cli.parts.needShare"));
        }

        if (code)
        {
            if (args.Has("include-usb"))
            {
                throw new ArgException("--include-usb belongs to --share (the code holds no USB device)");
            }

            return await RunCodeAsync(context, args.Get("output")).ConfigureAwait(false);
        }

        var insights = context.Services().Insights;
        var json = await PartsShare.CreateJsonAsync(
            (category, ct) => insights.GetHardwareAsync(category, ct),
            VersionCommand.CurrentVersion,
            args.Has("include-usb"),
            context.Ct).ConfigureAwait(false);

        var path = args.Get("output");
        if (path is null)
        {
            context.Out.WriteLine(json);
        }
        else
        {
            // A path that cannot be written is exit 1, as for --code -o; 4 is only for a refused --elevate (DESIGN-A 8).
            try
            {
                await File.WriteAllTextAsync(path, json + "\n", new UTF8Encoding(false), context.Ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var reason = ex is UnauthorizedAccessException ? "cli.parts.output.denied" : "cli.parts.output.error";
                context.Err.WriteLine("pcmi parts: " + context.Format("cli.parts.shareOutputFailed", ("file", OutputName(path)), ("reason", context.Text(reason))));
                return ExitCode.Failure;
            }
        }

        return ExitCode.Success;
    }

    private static async Task<ExitCode> RunCodeAsync(CliContext context, string? path)
    {
        var result = await context.BuildCodeProvider.CreateAsync(context.Ct).ConfigureAwait(false);

        // What was left out and what could not be read: stderr, whatever else happens.
        foreach (var line in NoteLines(context, result.Notes))
        {
            context.Err.WriteLine("pcmi parts: " + line);
        }

        foreach (var unread in result.Unread)
        {
            context.Err.WriteLine("pcmi parts: " + UnreadLine(context, unread));
        }

        if (result.Config.IsEmpty)
        {
            context.Err.WriteLine("pcmi parts: " + context.Text("cli.parts.emptyConfig"));
            return ExitCode.Failure;
        }

        if (path is not null)
        {
            // The file first (DESIGN-A 8): a code printed for a file that could not be written would look like a success to a script that reads
            // stdout. The provider's JSON as it is: UTF-8 without a BOM, and no newline added. A path that cannot be written (a folder, a read-only
            // file, a missing folder, a full disk) is exit 1, not 4: 4 is only for a refused --elevate.
            try
            {
                await File.WriteAllBytesAsync(path, new UTF8Encoding(false).GetBytes(result.Json), context.Ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var reason = ex is UnauthorizedAccessException ? "cli.parts.output.denied" : "cli.parts.output.error";
                context.Err.WriteLine("pcmi parts: " + context.Format("cli.parts.outputFailed", ("file", OutputName(path)), ("reason", context.Text(reason))));
                return ExitCode.Failure;
            }
        }

        context.Out.Write(result.Code + "\n");
        return ExitCode.Success;
    }

    /// <summary>The name of the output file as the user gave it, without its folders (the message names the file, never a path the system built).</summary>
    private static string OutputName(string path)
    {
        var name = Path.GetFileName(path);
        return CliText.Printable(string.IsNullOrEmpty(name) ? path : name);
    }

    /// <summary>
    /// The lines for the notes. The same kind for the same category, repeated, is one line with the count (the last-resort cut takes one part
    /// at a time, so a tight configuration gives the same note again and again); every other note is one line of its own.
    /// </summary>
    internal static IReadOnlyList<string> NoteLines(CliContext context, IEnumerable<BuildNote> notes)
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

            lines.Add(NoteText(context, note));
        }

        foreach (var (category, count) in onePart)
        {
            lines[onePartAt[category]] = count == 1
                ? NoteText(context, new BuildNote(BuildNoteKind.OnePartLeftOutForLength, category))
                : context.Format(NotePrefix + "one_part_left_out_for_length_many", ("category", CategoryWord(context, category)), ("count", Number(count)));
        }

        return lines;
    }

    /// <summary>The sentence for one note. A kind this version has no sentence for is said as such, never dropped.</summary>
    internal static string NoteText(CliContext context, BuildNote note)
    {
        var category = CategoryWord(context, note.Category);
        if (NoteKey(note.Kind) is not { } suffix)
        {
            return context.Format(NotePrefix + "unknown", ("category", category), ("kind", note.Kind.ToString()));
        }

        var max = note.Kind == BuildNoteKind.KindsLeftOut ? BuildCode.MaxPerCategory : BuildCode.MaxLength;
        return context.Format(NotePrefix + suffix, ("category", category), ("count", Number(note.Count)), ("max", Number(max)));
    }

    /// <summary>The key suffix of a note kind, or null for a kind this version does not know.</summary>
    internal static string? NoteKey(BuildNoteKind kind) => kind switch
    {
        BuildNoteKind.KindsLeftOut => "kinds_left_out",
        BuildNoteKind.CategoryLeftOutForLength => "category_left_out_for_length",
        BuildNoteKind.MemoryFirstKindOnly => "memory_first_kind_only",
        BuildNoteKind.OnePartLeftOutForLength => "one_part_left_out_for_length",
        _ => null,
    };

    internal static string UnreadLine(CliContext context, BuildUnread unread) =>
        context.Format(
            "cli.parts.unread",
            ("category", CategoryWord(context, unread.Category)),
            ("reason", context.Text("cli.parts.reason." + unread.Reason.ToString().ToLowerInvariant())));

    /// <summary>The word for a category; one this version has no word for shows as itself.</summary>
    internal static string CategoryWord(CliContext context, string category)
    {
        var key = "cli.parts.category." + category.Replace('-', '_');
        var word = context.Text(key);
        return word == "[" + key + "]" ? category : word;
    }

    private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
