using System.Text.Json.Nodes;
using PcMieru.Core.Reporting;

namespace PcMieru.Cli.Tests.Reporting;

public class RedactorTests
{
    private static readonly SimpleRedactor On = new(new RedactionContext("alice", "ALICE-PC", @"C:\Users\alice"), enabled: true);

    private static readonly SimpleRedactor Off = new(new RedactionContext("alice", "ALICE-PC", @"C:\Users\alice"), enabled: false);

    [Theory]
    // the user folder, in every spelling
    [InlineData(@"C:\Users\alice\Documents\a.txt", @"%USERPROFILE%\Documents\a.txt")]
    [InlineData(@"c:\users\ALICE\Documents", @"%USERPROFILE%\Documents")]
    [InlineData("c:/users/alice/Desktop", "%USERPROFILE%/Desktop")]
    [InlineData(@"\\?\C:\Users\alice\x", @"%USERPROFILE%\x")]
    [InlineData(@"opened C:\Users\alice and C:\Users\alice\y", @"opened %USERPROFILE% and %USERPROFILE%\y")]
    // other accounts, but not the shared folders
    [InlineData(@"C:\Users\alice2\x", @"C:\Users\<user>\x")]
    [InlineData(@"D:\Users\bob\AppData", @"D:\Users\<user>\AppData")]
    [InlineData(@"C:\Users\Public\Documents", @"C:\Users\Public\Documents")]
    [InlineData(@"C:\Users\Default\NTUSER.DAT", @"C:\Users\Default\NTUSER.DAT")]
    // network names; the device prefixes are not servers
    [InlineData(@"\\ALICE-PC\share\f", @"\\<host>\share\f")]
    [InlineData(@"\\nas01\media\movie.mkv", @"\\<host>\media\movie.mkv")]
    [InlineData(@"\\?\C:\Windows\System32", @"\\?\C:\Windows\System32")]
    [InlineData(@"\\.\PhysicalDrive0", @"\\.\PhysicalDrive0")]
    // addresses
    [InlineData("MAC 00:1A:2B:3C:4D:5E", "MAC <mac>")]
    [InlineData("mac 00-1a-2b-3c-4d-5e up", "mac <mac> up")]
    [InlineData("ip 192.168.1.20 and 10.0.0.1.", "ip <ip> and <ip>.")]
    [InlineData("v6 fe80::1ff:fe23:4567:890a%12 end", "v6 <ip> end")]
    [InlineData("v6 2001:db8:85a3:0:0:8a2e:370:7334", "v6 <ip>")]
    // device instance ids: Windows-made ones stay, a device's own serial goes
    [InlineData(@"USB\VID_046D&PID_C52B\5&2a1b3c4d&0&2", @"USB\VID_046D&PID_C52B\5&2a1b3c4d&0&2")]
    [InlineData(@"USB\VID_046D&PID_C534\1234567890AB", @"USB\VID_046D&PID_C534\<id>")]
    [InlineData(@"BTHENUM\DEV_AABBCCDDEEFF\7&1&0&BluetoothDevice_AABBCCDDEEFF", @"BTHENUM\DEV_<mac>\<id>")]
    // the user and PC name as whole words only
    [InlineData("user alice logged on ALICE-PC", "user <user> logged on <host>")]
    [InlineData("Alice's files", "<user>'s files")]
    [InlineData("alicenotuser and malice", "alicenotuser and malice")]
    [InlineData("PC Mieru 1.0", "PC Mieru 1.0")]
    [InlineData("", "")]
    public void Text_is_redacted_by_content(string input, string expected)
    {
        Assert.Equal(expected, On.RedactText(input));
    }

    [Fact]
    public void A_long_user_folder_is_also_hidden_in_its_8_3_short_form()
    {
        var redactor = new SimpleRedactor(new RedactionContext("Administrator", "WIN11", @"C:\Users\Administrator"), true);
        Assert.Equal(@"C:\Users\<user>\AppData\Local\Temp", redactor.RedactText(@"C:\Users\ADMINI~1\AppData\Local\Temp"));
        Assert.Equal("temp is <user>", redactor.RedactText("temp is ADMINI~1"));
        Assert.Equal("%USERPROFILE%\\x", redactor.RedactText(@"C:\Users\Administrator\x"));
    }

    [Fact]
    public void A_name_shorter_than_three_characters_is_not_replaced_inside_sentences()
    {
        var redactor = new SimpleRedactor(new RedactionContext("pc", "PC", @"C:\Users\pc"), true);
        Assert.Equal("PC Mieru reads pc data", redactor.RedactText("PC Mieru reads pc data"));
        Assert.Equal(@"%USERPROFILE%\a", redactor.RedactText(@"C:\Users\pc\a"));
    }

    [Fact]
    public void Redaction_off_changes_nothing()
    {
        const string text = @"C:\Users\alice\x 192.168.1.2 00:1A:2B:3C:4D:5E alice ALICE-PC";
        Assert.False(Off.Enabled);
        Assert.Equal(text, Off.RedactText(text));
        var node = JsonNode.Parse("""{"commandLine":"x --token abc","serialNumber":"S123"}""");
        Assert.Equal("x --token abc", Off.RedactJson(node)!["commandLine"]!.GetValue<string>());
    }

    // ---- by key name, on the JSON tree ---------------------------------------------------------------------------

    [Fact]
    public void Secrets_under_known_keys_are_blanked_wherever_they_sit()
    {
        var node = JsonNode.Parse("""
            {
              "commandLine": "tool.exe --password hunter2",
              "serialNumber": "SN-123",
              "macAddress": "00:1A:2B:3C:4D:5E",
              "ipAddresses": ["10.0.0.5"],
              "dnsServers": ["10.0.0.1"],
              "ssid": "HomeWifi",
              "nested": { "list": [ { "uuid": "abc", "machineGuid": "guid", "productKey": "KEY", "name": "kept" } ] }
            }
            """)!;
        var result = On.RedactJson(node)!;

        Assert.Equal("<redacted>", result["commandLine"]!.GetValue<string>());
        Assert.Equal("<redacted>", result["serialNumber"]!.GetValue<string>());
        Assert.Equal("<redacted>", result["macAddress"]!.GetValue<string>());
        Assert.Equal("<redacted>", result["ipAddresses"]!.GetValue<string>());
        Assert.Equal("<redacted>", result["dnsServers"]!.GetValue<string>());
        Assert.Equal("<redacted>", result["ssid"]!.GetValue<string>());
        var inner = result["nested"]!["list"]![0]!;
        Assert.Equal("<redacted>", inner["uuid"]!.GetValue<string>());
        Assert.Equal("<redacted>", inner["machineGuid"]!.GetValue<string>());
        Assert.Equal("<redacted>", inner["productKey"]!.GetValue<string>());
        Assert.Equal("kept", inner["name"]!.GetValue<string>());
    }

    [Fact]
    public void A_wrapped_value_keeps_its_status_and_loses_only_the_value()
    {
        var node = JsonNode.Parse("""{"serialNumber":{"value":"SN-123","status":"ok","source":"wmi"},"ssid":{"status":"denied","source":"wlan","detail":"no access"}}""")!;
        var result = On.RedactJson(node)!;
        Assert.Equal("<redacted>", result["serialNumber"]!["value"]!.GetValue<string>());
        Assert.Equal("ok", result["serialNumber"]!["status"]!.GetValue<string>());
        Assert.Equal("denied", result["ssid"]!["status"]!.GetValue<string>());
        Assert.False(result["ssid"]!.AsObject().ContainsKey("value"));
    }

    [Fact]
    public void Strings_in_unknown_keys_are_redacted_by_content_and_a_version_is_not_an_address()
    {
        var node = JsonNode.Parse("""
            {
              "path": "C:\\Users\\alice\\Downloads\\x.exe",
              "note": "talk to 192.168.0.9",
              "driverVersion": "31.0.15.3623",
              "biosVersion": "1.2.3.4",
              "list": ["ALICE-PC", "plain"],
              "count": 3,
              "flag": true,
              "nothing": null
            }
            """)!;
        var result = On.RedactJson(node)!;
        Assert.Equal(@"%USERPROFILE%\Downloads\x.exe", result["path"]!.GetValue<string>());
        Assert.Equal("talk to <ip>", result["note"]!.GetValue<string>());
        Assert.Equal("31.0.15.3623", result["driverVersion"]!.GetValue<string>());
        Assert.Equal("1.2.3.4", result["biosVersion"]!.GetValue<string>());
        Assert.Equal("<host>", result["list"]![0]!.GetValue<string>());
        Assert.Equal("plain", result["list"]![1]!.GetValue<string>());
        Assert.Equal(3, result["count"]!.GetValue<int>());
        Assert.True(result["flag"]!.GetValue<bool>());
    }

    [Fact]
    public void Redacting_a_null_tree_is_harmless()
    {
        Assert.Null(On.RedactJson(null));
    }

    // ---- by place in the tree ------------------------------------------------------------------------------------

    private const string Hardware = """
        {
          "label": "kept-global-label",
          "name": "kept-global-name",
          "processes": [ { "name": "kept.exe", "label": "kept" } ],
          "cpu": { "status": "ok", "source": "wmi", "value": { "name": { "status": "ok", "source": "wmi", "value": "kept CPU" } } },
          "storage": {
            "status": "ok", "source": "wmi",
            "value": [
              { "model": { "status": "ok", "source": "wmi", "value": "kept disk" },
                "volumes": [
                  { "letter": "C:", "label": "Tatsuki backup", "fileSystem": "NTFS" },
                  { "letter": "D:", "label": "", "fileSystem": "NTFS" } ] } ] },
          "audio": {
            "status": "ok", "source": "mmdevice",
            "value": [
              { "name": { "status": "ok", "source": "mmdevice", "value": "Headphones (Tatsuki's AirPods)" },
                "direction": { "status": "ok", "source": "mmdevice", "value": "output" } },
              { "name": { "status": "denied", "source": "mmdevice", "detail": "no access" } } ] },
          "usb": {
            "status": "ok", "source": "setupapi",
            "value": [
              { "name": { "status": "ok", "source": "setupapi", "value": "Tatsuki's phone" },
                "manufacturer": { "status": "ok", "source": "setupapi", "value": "kept vendor" } } ] }
        }
        """;

    [Fact]
    public void A_volume_label_audio_name_and_usb_name_are_blanked_at_their_place_only()
    {
        // Once at the root, once as the "hardware" part of a report: the match is on the end of the path.
        foreach (var wrapped in new[] { false, true })
        {
            var node = JsonNode.Parse(Hardware)!;
            var root = wrapped ? new JsonObject { ["hardware"] = node } : node;
            On.RedactJson(root);
            var hw = wrapped ? root["hardware"]! : root;

            var volumes = hw["storage"]!["value"]![0]!["volumes"]!;
            Assert.Equal("<redacted>", volumes[0]!["label"]!.GetValue<string>());
            Assert.Equal(string.Empty, volumes[1]!["label"]!.GetValue<string>());
            Assert.Equal("NTFS", volumes[0]!["fileSystem"]!.GetValue<string>());

            var audio = hw["audio"]!["value"]!;
            Assert.Equal("<redacted>", audio[0]!["name"]!["value"]!.GetValue<string>());
            Assert.Equal("ok", audio[0]!["name"]!["status"]!.GetValue<string>());
            Assert.Equal("output", audio[0]!["direction"]!["value"]!.GetValue<string>());
            // a name that could not be read keeps its reason
            Assert.Equal("denied", audio[1]!["name"]!["status"]!.GetValue<string>());
            Assert.False(audio[1]!["name"]!.AsObject().ContainsKey("value"));

            var usb = hw["usb"]!["value"]![0]!;
            Assert.Equal("<redacted>", usb["name"]!["value"]!.GetValue<string>());
            Assert.Equal("kept vendor", usb["manufacturer"]!["value"]!.GetValue<string>());

            // "label" and "name" anywhere else are other things and stay
            Assert.Equal("kept-global-label", hw["label"]!.GetValue<string>());
            Assert.Equal("kept-global-name", hw["name"]!.GetValue<string>());
            Assert.Equal("kept.exe", hw["processes"]![0]!["name"]!.GetValue<string>());
            Assert.Equal("kept", hw["processes"]![0]!["label"]!.GetValue<string>());
            Assert.Equal("kept CPU", hw["cpu"]!["value"]!["name"]!["value"]!.GetValue<string>());
            Assert.Equal("kept disk", hw["storage"]!["value"]![0]!["model"]!["value"]!.GetValue<string>());
        }
    }

    [Fact]
    public void The_blanked_places_are_listed_in_one_place()
    {
        Assert.Equal(
            ["audio[].details.adapter", "audio[].name", "displays[].details.monitorName", "network[].name", "usb[].name", "volumes[].label"],
            SimpleRedactor.BlankedPaths.Order().ToArray());
        Assert.True(SimpleRedactor.IsBlankedPath("storage[].volumes[].label"));
        Assert.True(SimpleRedactor.IsBlankedPath("volumes[].label"));
        Assert.True(SimpleRedactor.IsBlankedPath("network[].name"));
        Assert.True(SimpleRedactor.IsBlankedPath("hardware.network[].name"));
        Assert.True(SimpleRedactor.IsBlankedPath("displays[].details.monitorName"));
        Assert.True(SimpleRedactor.IsBlankedPath("audio[].details.adapter"));
        Assert.False(SimpleRedactor.IsBlankedPath("label"));
        Assert.False(SimpleRedactor.IsBlankedPath("storage[].label"));
        Assert.False(SimpleRedactor.IsBlankedPath("myvolumes[].label"));
        Assert.False(SimpleRedactor.IsBlankedPath("name"));
        Assert.False(SimpleRedactor.IsBlankedPath("processes[].name"));
        Assert.False(SimpleRedactor.IsBlankedPath("details.adapter"));
        Assert.False(SimpleRedactor.IsBlankedPath("monitorName"));
    }

    [Fact]
    public void A_network_name_monitor_name_and_audio_adapter_are_blanked_at_their_place()
    {
        // These three free-text fields carry a person's name in a real report (a renamed NIC, a monitor's OSD name,
        // an audio device's adapter) and the integrator left them unblanked. They are hidden by their tree place.
        const string json = """
            {
              "network": { "status": "ok", "source": "iphlp", "value": [
                { "name": { "status": "ok", "source": "iphlp", "value": "Tatsuki Wi-Fi" },
                  "driverVersion": { "status": "ok", "source": "iphlp", "value": "1.2.3.4" } } ] },
              "displays": { "status": "ok", "source": "edid", "value": [
                { "model": { "status": "ok", "source": "edid", "value": "kept monitor model" },
                  "details": {
                    "monitorName": { "status": "ok", "source": "edid", "value": "Tatsuki's Display" },
                    "serialNumber": { "status": "ok", "source": "edid", "value": "SN-XYZ" },
                    "inputType": { "status": "ok", "source": "edid", "value": "digital" } } } ] },
              "audio": { "status": "ok", "source": "mmdevice", "value": [
                { "name": { "status": "ok", "source": "mmdevice", "value": "Speakers" },
                  "details": {
                    "adapter": { "status": "ok", "source": "mmdevice", "value": "Tatsuki's USB DAC" },
                    "channels": { "status": "ok", "source": "mmdevice", "value": 2 } } } ] }
            }
            """;
        foreach (var wrapped in new[] { false, true })
        {
            var node = JsonNode.Parse(json)!;
            var root = wrapped ? new JsonObject { ["hardware"] = node } : node;
            On.RedactJson(root);
            var hw = wrapped ? root["hardware"]! : root;

            Assert.Equal("<redacted>", hw["network"]!["value"]![0]!["name"]!["value"]!.GetValue<string>());
            Assert.Equal("ok", hw["network"]!["value"]![0]!["name"]!["status"]!.GetValue<string>());
            // the four-part driver version must not be read as an IPv4 address
            Assert.Equal("1.2.3.4", hw["network"]!["value"]![0]!["driverVersion"]!["value"]!.GetValue<string>());

            var display = hw["displays"]!["value"]![0]!;
            Assert.Equal("<redacted>", display["details"]!["monitorName"]!["value"]!.GetValue<string>());
            Assert.Equal("<redacted>", display["details"]!["serialNumber"]!["value"]!.GetValue<string>()); // key-name rule still applies in details
            Assert.Equal("digital", display["details"]!["inputType"]!["value"]!.GetValue<string>());
            Assert.Equal("kept monitor model", display["model"]!["value"]!.GetValue<string>());

            var audio = hw["audio"]!["value"]![0]!;
            Assert.Equal("<redacted>", audio["details"]!["adapter"]!["value"]!.GetValue<string>());
            Assert.Equal(2, audio["details"]!["channels"]!["value"]!.GetValue<int>());
            Assert.Equal("<redacted>", audio["name"]!["value"]!.GetValue<string>()); // the endpoint name was already blanked by audio[].name
        }
    }

    // ---- the per-call switch -------------------------------------------------------------------------------------

    [Fact]
    public void The_per_call_switch_can_turn_redaction_off_for_one_call_but_never_on_when_the_process_turned_it_off()
    {
        IRedactor on = On;
        IRedactor off = Off;
        const string text = "alice on ALICE-PC";

        Assert.Equal("<user> on <host>", on.RedactText(text, redact: true));
        Assert.Equal(text, on.RedactText(text, redact: false));
        Assert.Equal(text, off.RedactText(text, redact: true));
        Assert.Equal(text, off.RedactText(text, redact: false));

        var node = JsonNode.Parse("""{"serialNumber":"S1"}""")!;
        Assert.Equal("S1", on.RedactJson(node.DeepClone(), redact: false)!["serialNumber"]!.GetValue<string>());
        Assert.Equal("<redacted>", on.RedactJson(node.DeepClone(), redact: true)!["serialNumber"]!.GetValue<string>());
        Assert.Equal("S1", off.RedactJson(node.DeepClone(), redact: true)!["serialNumber"]!.GetValue<string>());
    }
}
