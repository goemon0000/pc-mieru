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

    public string Usage => "pcmi hw [all|cpu|memory|board|gpu|storage|network|display|audio|usb|battery|os] [--json]";

    public IReadOnlyList<OptionSpec> Options { get; } = [new("json")];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.hw", "PC の部品情報（種別を指定可）", "PC hardware information (optionally one category)"),
        new("cli.hw.item", "{category} {number}", "{category} {number}"),
        new("cli.hw.empty", "なし", "none"),
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
            context.Out.Write(Render(context, info));
        }

        return ExitCode.Success;
    }

    /// <summary>
    /// One section per category that was collected: plain-language names, sizes and speeds in units, a value that
    /// could not be read says why. A field the label table does not know yet still appears under its own name.
    /// </summary>
    internal static string Render(CliContext context, HardwareInfo info)
    {
        var sb = new StringBuilder();
        foreach (var (key, node) in PcMieruJson.ToNode(info)!.AsObject())
        {
            if (node is not JsonObject section || !IsValue(section) || section["status"]?.GetValue<string>() == "notCollected")
            {
                continue;
            }

            var category = context.Text("hw.category." + CategoryKey(key));
            sb.Append('[').Append(category).Append("]\n");
            if (section["value"] is { } value)
            {
                WriteBody(context, sb, value, category, 1);
            }
            else
            {
                sb.Append("  ").Append(Strings.Status(ParseStatus(section["status"]?.GetValue<string>()), context.Language)).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static void WriteBody(CliContext context, StringBuilder sb, JsonNode node, string name, int depth)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (field, child) in obj)
                {
                    WriteField(context, sb, field, child, depth);
                }

                break;
            case JsonArray array:
                WriteItems(context, sb, array, name, depth);
                break;
        }
    }

    private static void WriteItems(CliContext context, StringBuilder sb, JsonArray array, string name, int depth)
    {
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonObject or JsonArray)
            {
                Indent(sb, depth).Append(context.Format("cli.hw.item", ("category", name), ("number", (i + 1).ToString(CultureInfo.InvariantCulture)))).Append('\n');
                WriteBody(context, sb, array[i]!, name, depth + 1);
            }
            else
            {
                Line(context, sb, name, array[i], depth);
            }
        }
    }

    private static void WriteField(CliContext context, StringBuilder sb, string field, JsonNode? node, int depth)
    {
        var label = CliLabels.Label(field, context.Language);
        switch (node)
        {
            case JsonObject obj when IsValue(obj):
                if (obj["value"] is not { } inner)
                {
                    Indent(sb, depth).Append(CliText.Printable(label)).Append(": ").Append(Strings.Status(ParseStatus(obj["status"]?.GetValue<string>()), context.Language)).Append('\n');
                }
                else if (IsStructured(inner))
                {
                    WriteStructured(context, sb, inner, label, depth);
                }
                else
                {
                    Line(context, sb, field, inner, depth);
                }

                break;
            case JsonObject:
            case JsonArray array when IsStructured(array):
                WriteStructured(context, sb, node, label, depth);
                break;
            case JsonArray array when array.Count == 0:
                Indent(sb, depth).Append(CliText.Printable(label)).Append(": ").Append(context.Text("cli.hw.empty")).Append('\n');
                break;
            default:
                Line(context, sb, field, node, depth);
                break;
        }
    }

    /// <summary>An object gets a heading line. An array of objects is already titled item by item ("Volume 1"), so it gets none.</summary>
    private static void WriteStructured(CliContext context, StringBuilder sb, JsonNode node, string label, int depth)
    {
        if (node is JsonArray array)
        {
            WriteItems(context, sb, array, label, depth);
            return;
        }

        Indent(sb, depth).Append(CliText.Printable(label)).Append('\n');
        WriteBody(context, sb, node, label, depth + 1);
    }

    private static bool IsValue(JsonObject o) => o.ContainsKey("status") && o.ContainsKey("source");

    private static bool IsStructured(JsonNode node) =>
        node is JsonObject || (node is JsonArray array && array.Any(a => a is JsonObject or JsonArray));

    private static void Line(CliContext context, StringBuilder sb, string field, JsonNode? node, int depth)
    {
        var label = CliLabels.Label(field, context.Language);
        Indent(sb, depth).Append(CliText.Printable(label)).Append(": ").Append(CliText.Printable(Scalar(context, field, node))).Append('\n');
    }

    private static StringBuilder Indent(StringBuilder sb, int depth) => sb.Append(' ', depth * 2);

    private static string Scalar(CliContext context, string field, JsonNode? node)
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
                        return WithUnit(context, field, double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture));
                    case JsonValueKind.String:
                        var text = value.GetValue<string>();
                        return EnumSpelling.GetValueOrDefault(text, text);
                }

                return value.ToString();
            default:
                return node?.ToString() ?? string.Empty;
        }
    }

    /// <summary>The unit comes from the field name (the model names every unit: Bytes, Mhz, MtPerSec, Watts ...).</summary>
    private static string WithUnit(CliContext context, string field, double number)
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

        if (field.EndsWith("Sec", StringComparison.Ordinal))
        {
            return CliFormat.Duration(context, (long)number);
        }

        return CliText.Num(number, "0.##");
    }

    private static ValueStatus ParseStatus(string? name) =>
        Enum.TryParse<ValueStatus>(name, ignoreCase: true, out var status) ? status : ValueStatus.Error;

    private static string CategoryKey(string property) => property switch
    {
        "gpus" => "gpu",
        "displays" => "display",
        _ => property,
    };
}
