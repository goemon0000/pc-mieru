using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Cli.Commands;

public sealed class HwCommand : ICliCommand
{
    // Enum values that read better in their usual spelling than as the lowercase JSON name.
    private static readonly Dictionary<string, string> EnumSpelling = new(StringComparer.Ordinal)
    {
        ["nvme"] = "NVMe",
        ["sata"] = "SATA",
        ["usb"] = "USB",
        ["raid"] = "RAID",
        ["scsi"] = "SCSI",
        ["ddr3"] = "DDR3",
        ["ddr4"] = "DDR4",
        ["ddr5"] = "DDR5",
        ["lpddr4"] = "LPDDR4",
        ["lpddr5"] = "LPDDR5",
        ["wifi"] = "Wi-Fi",
        ["hdmi"] = "HDMI",
        ["displayPort"] = "DisplayPort",
        ["dvi"] = "DVI",
        ["vga"] = "VGA",
    };

    public string Name => "hw";

    public string Usage => "pcmi hw [all|cpu|memory|board|gpu|storage|network|display|audio|usb|battery|os] [--details] [--json]";

    public IReadOnlyList<OptionSpec> Options { get; } = [new("json"), new("details")];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.hw", "PC の部品情報（種別を指定可。--details で細かい項目も）", "PC hardware information (optionally one category; --details adds the fine print)"),
        new("cli.hw.item", "{category} {number}", "{category} {number}"),
        new("cli.hw.empty", "なし", "none"),
        new("cli.hw.emptySlot", "{item}: {slot}（空きスロット）", "{item}: {slot} (empty slot)"),
        new("cli.hw.moreDetails", "細かい項目は --details を付けると表示します（--json にはすべて含まれます）", "Add --details to show the fine print (--json always includes everything)"),
        new("cli.hw.unit.inch", "インチ", "inch"),
    ];

    public async Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(0, 1);
        var category = args.Positionals.Count == 0
            ? HardwareCategory.All
            : ParsedArgs.ParseEnum<HardwareCategory>(args.Positionals[0], "the category");
        var info = await context.Services().Insights.GetHardwareAsync(category, context.Ct).ConfigureAwait(false);
        if (args.Has("json"))
        {
            context.Out.WriteLine(PcMieruJson.Serialize(info));
        }
        else
        {
            context.Out.Write(Render(context, info, args.Has("details")));
        }

        return ExitCode.Success;
    }

    /// <summary>
    /// One section per category that was collected: plain-language names, sizes and speeds in units, a value that
    /// could not be read says why. A field the label table does not know yet still appears under its own name.
    /// The "details" blocks (cache levels, partitions, EDID ...) are left out unless <paramref name="details"/> is set.
    /// </summary>
    internal static string Render(CliContext context, HardwareInfo info, bool details = false) =>
        Render(context, PcMieruJson.ToNode(info)!.AsObject(), details);

    /// <summary>The same, from the JSON form of a <see cref="HardwareInfo"/> (the text view is built from what --json prints).</summary>
    internal static string Render(CliContext context, JsonObject info, bool details)
    {
        var writer = new Writer(context, details);
        foreach (var (key, node) in info)
        {
            if (node is not JsonObject section || !Writer.IsValue(section) || section["status"]?.GetValue<string>() == "notCollected")
            {
                continue;
            }

            var category = context.Text("hw.category." + CategoryKey(key));
            writer.Sb.Append('[').Append(category).Append("]\n");
            if (section["value"] is { } value)
            {
                writer.WriteBody(value, category, 1);
            }
            else
            {
                writer.Sb.Append("  ").Append(Strings.Status(Writer.ParseStatus(section["status"]?.GetValue<string>()), context.Language)).Append('\n');
            }
        }

        if (writer.HiddenDetails)
        {
            writer.Sb.Append('\n').Append(context.Text("cli.hw.moreDetails")).Append('\n');
        }

        return writer.Sb.ToString();
    }

    private static string CategoryKey(string property) => property switch
    {
        "gpus" => "gpu",
        "displays" => "display",
        _ => property,
    };

    private sealed class Writer(CliContext context, bool showDetails)
    {
        public StringBuilder Sb { get; } = new();

        public bool HiddenDetails { get; private set; }

        public static bool IsValue(JsonObject o) => o.ContainsKey("status") && o.ContainsKey("source");

        public static ValueStatus ParseStatus(string? name) =>
            Enum.TryParse<ValueStatus>(name, ignoreCase: true, out var status) ? status : ValueStatus.Error;

        public void WriteBody(JsonNode node, string name, int depth)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (field, child) in obj)
                    {
                        WriteField(field, child, depth);
                    }

                    break;
                case JsonArray array:
                    WriteItems(array, name, depth);
                    break;
            }
        }

        private void WriteItems(JsonArray array, string name, int depth)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonObject or JsonArray)
                {
                    var title = context.Format("cli.hw.item", ("category", name), ("number", (i + 1).ToString(CultureInfo.InvariantCulture)));
                    if (array[i] is JsonObject item && EmptySlot(item) is { } slot)
                    {
                        Indent(depth).Append(context.Format("cli.hw.emptySlot", ("item", title), ("slot", slot))).Append('\n');
                        continue;
                    }

                    Indent(depth).Append(title).Append('\n');
                    WriteBody(array[i]!, name, depth + 1);
                }
                else
                {
                    Line(name, array[i], depth);
                }
            }
        }

        /// <summary>A memory module whose capacity was read as exactly 0 is an empty socket, not a module with unknown properties.</summary>
        private static string? EmptySlot(JsonObject item)
        {
            // Both fields are Value<T> in the model, so each is a { status, value } object in the JSON form.
            if (Unwrap(item["capacityBytes"]) is JsonValue { } capacity
                && capacity.GetValueKind() == JsonValueKind.Number
                && double.Parse(capacity.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture) == 0
                && Unwrap(item["slot"]) is JsonValue { } slot
                && slot.GetValueKind() == JsonValueKind.String)
            {
                return slot.GetValue<string>();
            }

            return null;
        }

        /// <summary>The payload of a Value&lt;T&gt; object; a bare node is returned as it is. A value that was not read has no payload.</summary>
        private static JsonNode? Unwrap(JsonNode? node) =>
            node is JsonObject obj && IsValue(obj) ? obj["value"] : node;

        private void WriteField(string field, JsonNode? node, int depth)
        {
            if (field == "details" && !showDetails)
            {
                HiddenDetails = true;
                return;
            }

            var label = CliLabels.Label(field, context.Language);
            switch (node)
            {
                case JsonObject obj when IsValue(obj):
                    if (obj["value"] is not { } inner)
                    {
                        Indent(depth).Append(CliText.Printable(label)).Append(": ").Append(Strings.Status(ParseStatus(obj["status"]?.GetValue<string>()), context.Language)).Append('\n');
                    }
                    else if (IsStructured(inner))
                    {
                        WriteStructured(inner, label, depth);
                    }
                    else
                    {
                        Line(field, inner, depth);
                    }

                    break;
                case JsonObject:
                case JsonArray array when IsStructured(array):
                    WriteStructured(node, label, depth);
                    break;
                case JsonArray array when array.Count == 0:
                    Indent(depth).Append(CliText.Printable(label)).Append(": ").Append(context.Text("cli.hw.empty")).Append('\n');
                    break;
                default:
                    Line(field, node, depth);
                    break;
            }
        }

        /// <summary>An object gets a heading line. An array of objects is already titled item by item ("Volume 1"), so it gets none.</summary>
        private void WriteStructured(JsonNode node, string label, int depth)
        {
            if (node is JsonArray array)
            {
                WriteItems(array, label, depth);
                return;
            }

            Indent(depth).Append(CliText.Printable(label)).Append('\n');
            WriteBody(node, label, depth + 1);
        }

        private static bool IsStructured(JsonNode node) =>
            node is JsonObject || (node is JsonArray array && array.Any(a => a is JsonObject or JsonArray));

        private void Line(string field, JsonNode? node, int depth)
        {
            var label = CliLabels.Label(field, context.Language);
            Indent(depth).Append(CliText.Printable(label)).Append(": ").Append(CliText.Printable(Scalar(field, node))).Append('\n');
        }

        private StringBuilder Indent(int depth) => Sb.Append(' ', depth * 2);

        private string Scalar(string field, JsonNode? node)
        {
            switch (node)
            {
                case JsonArray array:
                    return array.Count == 0 ? context.Text("cli.hw.empty") : string.Join(", ", array.Select(a => a?.ToString() ?? string.Empty));
                case JsonValue value:
                    switch (value.GetValueKind())
                    {
                        case JsonValueKind.True:
                            return CliFormat.YesNo(context, true);
                        case JsonValueKind.False:
                            return CliFormat.YesNo(context, false);
                        case JsonValueKind.Number:
                            return WithUnit(field, double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture));
                        case JsonValueKind.String:
                            var text = value.GetValue<string>();
                            if (text.Length == 0)
                            {
                                return context.Text("cli.hw.empty");
                            }

                            return EnumSpelling.GetValueOrDefault(text) ?? CliLabels.Word(text, context.Language) ?? text;
                    }

                    return value.ToString();
                default:
                    return node?.ToString() ?? string.Empty;
            }
        }

        /// <summary>The unit comes from the field name (the model names every unit: Bytes, Mhz, MtPerSec, Watts ...).</summary>
        private string WithUnit(string field, double number)
        {
            if (field.EndsWith("BytesPerSec", StringComparison.Ordinal))
            {
                return CliFormat.Rate(number);
            }

            if (field.EndsWith("Bytes", StringComparison.Ordinal))
            {
                return CliFormat.Bytes(number);
            }

            if (field.EndsWith("BitsPerSec", StringComparison.Ordinal))
            {
                return CliFormat.LinkSpeed((long)number);
            }

            if (field.EndsWith("MtPerSec", StringComparison.Ordinal))
            {
                return CliText.Num(number, "0") + " MT/s";
            }

            if (field.EndsWith("Mhz", StringComparison.Ordinal))
            {
                return CliFormat.Mhz(number);
            }

            if (field.EndsWith("Millivolts", StringComparison.Ordinal))
            {
                return CliText.Num(number / 1000.0, "0.0##") + " V";
            }

            if (field.EndsWith("MilliwattHours", StringComparison.Ordinal))
            {
                return CliText.Num(number / 1000.0, "0.0") + " Wh";
            }

            if (field.EndsWith("Watts", StringComparison.Ordinal))
            {
                return CliText.Num(number, "0.#") + " W";
            }

            if (field.EndsWith("Percent", StringComparison.Ordinal))
            {
                return CliFormat.Percent(number);
            }

            if (field.EndsWith("Hz", StringComparison.Ordinal))
            {
                return CliText.Num(number, "0.##") + " Hz";
            }

            if (field.EndsWith("Mm", StringComparison.Ordinal))
            {
                return CliText.Num(number, "0") + " mm";
            }

            if (field.EndsWith("Inches", StringComparison.Ordinal))
            {
                return CliText.Num(number, "0.#") + " " + context.Text("cli.hw.unit.inch");
            }

            if (field.EndsWith("Bits", StringComparison.Ordinal))
            {
                return CliText.Num(number, "0") + " bit";
            }

            if (field.EndsWith("Nits", StringComparison.Ordinal))
            {
                return CliText.Num(number, "0") + " nit";
            }

            if (field.EndsWith("Rpm", StringComparison.Ordinal))
            {
                return CliText.Num(number, "0") + " rpm";
            }

            if (field.EndsWith("Minutes", StringComparison.Ordinal))
            {
                return CliFormat.Duration(context, (long)(number * 60));
            }

            if (field.EndsWith("Sec", StringComparison.Ordinal))
            {
                return CliFormat.Duration(context, (long)number);
            }

            return CliText.Num(number, "0.##");
        }
    }
}
