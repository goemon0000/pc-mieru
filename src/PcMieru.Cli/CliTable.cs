using System.Text;

namespace PcMieru.Cli;

/// <summary>
/// A plain-text table. Widths are display widths (full-width = 2 cells), one column may shrink to fit the terminal,
/// and every cell is made printable first, so a process or service name cannot move the cursor.
/// </summary>
public static class CliTable
{
    /// <param name="headers">Header cells.</param>
    /// <param name="rows">Body rows; each has as many cells as there are headers.</param>
    /// <param name="rightAligned">Columns padded on the left (numbers). Others are padded on the right.</param>
    /// <param name="flexColumn">The column that is cut with an ellipsis when the line would be wider than the terminal.</param>
    /// <param name="consoleWidth">0 means "not a terminal": nothing is cut.</param>
    public static string Render(
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlySet<int>? rightAligned = null,
        int flexColumn = -1,
        int consoleWidth = 0)
    {
        rightAligned ??= new HashSet<int>();
        var columns = headers.Count;
        var cells = rows.Select(r => r.Select(CliText.Printable).ToArray()).ToList();
        var widths = headers.Select(CliText.Width).ToArray();
        foreach (var row in cells)
        {
            for (var i = 0; i < columns; i++)
            {
                widths[i] = Math.Max(widths[i], CliText.Width(row[i]));
            }
        }

        if (consoleWidth > 0 && flexColumn >= 0 && flexColumn < columns)
        {
            var others = widths.Sum() - widths[flexColumn] + ((columns - 1) * 2);
            widths[flexColumn] = Math.Max(Math.Min(12, widths[flexColumn]), Math.Min(widths[flexColumn], consoleWidth - others - 1));
        }

        var sb = new StringBuilder();
        Append(sb, headers.Select(CliText.Printable).ToArray(), widths, rightAligned, flexColumn);
        foreach (var row in cells)
        {
            Append(sb, row, widths, rightAligned, flexColumn);
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string[] row, int[] widths, IReadOnlySet<int> rightAligned, int flexColumn)
    {
        var last = row.Length - 1;
        for (var i = 0; i < row.Length; i++)
        {
            var text = i == flexColumn ? CliText.Truncate(row[i], widths[i]) : row[i];
            if (i == last && !rightAligned.Contains(i))
            {
                sb.Append(text);
            }
            else
            {
                sb.Append(rightAligned.Contains(i) ? CliText.PadLeft(text, widths[i]) : CliText.PadRight(text, widths[i]));
            }

            sb.Append(i < last ? "  " : "\n");
        }

        // An empty last cell would leave the separator behind as trailing spaces.
        if (!rightAligned.Contains(last))
        {
            var end = sb.Length - 1;
            while (end > 0 && sb[end - 1] == ' ')
            {
                end--;
            }

            sb.Length = end;
            sb.Append('\n');
        }
    }
}
