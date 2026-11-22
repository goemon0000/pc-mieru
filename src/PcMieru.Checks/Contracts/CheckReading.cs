namespace PcMieru.Checks;

/// <summary>
/// How strictly a pcm-check/1 file is read (DESIGN-A §3 "読み手の寛容さ", §3.5).
/// </summary>
public enum CheckReadMode
{
    /// <summary>
    /// The writer's and the tests' reading: an unknown key, item ID, source, evidence code or note is an error. This is
    /// what the validator applies before a result is written, and what <see cref="CheckJson.Deserialize(string)"/> does.
    /// </summary>
    Strict,

    /// <summary>
    /// The reader of the history (listing, detail, MCP), which may meet a file a newer version wrote. It ignores unknown
    /// keys; skips a row with an item ID, a source or an evidence code it does not know (the row is "not shown by this
    /// version"); takes a well-formed note as it is; and reports a "kind" it does not know as
    /// <see cref="CheckFormatException.UnknownKind"/> so the file is kept. It still rejects an unknown verdict, reason or
    /// outcome, a wrong type, a missing required field and every broken rule of the rows it does read.
    /// </summary>
    Tolerant,
}

/// <summary>
/// A result as read by <see cref="CheckJson.Read(string, CheckReadMode)"/>. In tolerant mode the rows this version does
/// not know are not in <see cref="Result"/>.<see cref="CheckResult.Items"/>, but the stored counts and overall verdict were
/// checked against all rows, skipped ones included (DESIGN-A §3.5-11), and are kept as stored: so for
/// <see cref="SkippedItems"/> above 0 the result's counts add up to more than its Items. Use the stored values for display.
/// <see cref="SkippedNotes"/> counts the well-formed notes of a newer version that were left out of <see cref="CheckResult.Notes"/>.
/// </summary>
public sealed record CheckReading(CheckResult Result, int SkippedItems, int SkippedNotes);
