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
}
