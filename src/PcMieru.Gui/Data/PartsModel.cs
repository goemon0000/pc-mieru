using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace PcMieru.Gui;

/// <summary>
/// One entry of the parts tree: a category (CPU, GPUs, ...) or one item below it (one GPU, one memory module, one volume).
/// Built from the JSON form of the hardware model, so the page shows whatever the model holds and never invents a value.
/// </summary>
internal sealed class PartsNode
{
    public PartsNode(string id, string? titleKey, string title, string categoryKey, JsonObject? whole, JsonObject? props)
    {
        Id = id;
        TitleKey = titleKey;
        Title = title;
        CategoryKey = categoryKey;
        Whole = whole;
        Props = props;
    }

    /// <summary>"cpu", "gpu.0", "memory.1": stable while the hardware does not change, so the selection survives a reload.</summary>
    public string Id { get; }

    /// <summary>A category's title comes from the string table (so it follows the language); an item's title is data.</summary>
    public string? TitleKey { get; }

    public string Title { get; }

    public string CategoryKey { get; }

    /// <summary>The {status, source, value} wrapper of the category. Items share their category's wrapper (it carries the source).</summary>
    public JsonObject? Whole { get; }

    /// <summary>The object whose properties are the rows. Null for an array category (its rows are its items) and for a category with no data.</summary>
    public JsonObject? Props { get; }

    public List<PartsNode> Children { get; } = [];

    public string DisplayTitle => TitleKey is null ? Title : Strings.Get(TitleKey);

    public override string ToString() => DisplayTitle;
}

/// <summary>One line of the property grid.</summary>
internal sealed record PartsRow(string Key, string Label, string Text, ValueStatus Status, string Source, string? Detail, bool Expert);

/// <summary>Turns the JSON of a <see cref="HardwareInfo"/> into tree nodes and property rows. No UI in here.</summary>
internal static class PartsModel
{
    private static readonly string[] StatusNames = ["notCollected", "ok", "unsupported", "denied", "timeout", "error", "gone"];

    /// <summary>The model's categories in the order the tree shows them: (JSON name, string-table key).</summary>
    private static readonly (string Json, string Key)[] Categories =
    [
        ("cpu", "cpu"),
        ("memory", "memory"),
        ("board", "board"),
        ("gpus", "gpu"),
        ("storage", "storage"),
        ("network", "network"),
        ("displays", "display"),
        ("audio", "audio"),
        ("usb", "usb"),
        ("battery", "battery"),
        ("os", "os"),
    ];

    /// <summary>Ids, serial numbers and low-level details. They are kept, but folded away under "details" so the page stays readable.</summary>
    private static readonly HashSet<string> ExpertKeys = new(StringComparer.Ordinal)
    {
        "family", "stepping", "instructionSets",
        "serialNumber", "partNumber", "revision", "ranks", "voltageMillivolts",
        "luid", "pciId", "vbios", "pcieGeneration", "pcieWidth",
        "macAddress", "ipAddresses", "dnsServers",
        "vendorId", "productId", "parentId",
        "firmware", "partitionStyle", "trimSupported",
        "manufactureWeek", "bitsPerSample", "sampleRateHz",
        "build", "virtualizationBasedSecurity", "hvci",
    };

    private static readonly string[] TitleKeys = ["name", "model", "product", "slot"];

    public static int CategoryCount => Categories.Length;

    /// <summary>The tree: one node per category (all present, even when the value is missing), items as children.</summary>
    public static List<PartsNode> Build(JsonObject root)
    {
        var nodes = new List<PartsNode>(Categories.Length);
        foreach (var (json, key) in Categories)
        {
            var titleKey = "gui.hw.cat." + key;
            if (root[json] is not JsonObject whole)
            {
                nodes.Add(new PartsNode(key, titleKey, string.Empty, key, null, null));
                continue;
            }

            var inner = StatusOf(whole) == ValueStatus.Ok ? whole["value"] : null;
            switch (inner)
            {
                case JsonObject obj:
                    var single = new PartsNode(key, titleKey, string.Empty, key, whole, obj);
                    AddChildren(single, obj, whole);
                    nodes.Add(single);
                    break;
                case JsonArray array:
                    var list = new PartsNode(key, titleKey, string.Empty, key, whole, null);
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (array[i] is JsonObject item)
                        {
                            var child = new PartsNode(key + "." + i.ToString(CultureInfo.InvariantCulture), null, ItemTitle(item, i), key, whole, item);
                            AddChildren(child, item, whole);
                            list.Children.Add(child);
                        }
                    }

                    nodes.Add(list);
                    break;
                default:
                    nodes.Add(new PartsNode(key, titleKey, string.Empty, key, whole, null));
                    break;
            }
        }

        return nodes;
    }

    /// <summary>Finds a node by id anywhere in the tree.</summary>
    public static PartsNode? Find(IEnumerable<PartsNode> nodes, string id)
    {
        foreach (var node in nodes)
        {
            if (node.Id == id)
            {
                return node;
            }

            if (Find(node.Children, id) is { } inner)
            {
                return inner;
            }
        }

        return null;
    }

    /// <summary>The rows of one node, in the model's order. Expert rows are marked, not removed: the page decides whether to fold them.</summary>
    public static List<PartsRow> Rows(PartsNode node)
    {
        var rows = new List<PartsRow>();
        var source = SourceOf(node.Whole);
        if (node.Props is null)
        {
            if (node.Whole is null || StatusOf(node.Whole) != ValueStatus.Ok)
            {
                rows.Add(new PartsRow(
                    node.CategoryKey,
                    node.DisplayTitle,
                    Fmt.Dash,
                    node.Whole is null ? ValueStatus.NotCollected : StatusOf(node.Whole),
                    source,
                    node.Whole?["detail"]?.GetValue<string>(),
                    false));
            }
            else if (node.Children.Count == 0)
            {
                rows.Add(new PartsRow(node.CategoryKey, node.DisplayTitle, Strings.Get("gui.common.nothing"), ValueStatus.Ok, source, null, false));
            }
            else
            {
                for (var i = 0; i < node.Children.Count; i++)
                {
                    var label = "#" + (i + 1).ToString(CultureInfo.CurrentCulture);
                    rows.Add(new PartsRow(label, label, node.Children[i].DisplayTitle, ValueStatus.Ok, source, null, false));
                }
            }

            return rows;
        }

        foreach (var (key, value) in node.Props)
        {
            AddRow(rows, node.CategoryKey, key, value, source, nested: false);
        }

        return rows;
    }

    /// <summary>
    /// A value becomes one row. A record without a status of its own (the model's "details") is opened: its members become
    /// rows of the same part, marked as details so they fold away. Lists of parts are the tree's children, not rows.
    /// </summary>
    private static void AddRow(List<PartsRow> rows, string category, string key, JsonNode? value, string source, bool nested)
    {
        if (value is JsonArray array && array.Any(e => e is JsonObject))
        {
            return;
        }

        if (value is JsonObject record && !record.ContainsKey("status"))
        {
            foreach (var (innerKey, innerValue) in record)
            {
                AddRow(rows, category, innerKey, innerValue, source, nested: true);
            }

            return;
        }

        var row = Row(category, key, value, source);
        rows.Add(nested && !row.Expert ? row with { Expert = true } : row);
    }

    /// <summary>The whole tree as plain text for pasting. Missing values say why; nothing is left blank.</summary>
    public static string ToText(IReadOnlyList<PartsNode> nodes)
    {
        var sb = new StringBuilder();
        foreach (var node in nodes)
        {
            AppendNode(sb, node, 2);
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    public static string Label(string key) =>
        LabelKeys.Contains(key) ? Strings.Get("gui.hw.f." + key) : Humanize(key);

    public static string StatusText(ValueStatus status) => Strings.Status(status);

    public static ValueStatus StatusOf(JsonObject value)
    {
        var index = Array.IndexOf(StatusNames, value["status"]?.GetValue<string>());
        return index >= 0 ? (ValueStatus)index : ValueStatus.Error;
    }

    public static string SourceOf(JsonObject? value) => value?["source"]?.GetValue<string>() ?? string.Empty;

    /// <summary>The text of a grid cell for a key and a JSON value: units from the key's name, enums by name, lists joined.</summary>
    public static string Format(string key, JsonNode? node)
    {
        switch (node)
        {
            case null:
                return Fmt.Dash;
            case JsonArray array:
                return array.Count == 0 ? Strings.Get("gui.common.nothing") : string.Join(", ", array.Select(e => Format(key, e)));
            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case System.Text.Json.JsonValueKind.True:
                    case System.Text.Json.JsonValueKind.False:
                        return Strings.Get(value.GetValue<bool>() ? "gui.hw.yes" : "gui.hw.no");
                    case System.Text.Json.JsonValueKind.String:
                        var text = value.GetValue<string>();
                        return EnumKeys.Contains(key) ? EnumText(text) : text;
                    case System.Text.Json.JsonValueKind.Number when double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number):
                        return Number(key, number);
                    default:
                        return value.ToJsonString();
                }

            default:
                return node.ToJsonString();
        }
    }

    private static PartsRow Row(string category, string key, JsonNode? value, string wholeSource)
    {
        var expert = ExpertKeys.Contains(key) || (category == "cpu" && key == "model");
        if (value is JsonObject wrapper && wrapper.ContainsKey("status"))
        {
            var status = StatusOf(wrapper);
            var text = status == ValueStatus.Ok ? Format(key, wrapper["value"]) : Fmt.Dash;
            return new PartsRow(key, Label(key), text, status, SourceOf(wrapper), wrapper["detail"]?.GetValue<string>(), expert);
        }

        return new PartsRow(key, Label(key), Format(key, value), ValueStatus.Ok, wholeSource, null, expert);
    }

    private static void AddChildren(PartsNode parent, JsonObject obj, JsonObject whole)
    {
        foreach (var (key, value) in obj)
        {
            if (value is not JsonArray array || !array.Any(e => e is JsonObject))
            {
                continue;
            }

            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonObject item)
                {
                    parent.Children.Add(new PartsNode(
                        parent.Id + "." + key + "." + i.ToString(CultureInfo.InvariantCulture),
                        null,
                        ItemTitle(item, i),
                        parent.CategoryKey,
                        whole,
                        item));
                }
            }
        }
    }

    /// <summary>A name for an item: its name/model/product/slot when the model has one, a drive letter for a volume, else "#n".</summary>
    private static string ItemTitle(JsonObject item, int index)
    {
        foreach (var key in TitleKeys)
        {
            if (item[key] is JsonObject wrapper && StatusOf(wrapper) == ValueStatus.Ok && wrapper["value"] is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0)
            {
                return text;
            }
        }

        if (item["letter"] is JsonValue letter && letter.TryGetValue<string>(out var drive) && drive.Length > 0)
        {
            var label = item["label"] is JsonValue l && l.TryGetValue<string>(out var name) ? name : string.Empty;
            return label.Length > 0 ? drive + " (" + label + ")" : drive;
        }

        return "#" + (index + 1).ToString(CultureInfo.CurrentCulture);
    }

    private static void AppendNode(StringBuilder sb, PartsNode node, int depth)
    {
        sb.Append('\n').Append('#', Math.Min(depth, 6)).Append(' ').Append(node.DisplayTitle).Append('\n');
        if (node.Props is not null || node.Children.Count == 0)
        {
            foreach (var row in Rows(node))
            {
                sb.Append("- ").Append(row.Label).Append(": ").Append(row.Text);
                if (row.Status != ValueStatus.Ok)
                {
                    sb.Append(" (").Append(StatusText(row.Status));
                    if (!string.IsNullOrWhiteSpace(row.Detail))
                    {
                        sb.Append(": ").Append(row.Detail);
                    }

                    sb.Append(')');
                }

                sb.Append('\n');
            }
        }

        foreach (var child in node.Children)
        {
            AppendNode(sb, child, depth + 1);
        }
    }

    private static string Number(string key, double n)
    {
        var c = CultureInfo.CurrentCulture;
        if (key.EndsWith("Bytes", StringComparison.Ordinal))
        {
            return Fmt.Bytes((long)n);
        }

        if (key.EndsWith("Mhz", StringComparison.Ordinal))
        {
            return PerfMetrics.Ghz(n);
        }

        if (key.EndsWith("MtPerSec", StringComparison.Ordinal))
        {
            return n.ToString("0", c) + " MT/s";
        }

        if (key.EndsWith("BitsPerSec", StringComparison.Ordinal))
        {
            return PerfMetrics.Speed((long)n);
        }

        if (key.EndsWith("Millivolts", StringComparison.Ordinal))
        {
            return (n / 1000).ToString("0.00", c) + " V";
        }

        if (key.EndsWith("Watts", StringComparison.Ordinal))
        {
            return n.ToString("0.#", c) + " W";
        }

        if (key.EndsWith("MilliwattHours", StringComparison.Ordinal))
        {
            return (n / 1000).ToString("0.0", c) + " Wh";
        }

        if (key.EndsWith("Hz", StringComparison.Ordinal))
        {
            return n.ToString("0.##", c) + " Hz";
        }

        if (key.EndsWith("Mm", StringComparison.Ordinal))
        {
            return n.ToString("0", c) + " mm";
        }

        if (key.EndsWith("Percent", StringComparison.Ordinal))
        {
            return n.ToString("0.#", c) + " %";
        }

        if (key.EndsWith("BitsPerSample", StringComparison.Ordinal))
        {
            return n.ToString("0", c) + " bit";
        }

        if (key.EndsWith("UptimeSec", StringComparison.OrdinalIgnoreCase))
        {
            return Fmt.Uptime((long)n);
        }

        return n.ToString("0.##", c);
    }

    private static readonly HashSet<string> EnumKeys = new(StringComparer.Ordinal) { "type", "bus", "kind", "connection", "direction" };

    private static readonly HashSet<string> EnumValues = new(StringComparer.Ordinal)
    {
        "ddr3", "ddr4", "ddr5", "lpddr4", "lpddr5", "nvme", "sata", "usb", "raid", "scsi", "ethernet", "wifi", "virtual", "loopback",
        "hdmi", "displayPort", "dvi", "vga", "internal", "output", "input", "other", "unknown",
    };

    private static string EnumText(string value) => EnumValues.Contains(value) ? Strings.Get("gui.hw.v." + value) : value;

    /// <summary>The keys that have a label in the string table. Any other key is shown as its own name, spaced out.</summary>
    internal static readonly HashSet<string> LabelKeys = new(HardwareStrings.FieldKeys, StringComparer.Ordinal);

    /// <summary>"biosVersion" becomes "Bios Version". A field this version has no label for still shows up, readable.</summary>
    internal static string Humanize(string key)
    {
        if (key.Length == 0)
        {
            return key;
        }

        var sb = new StringBuilder(key.Length + 4);
        sb.Append(char.ToUpperInvariant(key[0]));
        for (var i = 1; i < key.Length; i++)
        {
            if (char.IsUpper(key[i]) && !char.IsUpper(key[i - 1]))
            {
                sb.Append(' ');
            }

            sb.Append(key[i]);
        }

        return sb.ToString();
    }
}
