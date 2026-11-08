using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PcMieru.Core;

namespace PcMieru.Cli;

/// <summary>
/// Generic "path: value" text for any contract record, so a field added later shows up in the human output without
/// editing the command. A Value&lt;T&gt; collapses to its value, or to the localized status when it has none.
/// </summary>
public static class TreeText
{
    public static bool IsValue(JsonObject o) => o.ContainsKey("status") && o.ContainsKey("source");

    public static string Render(JsonNode? node, Language language)
    {
        var sb = new StringBuilder();
        Write(sb, string.Empty, node, language);
        return sb.ToString();
    }

    public static void Write(StringBuilder sb, string path, JsonNode? node, Language language)
    {
        switch (node)
        {
            case JsonObject obj when IsValue(obj):
                if (obj.TryGetPropertyValue("value", out var inner) && inner is not null)
                {
                    Write(sb, path, inner, language);
                }
                else
                {
                    Line(sb, path, Strings.Status(ParseStatus(obj["status"]?.GetValue<string>()), language));
                }

                break;
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    Write(sb, path.Length == 0 ? key : path + "." + key, child, language);
                }

                break;
            case JsonArray array when array.All(a => a is JsonValue):
                Line(sb, path, string.Join(", ", array.Select(a => a?.ToString() ?? string.Empty)));
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    Write(sb, path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", array[i], language);
                }

                break;
            case JsonValue value:
                Line(sb, path, value.ToString());
                break;
        }
    }

    private static ValueStatus ParseStatus(string? name) =>
        Enum.TryParse<ValueStatus>(name, ignoreCase: true, out var status) ? status : ValueStatus.Error;

    private static void Line(StringBuilder sb, string path, string value) =>
        sb.Append("  ").Append(CliText.Printable(path)).Append(": ").Append(CliText.Printable(value)).Append('\n');
}
