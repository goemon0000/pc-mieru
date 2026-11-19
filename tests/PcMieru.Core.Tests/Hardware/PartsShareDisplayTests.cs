using System.Text;
using System.Text.Json;
using PcMieru.Core.Hardware;
using PcMieru.Core.Hardware.Collectors;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;
using PcMieru.Core.Hardware.Share;

namespace PcMieru.Core.Tests.Hardware;

/// <summary>
/// The display part of "pcm-parts/1", from real and crafted EDIDs run through the real DisplayBuilder: the maker id, the product code
/// (bytes 10 and 11, little-endian, four hex digits) and the monitor-name descriptor 0xFC. The serial-number descriptor 0xFF, the
/// unspecified-text descriptor 0xFE and the 32-bit serial of the base block must never be in the document.
/// </summary>
public class PartsShareDisplayTests
{
    private static ActiveDisplayPath Path(string? windowsName = "Generic PnP Monitor") =>
        new(0x1234, 0, 0, 4352, 10, 143.99, 2560, 1440, windowsName, "\\\\?\\DISPLAY#X", 0, 0, false);

    private static void Descriptor(byte[] edid, int slot, byte tag, string text)
    {
        var at = 54 + (18 * slot);
        Array.Clear(edid, at, 18);
        edid[at + 3] = tag;
        var bytes = Encoding.ASCII.GetBytes(text + "\n");
        for (var i = 0; i < 13; i++)
        {
            edid[at + 5 + i] = i < bytes.Length ? bytes[i] : (byte)' ';
        }
    }

    /// <summary>A crafted EDID: maker "DEL" (bytes 8-9), product code 0x1234, size 600 x 340 mm, a 32-bit serial of 123456789.</summary>
    private static byte[] Crafted(string? fc, string? ff, string? fe)
    {
        var edid = EdidParserTests.BaseBlock(0);
        // 123456789 = 0x075BCD15, little-endian in bytes 12 to 15.
        edid[12] = 0x15;
        edid[13] = 0xCD;
        edid[14] = 0x5B;
        edid[15] = 0x07;
        if (ff is not null)
        {
            Descriptor(edid, 0, 0xFF, ff);
        }

        if (fe is not null)
        {
            Descriptor(edid, 1, 0xFE, fe);
        }

        if (fc is not null)
        {
            Descriptor(edid, 2, 0xFC, fc);
        }

        EdidParserTests.Fix(edid);
        return edid;
    }

    private static HardwareInfo Info(params DisplayDevice[] displays) =>
        FakeHardwareData.Create(new DateTime(2031, 2, 3, 4, 5, 6, DateTimeKind.Utc), HardwareCategory.Display)
        with { Displays = Value.Ok<IReadOnlyList<DisplayDevice>>(displays, "test") };

    private static string Json(HardwareInfo info) => PartsShare.ToJson(PartsShare.Build(info, "9.9.9", includeUsb: false));

    private static JsonElement Displays(string json) => JsonDocument.Parse(json).RootElement.GetProperty("displays").Clone();

    [Fact]
    public void TheSerialAndUnspecifiedTextDescriptorsNeverReachTheDocument()
    {
        var edid = Crafted(fc: "MY MONITOR", ff: "SERIALFF123", fe: "TEXTFE45678");
        var display = DisplayBuilder.Build(Path(), edid, null, null, 2026);

        // The collector does read the serial text, so the texts below are really in the data the document is built from.
        Assert.Equal("SERIALFF123", display.Details!.SerialNumber.Data);
        Assert.Contains("SERIALFF123", PcMieruJson.Serialize(display.Details), StringComparison.Ordinal);

        var json = Json(Info(display));
        foreach (var secret in new[] { "SERIALFF123", "TEXTFE45678", "SERIALFF", "TEXTFE", "123456789", "075BCD15", "075bcd15" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        }

        var only = Assert.Single(Displays(json).EnumerateArray());
        Assert.Equal("DEL", only.GetProperty("edidManufacturer").GetString());
        Assert.Equal("1234", only.GetProperty("edidProductCode").GetString());
        Assert.Equal("MY MONITOR", only.GetProperty("edidModel").GetString());
        Assert.Equal(600, only.GetProperty("physicalWidthMm").GetInt32());
        Assert.Equal(340, only.GetProperty("physicalHeightMm").GetInt32());
    }

    [Fact]
    public void WithoutAMonitorNameDescriptorTheModelIsLeftOut()
    {
        // 0xFF and 0xFE text that looks like a model name, a Windows-side name, and no 0xFC: nothing may stand in for the model.
        var edid = Crafted(fc: null, ff: "SERIALFF123", fe: "SHADOWMODEL1");
        var display = DisplayBuilder.Build(Path("Generic PnP Monitor"), edid, null, null, 2026);

        var json = Json(Info(display));
        var only = Assert.Single(Displays(json).EnumerateArray());
        Assert.False(only.TryGetProperty("edidModel", out _));
        Assert.Equal("DEL", only.GetProperty("edidManufacturer").GetString());
        Assert.Equal("1234", only.GetProperty("edidProductCode").GetString());
        foreach (var secret in new[] { "SERIALFF123", "SHADOWMODEL1", "Generic PnP", "123456789" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheWindowsSideModelTextIsNotUsed()
    {
        // DisplayDevice.Model (which may come from Windows) is not the EDID model; only Details.MonitorName is.
        var edid = Crafted(fc: "EDID NAME", ff: null, fe: null);
        var display = DisplayBuilder.Build(Path("Windows Side Name"), edid, null, null, 2026) with { Model = Value.Ok("WINDOWS MODEL TEXT", "test") };

        var json = Json(Info(display));
        Assert.DoesNotContain("WINDOWS MODEL TEXT", json, StringComparison.Ordinal);
        Assert.Equal("EDID NAME", Assert.Single(Displays(json).EnumerateArray()).GetProperty("edidModel").GetString());
    }

    private static string? CodeOf(byte[] edid) =>
        Assert.Single(Displays(Json(Info(DisplayBuilder.Build(Path(), edid, null, null, 2026)))).EnumerateArray()).GetProperty("edidProductCode").GetString();

    [Fact]
    public void TheProductCodeIsBytesTenAndElevenLittleEndianInFourHexDigits()
    {
        var edid = Crafted(fc: "X", ff: null, fe: null);
        Assert.Equal(0x34, edid[10]);
        Assert.Equal(0x12, edid[11]);
        Assert.Equal("1234", CodeOf(edid));

        edid[10] = 0xFF;
        edid[11] = 0xFF;
        EdidParserTests.Fix(edid);
        Assert.Equal("FFFF", CodeOf(edid));

        edid[10] = 0x05;
        edid[11] = 0x00;
        EdidParserTests.Fix(edid);
        Assert.Equal("0005", CodeOf(edid));
    }

    [Theory]
    [InlineData("edid-ug27pj.bin", "WAM", "2700", "UG27PJ")]
    [InlineData("edid-mfg27f4q.bin", "XEC", "0033", "MFG27F4Q")]
    [InlineData("edid-philips-278e1.bin", "PHL", "C217", "PHL 278E1")]
    public void RealEdidsGiveTheMakerCodeAndName(string fixture, string maker, string code, string model)
    {
        var bytes = HwFixture.Bytes(fixture);
        var display = DisplayBuilder.Build(Path(), bytes, null, null, 2026);

        // The expected code is worked out here from the raw bytes, independent of the parser.
        Assert.Equal(code, (bytes[10] | (bytes[11] << 8)).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));

        var only = Assert.Single(Displays(Json(Info(display))).EnumerateArray());
        Assert.Equal(maker, only.GetProperty("edidManufacturer").GetString());
        Assert.Equal(code, only.GetProperty("edidProductCode").GetString());
        Assert.Equal(model, only.GetProperty("edidModel").GetString());
        Assert.Equal(["edidManufacturer", "edidProductCode", "edidModel", "physicalWidthMm", "physicalHeightMm"], only.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void RealEdidsNeverLeakTheirSerialText()
    {
        // The three captured EDIDs carry no serial of their own (a 32-bit serial of 0 and no 0xFF descriptor), so checking them as they are
        // would pass without testing anything. Each gets a serial planted in a descriptor slot and in the base block, and the test
        // insists that the parser did read it: only then does "not in the document" mean something.
        var planted = 0;
        foreach (var (fixture, maker, code, model) in new[]
        {
            ("edid-ug27pj.bin", "WAM", "2700", "UG27PJ"),
            ("edid-mfg27f4q.bin", "XEC", "0033", "MFG27F4Q"),
            ("edid-philips-278e1.bin", "PHL", "C217", "PHL 278E1"),
        })
        {
            var bytes = HwFixture.Bytes(fixture);
            Assert.Equal(0, bytes[12] | bytes[13] | bytes[14] | bytes[15]);

            // Slot 1 is a spare timing or a dummy descriptor in all three; overwrite it with the serial text.
            EdidParserTests.TextDescriptor(bytes, 1, 0xFF, "PLANTSER12345");
            bytes[12] = 0x15;
            bytes[13] = 0xCD;
            bytes[14] = 0x5B;
            bytes[15] = 0x07;
            EdidParserTests.Fix(bytes);

            var display = DisplayBuilder.Build(Path(), bytes, null, null, 2026);
            var serial = display.Details!.SerialNumber;
            Assert.True(serial.TryGet(out var text) && text.Length >= 4, fixture + ": the planted serial was not read, so the check below would be vacuous");
            Assert.Equal("PLANTSER12345", text);
            planted++;

            var json = Json(Info(display));
            foreach (var secret in new[] { text, "PLANTSER", "123456789", "075BCD15" })
            {
                Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
            }

            // The model stays what the real descriptor says.
            var only = Assert.Single(Displays(json).EnumerateArray());
            Assert.Equal(maker, only.GetProperty("edidManufacturer").GetString());
            Assert.Equal(code, only.GetProperty("edidProductCode").GetString());
            Assert.Equal(model, only.GetProperty("edidModel").GetString());
        }

        Assert.Equal(3, planted);
    }

    [Fact]
    public void ANameSplitOverTwoDescriptorsIsOneModelAndNothingElseJoinsIt()
    {
        var edid = EdidParserTests.BaseBlock(extensions: 0);
        EdidParserTests.TextDescriptor(edid, 0, 0xFF, "SERIALFF123");
        EdidParserTests.TextDescriptor(edid, 1, 0xFC, "LG ULTRAGEAR ");
        EdidParserTests.TextDescriptor(edid, 2, 0xFE, "TEXTFE45678");
        EdidParserTests.TextDescriptor(edid, 3, 0xFC, "27GP950-B");
        EdidParserTests.Fix(edid);

        var json = Json(Info(DisplayBuilder.Build(Path(), edid, null, null, 2026)));

        var only = Assert.Single(Displays(json).EnumerateArray());
        Assert.Equal("LG ULTRAGEAR 27GP950-B", only.GetProperty("edidModel").GetString());
        foreach (var secret in new[] { "SERIALFF123", "TEXTFE45678", "SERIALFF", "TEXTFE" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ADisplayWithoutAnEdidIsNotListed()
    {
        // No EDID: no maker, no code, no name. A size alone tells the tool nothing about the model.
        var display = DisplayBuilder.Build(Path(), null, null, "Some GPU", 2026);
        Assert.Equal(0, Displays(Json(Info(display))).GetArrayLength());
    }

    [Fact]
    public void AVirtualDisplayWithoutAnEdidIsDroppedAndTheRealMonitorStays()
    {
        // Remote Desktop, an indirect display driver and a virtual monitor have a name and a resolution from Windows and no EDID behind them.
        var real = DisplayBuilder.Build(Path(), Crafted(fc: "MY MONITOR", ff: null, fe: null), null, null, 2026);
        var virtualOne = DisplayBuilder.Build(Path("Indirect Display Device"), null, null, "Some GPU", 2026)
            with { Model = Value.Ok("Indirect Display Device", "test") };
        var remote = DisplayBuilder.Build(Path("Remote Display"), null, null, "Some GPU", 2026);

        var json = Json(Info(virtualOne, real, remote));

        var only = Assert.Single(Displays(json).EnumerateArray());
        Assert.Equal("MY MONITOR", only.GetProperty("edidModel").GetString());
        Assert.DoesNotContain("Indirect", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Remote", json, StringComparison.Ordinal);
    }
}
