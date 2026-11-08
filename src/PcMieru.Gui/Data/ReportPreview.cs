namespace PcMieru.Gui;

/// <summary>What a line of the report is, for the formatted preview.</summary>
internal enum PreviewKind
{
    Title,
    Heading,
    Bullet,
    SubBullet,
    Quote,
    TableHeader,
    TableRow,
    Paragraph,
    Blank,
}

/// <summary>How a piece of a line is drawn: plain text, a name from the PC (a code span in the Markdown), or the "[level / kind]" tag in front of an observation.</summary>
internal enum PreviewStyle
{
    Plain,
    Code,
    Tag,
}

internal readonly record struct PreviewRun(string Text, PreviewStyle Style);

/// <summary>One line of the preview. A table row has one entry in <see cref="Cells"/> per column; every other line has exactly one.</summary>
internal sealed record PreviewBlock(PreviewKind Kind, IReadOnlyList<IReadOnlyList<PreviewRun>> Cells)
{
    public IReadOnlyList<PreviewRun> Runs => Cells.Count > 0 ? Cells[0] : [];

    /// <summary>The text of a cell without any drawing information.</summary>
    public static string Plain(IEnumerable<PreviewRun> runs) => string.Concat(runs.Select(r => r.Text));
}

/// <summary>
/// Reads the Markdown that the report builder writes (headings, bullets, one level of sub-bullets, a quote, a table and
/// code spans) into lines the preview can draw. It is not a general Markdown reader: it knows the report's own shapes, and
/// anything else is kept as a plain paragraph so no text is ever dropped. The preview and the copied text come from the
/// same Markdown, so what is shown is what is copied.
/// </summary>
internal static class ReportPreview
{
    private const int MaxTagLength = 40;

    public static IReadOnlyList<PreviewBlock> Parse(string markdown)
    {
        var blocks = new List<PreviewBlock>();
        var inTable = false;
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                inTable = false;
                if (blocks.Count > 0 && blocks[^1].Kind != PreviewKind.Blank)
                {
                    blocks.Add(new PreviewBlock(PreviewKind.Blank, [[]]));
                }

                continue;
            }

            if (line.StartsWith('|'))
            {
                var cells = SplitRow(line);
                if (IsSeparator(cells))
                {
                    continue;
                }

                blocks.Add(new PreviewBlock(inTable ? PreviewKind.TableRow : PreviewKind.TableHeader, cells.Select(c => Inline(c.Trim(), tag: false)).ToList()));
                inTable = true;
                continue;
            }

            inTable = false;
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                blocks.Add(Single(PreviewKind.Heading, line[3..], tag: false));
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                blocks.Add(Single(PreviewKind.Title, line[2..], tag: false));
            }
            else if (line.StartsWith("  - ", StringComparison.Ordinal))
            {
                blocks.Add(Single(PreviewKind.SubBullet, line[4..], tag: false));
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                blocks.Add(Single(PreviewKind.Bullet, line[2..], tag: true));
            }
            else if (line.StartsWith('>'))
            {
                blocks.Add(Single(PreviewKind.Quote, line[1..].TrimStart(), tag: false));
            }
            else
            {
                blocks.Add(Single(PreviewKind.Paragraph, line, tag: false));
            }
        }

        while (blocks.Count > 0 && blocks[^1].Kind == PreviewKind.Blank)
        {
            blocks.RemoveAt(blocks.Count - 1);
        }

        return blocks;
    }

    /// <summary>The preview as plain text, one line per block (a table row has its cells separated by a tab).</summary>
    public static string PlainText(IEnumerable<PreviewBlock> blocks) =>
        string.Join("\n", blocks.Select(b => string.Join("\t", b.Cells.Select(PreviewBlock.Plain))));

    private static PreviewBlock Single(PreviewKind kind, string text, bool tag) => new(kind, [Inline(text, tag)]);

    /// <summary>A table line to its cells. The report writes a "|" inside a cell as "\|", which is not a separator.</summary>
    internal static List<string> SplitRow(string line)
    {
        var body = line.Trim();
        if (body.StartsWith('|'))
        {
            body = body[1..];
        }

        if (body.EndsWith('|') && !body.EndsWith("\\|", StringComparison.Ordinal))
        {
            body = body[..^1];
        }

        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '\\' && i + 1 < body.Length && body[i + 1] == '|')
            {
                current.Append('|');
                i++;
            }
            else if (c == '|')
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString());
        return cells;
    }

    /// <summary>The "---|:---:|---:" line under a table header.</summary>
    private static bool IsSeparator(List<string> cells) =>
        cells.Count > 0 && cells.All(c =>
        {
            var t = c.Trim().Trim(':');
            return t.Length > 0 && t.All(ch => ch == '-');
        });

    /// <summary>
    /// A line to runs: text between a pair of backticks is a Code run. A backtick with no partner stays as the plain text it
    /// is. With <paramref name="tag"/>, a leading "[a / b]" is cut off as the Tag run.
    /// </summary>
    internal static List<PreviewRun> Inline(string text, bool tag)
    {
        var runs = new List<PreviewRun>();
        if (tag && text.StartsWith('['))
        {
            var close = text.IndexOf("] ", StringComparison.Ordinal);
            if (close is > 0 and <= MaxTagLength && text.AsSpan(1, close - 1).Contains(" / ", StringComparison.Ordinal))
            {
                runs.Add(new PreviewRun(text[..(close + 1)], PreviewStyle.Tag));
                text = text[(close + 1)..];
            }
        }

        var start = 0;
        while (start < text.Length)
        {
            var open = text.IndexOf('`', start);
            var close = open >= 0 ? text.IndexOf('`', open + 1) : -1;
            if (open < 0 || close < 0)
            {
                runs.Add(new PreviewRun(text[start..], PreviewStyle.Plain));
                break;
            }

            if (open > start)
            {
                runs.Add(new PreviewRun(text[start..open], PreviewStyle.Plain));
            }

            if (close > open + 1)
            {
                runs.Add(new PreviewRun(text[(open + 1)..close], PreviewStyle.Code));
            }

            start = close + 1;
        }

        return runs;
    }
}
