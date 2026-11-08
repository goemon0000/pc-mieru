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
        new("cli.col.started", "開始時刻（UTC）", "Started (UTC)"),
        new("cli.col.protection", "保護", "Protection"),
        new("cli.protection.protected", "保護: {reason}", "protected: {reason}"),
        new("cli.protection.unknown", "確認できず: {reason}", "unconfirmed: {reason}"),
        new("cli.target.changed", "PID {pid} は、指定した開始時刻のプロセスではなくなっています（別のプロセスに変わりました）。何もしていません", "PID {pid} is no longer the process started at that time (it is a different process now). Nothing was done"),
        new("cli.target.noStartedAt", "--started-at が無いので、いまその PID（{pid}）を使っているプロセス（開始 {started}）を対象にします", "No --started-at: acting on the process that has PID {pid} right now (started {started})"),
        new("cli.hint.elevate", "管理者の権限が要ります。--elevate を付けると管理者の許可を求めます", "Administrator rights are needed. Add --elevate to ask for approval"),
        new("cli.elevate.prompt", "管理者の許可を求めています。表示される確認に答えてください", "Asking for administrator approval; answer the prompt that appears"),
        new("cli.elevate.declined", "管理者の許可が出されませんでした", "Administrator approval was not given"),
        new("cli.result.ok", "完了しました", "Done"),
        new("cli.result.error", "失敗しました", "Failed"),
        new("cli.result.invalidArguments", "指定が正しくありません", "Invalid arguments"),
        new("cli.result.notFound", "対象が見つかりません", "Not found"),
        new("cli.result.denied", "権限が足りません", "Permission denied"),
        new("cli.result.protected", "保護された対象のため何もしていません", "Protected target; nothing was done"),
        new("cli.result.multipleMatches", "一致が複数あります", "More than one match"),
        new("cli.result.partial", "一部だけ成功しました", "Partly done"),
        new("cli.result.gone", "対象はすでにありません", "The target is already gone"),
        new("cli.result.cancelled", "中止しました", "Cancelled"),
        new("cli.result.unsupported", "この環境ではできません", "Not supported here"),
        new("cli.result.notImplemented", "まだできません", "Not available yet"),
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
        new("cli.fmt.duration.dhm", "{d} 日 {h} 時間 {m} 分", "{d}d {h}h {m}m"),
        new("cli.fmt.duration.hm", "{h} 時間 {m} 分", "{h}h {m}m"),
        new("cli.fmt.duration.m", "{m} 分", "{m}m"),
        new("cli.fmt.yes", "はい", "yes"),
        new("cli.fmt.no", "いいえ", "no"),
        new("cli.fmt.paren", "（{text}）", " ({text})"),
        .. CliLabels.Entries,
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
