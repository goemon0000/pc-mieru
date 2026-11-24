using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PcMieru.Update.Feed;
using PcMieru.Update.Transaction;

namespace PcMieru.Gui;

/// <summary>The runner's last result is only a message for the settings page. The runner owns the file.</summary>
internal sealed record UpdateResultNote(string Key, string Stamp, string Message, string To, string? From)
{
    public string Text => Strings.Format("gui.update.result." + Message, Fmt.Dict(("to", To), ("from", From ?? "")));

    public static UpdateResultNote? Read(string install)
    {
        var paths = new TxPaths(install);
        try
        {
            using var file = new FileStream(paths.LastResult, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length is 0 or > 65_536)
            {
                return null;
            }

            var bytes = new byte[file.Length];
            file.ReadExactly(bytes);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var utc = StringAt(root, "utc");
            var tx = StringAt(root, "tx");
            if (StringAt(root, "format") != LastResult.Format
                || !DateTimeOffset.TryParseExact(utc, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)
                || !IsHex(tx, 12) || Generation(root, "to") is not { } to)
            {
                return null;
            }

            var key = "update.result." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(paths.Install.ToUpperInvariant())));
            var stamp = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (StringAt(root, "outcome") == "committed" && StringAt(root, "reason") == "none")
            {
                return new UpdateResultNote(key, stamp, "committed", to.Version, null);
            }

            if (StringAt(root, "outcome") != "rolledBack" || Generation(root, "from") is not { } from
                || !Enum.GetValues<TxReason>().Any(reason => reason != TxReason.None && LastResult.Camel(reason.ToString()) == StringAt(root, "reason")))
            {
                return null;
            }

            var blocked = BlockedTags.Read(paths.Blocked).Any(tag => tag.Version == to.Version && tag.BuildId == to.Build);
            return new UpdateResultNote(key, stamp, blocked ? "blocked" : "retry", to.Version, from.Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? StringAt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static (string Version, string Build)? Generation(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var generation))
        {
            return null;
        }

        var version = StringAt(generation, "version");
        var build = StringAt(generation, "buildId");
        return AppVersion.TryParse(version, out var parsed) && version == parsed.ToString()
            && Guid.TryParseExact(build, "D", out var id) && build == id.ToString("D")
            ? (version!, build!) : null;
    }

    private static bool IsHex(string? text, int length) =>
        text?.Length == length && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
