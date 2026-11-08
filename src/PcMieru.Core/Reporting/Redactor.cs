using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PcMieru.Core.Reporting;

/// <summary>Who to hide in text: user name, PC name and the user profile folder.</summary>
public sealed record RedactionContext(string UserName, string MachineName, string UserProfile)
{
    public static RedactionContext FromEnvironment() =>
        new(Environment.UserName, Environment.MachineName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}

/// <summary>
/// The one redactor (SPEC section 9): user name to &lt;user&gt;, the user folder to %USERPROFILE%, the PC name to
/// &lt;host&gt;, serial/MAC/IP/DNS/SSID hidden, command lines never shown. It works in two ways at once, because a
/// secret is either under a known key or somewhere inside a sentence:
/// by key name on the JSON tree (commandLine, serialNumber, macAddress, ipAddresses, dnsServers, ssid and friends) and by
/// content on every string (profile paths in all spellings, UNC servers, MAC, IPv4/IPv6, device instance IDs, the user
/// and PC name as whole words). It is applied to the final JSON tree or text right before output, so error texts and
/// device IDs are covered too. A few free-text fields that a person typed (a volume label, an audio endpoint name, a
/// USB product string) are blanked by their place in the tree, not by a global key: "label" and "name" mean other
/// things elsewhere. <see cref="Enabled"/> is fixed when the process starts (--no-redact); the per-call overloads of
/// <see cref="IRedactor"/> can only switch redaction off for one call, never on when the process turned it off.
/// </summary>
public sealed partial class SimpleRedactor : IRedactor
{
    public const string Placeholder = "<redacted>";

    /// <summary>Under these keys the whole value is replaced (a Value&lt;T&gt; keeps its status, loses its value).</summary>
    private static readonly HashSet<string> BlankKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "commandLine", "serialNumber", "macAddress", "ipAddresses", "dnsServers", "ssid",
        "bssid", "uuid", "machineGuid", "productKey",
    };

    /// <summary>
    /// Blanked only at this place in the tree. A path is the dotted key names from the root; an array adds "[]" and a
    /// Value&lt;T&gt; wrapper's own "value" is not a segment, so <c>audio[].name</c> is the friendly name of every audio
    /// endpoint however deep the hardware tree sits in a report. The match is on the end of the path.
    /// These are names that a person, a pairing wizard or a monitor's own EDID carries: a drive label ("Tatsuki's backup"),
    /// the endpoint name Windows builds from a Bluetooth device ("Headphones (Tatsuki's AirPods)"), a USB product string,
    /// a network adapter the owner renamed ("Tatsuki Wi-Fi"), the monitor name a person set in its OSD, and the audio
    /// device's adapter name. "label", "name" and "adapter" mean other things elsewhere, so each is blanked by its place.
    /// </summary>
    public static readonly IReadOnlyList<string> BlankedPaths =
        ["volumes[].label", "network[].name", "displays[].details.monitorName", "audio[].name", "audio[].details.adapter", "usb[].name"];

    /// <summary>
    /// Version-like fields: a four-part number such as 1.2.3.4 is a firmware version here, not an IPv4 address,
    /// so the IPv4 mask is not applied to them. Everything else about them is redacted as usual.
    /// </summary>
    private static readonly HashSet<string> VersionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "version", "build", "driverVersion", "biosVersion", "tpmVersion", "firmware", "revision", "vbios", "schemaVersion",
        "protocolVersion", "driverDate", "biosDate",
    };

    private static readonly HashSet<string> SharedProfileFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Public", "Default", "Default User", "All Users", "desktop.ini",
    };

    private readonly RedactionContext _context;
    private readonly Regex? _userRegex;
    private readonly Regex? _hostRegex;
    private readonly Regex? _shortNameRegex;
    private readonly Regex? _profileRegex;

    public SimpleRedactor(RedactionContext context, bool enabled)
    {
        _context = context;
        Enabled = enabled;
        _userRegex = WholeWord(context.UserName);
        _hostRegex = WholeWord(context.MachineName);
        _shortNameRegex = ShortNameRegex(context.UserName);
        _profileRegex = ProfileRegex(context.UserProfile);
    }

    public bool Enabled { get; }

    public string RedactText(string text) => Enabled ? Redact(text, hideIpv4: true) : text;

    public JsonNode? RedactJson(JsonNode? node)
    {
        if (Enabled && node is not null)
        {
            Walk(node, hideIpv4: true, string.Empty);
        }

        return node;
    }

    /// <summary>True when a key at this tree path is one of <see cref="BlankedPaths"/>.</summary>
    internal static bool IsBlankedPath(string path)
    {
        foreach (var blanked in BlankedPaths)
        {
            if (path.Equals(blanked, StringComparison.OrdinalIgnoreCase)
                || (path.Length > blanked.Length && path.EndsWith(blanked, StringComparison.OrdinalIgnoreCase) && path[path.Length - blanked.Length - 1] == '.'))
            {
                return true;
            }
        }

        return false;
    }

    private string Redact(string text, bool hideIpv4)
    {
        if (text.Length == 0)
        {
            return text;
        }

        // Paths first: once the folder is %USERPROFILE%, no later rule can see the name inside it.
        if (_profileRegex is not null)
        {
            text = _profileRegex.Replace(text, "%USERPROFILE%");
        }

        text = OtherProfileRegex().Replace(text, static m => SharedProfileFolders.Contains(m.Groups[2].Value) ? m.Value : m.Groups[1].Value + "<user>");
        if (_shortNameRegex is not null)
        {
            text = _shortNameRegex.Replace(text, "<user>");
        }

        text = UncRegex().Replace(text, @"\\<host>\");
        text = DeviceInstanceRegex().Replace(text, static m => IsWindowsMadeInstance(m.Groups[3].Value) ? m.Value : m.Groups[1].Value + "\\" + m.Groups[2].Value + "\\<id>");
        text = BluetoothAddressRegex().Replace(text, "$1<mac>");
        text = MacRegex().Replace(text, "<mac>");
        text = Ipv6Regex().Replace(text, "<ip>");
        if (hideIpv4)
        {
            text = Ipv4Regex().Replace(text, "<ip>");
        }

        if (_hostRegex is not null)
        {
            text = _hostRegex.Replace(text, "<host>");
        }

        if (_userRegex is not null)
        {
            text = _userRegex.Replace(text, "<user>");
        }

        return text;
    }

    private void Walk(JsonNode node, bool hideIpv4, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                var isValue = obj.ContainsKey("status");
                foreach (var (name, child) in obj.ToList())
                {
                    if (child is null)
                    {
                        continue;
                    }

                    // The "value" of a Value<T> is the same place as the Value<T> itself, not one level down.
                    var childPath = isValue && name == "value" ? path : path.Length == 0 ? name : path + "." + name;
                    if (BlankKeys.Contains(name) || IsBlankedPath(childPath))
                    {
                        if (child is JsonObject valueObject && valueObject.ContainsKey("status"))
                        {
                            if (valueObject.ContainsKey("value"))
                            {
                                valueObject["value"] = Placeholder;
                            }
                        }
                        else if (child is not JsonValue plain || !plain.TryGetValue<string>(out var text) || text.Length > 0)
                        {
                            // An empty label stays empty: "no label" is a fact, not a secret.
                            obj[name] = Placeholder;
                        }
                    }
                    else if (child is JsonValue value && value.TryGetValue<string>(out var s))
                    {
                        obj[name] = Redact(s, hideIpv4 && !VersionKeys.Contains(name));
                    }
                    else
                    {
                        Walk(child, hideIpv4 && !VersionKeys.Contains(name), childPath);
                    }
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var child = array[i];
                    if (child is JsonValue value && value.TryGetValue<string>(out var s))
                    {
                        array[i] = Redact(s, hideIpv4);
                    }
                    else if (child is not null)
                    {
                        Walk(child, hideIpv4, path + "[]");
                    }
                }

                break;
        }
    }

    /// <summary>
    /// A name is replaced as a whole word only when it is 3 or more characters long. A one-letter user or a PC called
    /// "PC" would shred every sentence ("PC Mieru"); such names are still hidden inside paths and UNC names.
    /// </summary>
    private static Regex? WholeWord(string name) =>
        name.Length >= 3
            ? new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(name) + @"(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            : null;

    /// <summary>
    /// The user folder in every spelling: either slash, a \\?\ prefix, any case. Matches only up to the end of the
    /// folder name, so a longer sibling ("C:\Users\alice2") is not cut in half.
    /// </summary>
    private static Regex? ProfileRegex(string profile)
    {
        profile = profile.Trim().TrimEnd('\\', '/');
        if (profile.Length == 0)
        {
            return null;
        }

        var pattern = Regex.Escape(profile).Replace(@"\\", @"[\\/]", StringComparison.Ordinal);
        return new Regex(@"(?:\\\\\?\\)?" + pattern + @"(?![\p{L}\p{N}_~])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// The 8.3 short form of a long user folder name ("Administrator" is "ADMINI~1"). Windows builds it from the first
    /// six characters plus ~N, and that spelling shows up in %TEMP%.
    /// </summary>
    private static Regex? ShortNameRegex(string user)
    {
        var letters = new string(user.Where(c => c is not (' ' or '.')).ToArray());
        if (letters.Length < 3 || (user.Length <= 8 && !user.Contains(' ', StringComparison.Ordinal) && !user.Contains('.', StringComparison.Ordinal)))
        {
            return null;
        }

        var prefix = letters[..Math.Min(6, letters.Length)];
        return new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(prefix) + @"~\d(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>A device instance such as 5&amp;2a1b3c4d&amp;0&amp;2 is made by Windows; a real serial number is anything else.</summary>
    private static bool IsWindowsMadeInstance(string instance) => WindowsInstanceRegex().IsMatch(instance);

    /// <summary>Another account's folder (X:\Users\bob\...) hides the name, but the shared folders stay readable.</summary>
    [GeneratedRegex(@"(\b[A-Za-z]:[\\/]Users[\\/])([^\\/\s""':*?<>|%]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OtherProfileRegex();

    /// <summary>\\server\share (not the \\?\ and \\.\ device prefixes).</summary>
    [GeneratedRegex(@"\\\\(?![?.]\\)[^\\/\s""'?*<>|]+\\")]
    private static partial Regex UncRegex();

    [GeneratedRegex(@"\b[0-9A-Fa-f]{2}([:-])[0-9A-Fa-f]{2}(\1[0-9A-Fa-f]{2}){4}\b")]
    private static partial Regex MacRegex();

    [GeneratedRegex(@"(?<![\w.])(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?:\.(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}(?![\w]|\.\d)")]
    private static partial Regex Ipv4Regex();

    [GeneratedRegex(@"(?<![\w:.])(?:(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:){1,7}:(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4}){0,5})?|:(?::[0-9A-Fa-f]{1,4}){1,7})(?:%\w+)?(?![\w:])")]
    private static partial Regex Ipv6Regex();

    /// <summary>USB\VID_xxxx&amp;PID_xxxx\instance and friends: the last part is the device's own serial number when it has one.</summary>
    [GeneratedRegex(@"\b(USB|USBSTOR|BTHENUM|BTHLE|HID|SCSI|IDE|NVME|SWD)\\([^\\\s""'<>|]+)\\([^\\\s""'<>|]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceInstanceRegex();

    [GeneratedRegex(@"^\d+&[0-9a-f]+&\d+&[0-9a-f]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsInstanceRegex();

    /// <summary>Bluetooth device IDs embed the device's MAC address (DEV_AABBCCDDEEFF).</summary>
    [GeneratedRegex(@"(DEV_|BLUETOOTHDEVICE_)[0-9A-Fa-f]{12}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BluetoothAddressRegex();
}
