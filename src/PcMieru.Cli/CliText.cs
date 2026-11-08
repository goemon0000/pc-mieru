using System.Globalization;
using System.Text;
using PcMieru.Core;

namespace PcMieru.Cli;

public static class CliStrings
{
    public static readonly StringEntry[] All =
    [
        new("cli.usage", "使い方: pcmi <コマンド> [オプション]   （pcmi <コマンド> --help で書式）", "Usage: pcmi <command> [options]   (pcmi <command> --help for the syntax)"),
        new("cli.help.globals", "共通: --lang ja|en  表示言語   -h/--help  ヘルプ   --version  版", "Global: --lang ja|en  display language   -h/--help  help   --version  version"),
        new("cli.unknownCommand", "不明なコマンド", "unknown command"),
        new("cli.tryHelp", "pcmi --help で一覧を見られます", "Run pcmi --help for the list"),
        new("cli.notImplemented", "このコマンドはまだ実装されていません（骨組みのみ）", "This command is not implemented yet (skeleton only)"),
        new("cli.notFound.pid", "その PID のプロセスはありません", "No process with that PID"),
        new("cli.col.pid", "PID", "PID"),
        new("cli.col.name", "名前", "Name"),
        new("cli.col.cpu", "CPU%", "CPU%"),
        new("cli.col.mem", "メモリMB", "Mem MB"),
        new("cli.col.threads", "スレッド", "Threads"),
        new("cli.col.state", "状態", "State"),
        new("cli.ps.footer", "{shown} / {total} 件（{sort} 順）", "{shown} of {total} (sorted by {sort})"),
        new("cli.hw.category", "カテゴリ", "category"),
        new("cli.value.notAvailable", "-", "-"),
    ];
}

/// <summary>Display-width aware text helpers: a full-width character takes two terminal cells.</summary>
public static class CliText
{
    public static int Width(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += RuneWidth(rune);
        }

        return width;
    }

    public static int RuneWidth(Rune rune)
    {
        var v = rune.Value;
        if (v < 0x20 || (v >= 0x7F && v < 0xA0))
        {
            return 0;
        }

        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return 0;
        }

        return IsWide(v) ? 2 : 1;
    }

    private static bool IsWide(int v) =>
        (v >= 0x1100 && v <= 0x115F)
        || (v >= 0x2E80 && v <= 0x303E)
        || (v >= 0x3041 && v <= 0x33FF)
        || (v >= 0x3400 && v <= 0x4DBF)
        || (v >= 0x4E00 && v <= 0x9FFF)
        || (v >= 0xA000 && v <= 0xA4CF)
        || (v >= 0xAC00 && v <= 0xD7A3)
        || (v >= 0xF900 && v <= 0xFAFF)
        || (v >= 0xFE30 && v <= 0xFE6F)
        || (v >= 0xFF00 && v <= 0xFF60)
        || (v >= 0xFFE0 && v <= 0xFFE6)
        || (v >= 0x1F300 && v <= 0x1F64F)
        || (v >= 0x1F900 && v <= 0x1F9FF)
        || (v >= 0x20000 && v <= 0x3FFFD);

    /// <summary>Control characters are replaced so a process name can never move the cursor or recolor the terminal.</summary>
    public static string Printable(string text)
    {
        if (!text.Any(char.IsControl))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(char.IsControl(c) ? '?' : c);
        }

        return sb.ToString();
    }

    public static string PadRight(string text, int width)
    {
        var pad = width - Width(text);
        return pad > 0 ? text + new string(' ', pad) : text;
    }

    public static string PadLeft(string text, int width)
    {
        var pad = width - Width(text);
        return pad > 0 ? new string(' ', pad) + text : text;
    }

    /// <summary>Cuts to at most <paramref name="width"/> cells, ending in an ellipsis when something was cut.</summary>
    public static string Truncate(string text, int width)
    {
        if (Width(text) <= width)
        {
            return text;
        }

        var sb = new StringBuilder();
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var w = RuneWidth(rune);
            if (used + w > width - 1)
            {
                break;
            }

            sb.Append(rune.ToString());
            used += w;
        }

        return sb.Append('…').ToString();
    }

    public static string Num(double value, string format = "0.0") => value.ToString(format, CultureInfo.InvariantCulture);
}
