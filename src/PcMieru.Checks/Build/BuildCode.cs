// Provenance: PartsLife.Checks.Build.BuildCode and BuildCodeResult (pl-cloud 25e4571, src/PartsLife.Checks/Build/BuildCode.cs), MIT, same owner.
// The format, the field and category order, the omission rules, the merge of equal parts, the limits and the order in which parts are dropped
// are the old ones, byte for byte (the receiver, the /my-config page of web-autoai.com, reads this string). What changed: the names, the
// namespace, the surface (only Make, the result and the limits are public), the comments, and the notes: the old code built bilingual texts
// with L.T, here a note is a code plus its category and count and the screen turns it into words (DESIGN-A: no display text in Checks).
using System.Buffers.Text;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace PcMieru.Checks.Build;

/// <summary>What was left out of the code, and why. The screen writes the words.</summary>
public enum BuildNoteKind
{
    /// <summary>A category had more than <see cref="BuildCode.MaxPerCategory"/> kinds; <see cref="BuildNote.Count"/> kinds were left out.</summary>
    KindsLeftOut,

    /// <summary>The whole category was left out to stay within <see cref="BuildCode.MaxLength"/> characters.</summary>
    CategoryLeftOutForLength,

    /// <summary>Only the first kind of memory was kept, to stay within <see cref="BuildCode.MaxLength"/> characters.</summary>
    MemoryFirstKindOnly,

    /// <summary>One part of the category was left out, to fit (the last resort, which a real machine never reaches).</summary>
    OnePartLeftOutForLength,
}

/// <param name="Category">The category name as written in the code ("hdd", "optical-drive"...).</param>
/// <param name="Count">For <see cref="BuildNoteKind.KindsLeftOut"/>: how many kinds; 0 for the others.</param>
public sealed record BuildNote(BuildNoteKind Kind, string Category, int Count = 0);

/// <summary>The result of making a configuration code.</summary>
/// <param name="Code"><c>PCB1.</c> plus the base64url of the JSON: the text that goes to the clipboard.</param>
/// <param name="Json">The same content in a readable, indented form (the file that is saved, and what "look inside" shows).</param>
/// <param name="Config">The parts that are really in the code (after the length limit).</param>
/// <param name="Notes">What was left out, in the order it happened.</param>
public sealed record BuildCodeResult(string Code, string Json, BuildConfig Config, IReadOnlyList<BuildNote> Notes);

/// <summary>
/// The configuration code (the string pasted into /my-config of web-autoai.com to register "my PC").
///
/// Form: <c>PCB1.</c> plus UTF-8 JSON as base64url (no padding, + to -, / to _).
/// **It is a contract made together with the receiver: when the form changes, v goes up.**
/// <code>{"v":1,"src":"parts-life","p":{"cpu":[{"n":"..."}],"ram":[{"n":"...","cap":16,"spd":3200,"t":"DDR4","mfr":"...","q":2}]}}</code>
///
/// **The app never sends it.** It only makes the string; the person decides to copy or save it. Only model names and numbers go in (there
/// is no place for a serial number, the name of the PC, a user name or a drive letter). The JSON is written by hand (Utf8JsonWriter).
/// </summary>
public static class BuildCode
{
    public const string Prefix = "PCB1.";
    public const int Version = 1;
    public const string Source = "parts-life";

    /// <summary>The most characters after the prefix.</summary>
    public const int MaxLength = 6000;
    public const int MaxPerCategory = 8;
    public const int MaxQuantity = 16;

    /// <summary>
    /// What is left out first when the code is over the limit. **Parts that do not matter for the machine's performance go first**:
    /// the optical drive, the monitor, the HDD, and then every kind of memory after the first (after equal parts were merged by q).
    /// </summary>
    internal static readonly string[] DropFirst = { "optical-drive", "monitor", "hdd" };

    /// <summary>From the list of parts: the code, the JSON and what was left out.</summary>
    public static BuildCodeResult Make(BuildConfig raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var notes = new List<BuildNote>();
        var c = Normalize(raw, notes);
        string code = Encode(c);
        if (code.Length - Prefix.Length > MaxLength) code = Trim(c, notes);
        return new BuildCodeResult(code, ToJson(c, indented: true), c, notes);
    }

    /// <summary>
    /// Tidies the names, drops what looks like a serial number, merges equal parts into q, and keeps at most 8 kinds per category.
    /// </summary>
    internal static BuildConfig Normalize(BuildConfig raw, List<BuildNote>? notes = null)
    {
        var c = new BuildConfig();
        foreach (var cat in BuildConfig.Categories)
        {
            var parts = new List<BuildPart>();
            foreach (var p in raw[cat])
            {
                var s = Sanitize(p);
                if (s is null) continue;
                int i = parts.FindIndex(x => x.Key == s.Key);
                if (i >= 0) parts[i] = parts[i] with { Q = Math.Min(MaxQuantity, parts[i].Q + s.Q) };
                else parts.Add(s with { Q = Math.Clamp(s.Q, 1, MaxQuantity) });
            }
            if (parts.Count > MaxPerCategory)
            {
                notes?.Add(new BuildNote(BuildNoteKind.KindsLeftOut, cat, parts.Count - MaxPerCategory));
                parts = parts.Take(MaxPerCategory).ToList();
            }
            if (parts.Count > 0) c.Set(cat, parts);
        }
        return c;
    }

    /// <summary>Brings one part into the agreed range. Null when the name cannot be used (empty, or it looks like a serial number).</summary>
    internal static BuildPart? Sanitize(BuildPart p)
    {
        string n = BuildRules.CleanName(p.Name);
        if (BuildRules.IsPlaceholder(n) || BuildRules.LooksLikeSerial(n)) return null;
        string? t = Opt(p.Type, 8);
        string? m = Opt(p.Mfr, 40);
        if (m is not null && BuildRules.LooksLikeSerial(m)) m = null;
        return new BuildPart(n, Pos(p.Cap), Pos(p.Speed), t, m, Pos(p.Vram), p.Q);

        static string? Opt(string? s, int max)
        {
            string v = BuildRules.CleanName(s, max);
            return v.Length == 0 ? null : v;
        }
        static int? Pos(int? v) => v is > 0 ? v : null;
    }

    /// <summary>Leaves things out, in the fixed order, until the code is within the limit.</summary>
    private static string Trim(BuildConfig c, List<BuildNote> notes)
    {
        bool Fits(out string code)
        {
            code = Encode(c);
            return code.Length - Prefix.Length <= MaxLength;
        }

        string code;
        foreach (var cat in DropFirst)
        {
            if (c[cat].Count == 0) continue;
            c.Remove(cat);
            notes.Add(new BuildNote(BuildNoteKind.CategoryLeftOutForLength, cat));
            if (Fits(out code)) return code;
        }
        if (c["ram"].Count > 1)
        {
            c.Set("ram", c["ram"].Take(1));
            notes.Add(new BuildNote(BuildNoteKind.MemoryFirstKindOnly, "ram"));
            if (Fits(out code)) return code;
        }
        // Normally we never get here (8 kinds of 80 characters fit). If we do, cut one part at a time, from the later categories
        string[] order = { "ssd", "gpu", "ram", "motherboard", "cpu" };
        while (!Fits(out code))
        {
            var cat = order.FirstOrDefault(x => c[x].Count > 1) ?? order.FirstOrDefault(x => c[x].Count > 0);
            if (cat is null) break;
            c.Set(cat, c[cat].Take(c[cat].Count - 1));
            if (c[cat].Count == 0) c.Remove(cat);
            notes.Add(new BuildNote(BuildNoteKind.OnePartLeftOutForLength, cat));
        }
        return code;
    }

    /// <summary><c>PCB1.</c> plus the base64url of the compact JSON. Leaves nothing out (use <see cref="Make"/>).</summary>
    internal static string Encode(BuildConfig c) =>
        Prefix + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(ToJson(c, indented: false)));

    /// <summary>The JSON inside a code (for the tests, and for checking a pasted code). A FormatException when the form is wrong.</summary>
    internal static string Decode(string code)
    {
        string s = code.Trim();
        if (!s.StartsWith(Prefix, StringComparison.Ordinal)) throw new FormatException("The code does not start with PCB1.");
        try { return Encoding.UTF8.GetString(Base64Url.DecodeFromChars(s.AsSpan(Prefix.Length))); }
        catch (FormatException) { throw; }
        catch (Exception ex) { throw new FormatException(ex.Message, ex); }
    }

    /// <summary>
    /// Makes the JSON. An empty category is not written. The fields of a part are n, cap, spd, t, mfr, vram, q, in that order (what is not
    /// there is not written). Japanese names stay readable: they are not escaped to \uXXXX (it is the same JSON).
    /// </summary>
    internal static string ToJson(BuildConfig c, bool indented)
    {
        var buf = new System.Buffers.ArrayBufferWriter<byte>();
        var opts = new JsonWriterOptions { Indented = indented, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
        using (var w = new Utf8JsonWriter(buf, opts))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteString("src", Source);
            w.WriteStartObject("p");
            foreach (var cat in c.NonEmpty)
            {
                w.WriteStartArray(cat);
                foreach (var p in c[cat])
                {
                    w.WriteStartObject();
                    w.WriteString("n", p.Name);
                    if (p.Cap is { } cap) w.WriteNumber("cap", cap);
                    if (p.Speed is { } spd) w.WriteNumber("spd", spd);
                    if (p.Type is { } t) w.WriteString("t", t);
                    if (p.Mfr is { } m) w.WriteString("mfr", m);
                    if (p.Vram is { } vram) w.WriteNumber("vram", vram);
                    if (p.Q > 1) w.WriteNumber("q", p.Q);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }
}
