using PcMieru.Core.Hardware.Collectors;
using PcMieru.Core.Hardware.Display;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Tests.Hardware;

public class EdidParserTests
{
    private static EdidInfo Load(string name) => EdidParser.Parse(HwFixture.Bytes(name))!;

    [Fact]
    public void Ug27pjIsEdid13WithHdrBlock()
    {
        var e = Load("edid-ug27pj.bin");
        Assert.Equal("WAM", e.PnpId);
        Assert.Equal(9984, e.ProductCode);
        Assert.Equal("UG27PJ", e.MonitorName);
        Assert.Equal((1, 3), (e.Major, e.Minor));
        Assert.True(e.ChecksumOk);
        Assert.Equal(1, e.ExtensionBlocks);
        Assert.True(e.ExtensionChecksumsOk);
        Assert.Equal(2022, e.Year);
        Assert.Equal(23, e.Week);
        Assert.Equal((597, 336), e.PhysicalSizeMm);
        Assert.Equal(48, e.MinVerticalHz);
        Assert.Equal(240, e.MaxVerticalHz);
        Assert.True(e.HdrStaticMetadataBlock);
        Assert.True(e.HdrPq);
        Assert.Equal(400, e.MaxLuminanceNits);
        // EDID 1.3 has no colour depth field.
        Assert.Null(e.BitsPerColor);
        Assert.True(e.Digital);
    }

    [Fact]
    public void FirstDetailedTimingIsThePreferredOne()
    {
        var e = Load("edid-ug27pj.bin");
        var p = e.Preferred!;
        Assert.Equal((1920, 1080), (p.Width, p.Height));
        Assert.Equal(60, p.RefreshHz, 1);
        Assert.False(p.Interlaced);
    }

    [Fact]
    public void Mfg27f4qIsEdid14WithTwoExtensionBlocks()
    {
        var e = Load("edid-mfg27f4q.bin");
        Assert.Equal("XEC", e.PnpId);
        Assert.Equal("MFG27F4Q", e.MonitorName);
        Assert.Equal((1, 4), (e.Major, e.Minor));
        Assert.Equal(2, e.ExtensionBlocks);
        Assert.True(e.ChecksumOk);
        Assert.True(e.ExtensionChecksumsOk);
        Assert.Equal(2025, e.Year);
        Assert.Equal(27, e.Week);
        Assert.Equal(10, e.BitsPerColor);
        Assert.Equal("DisplayPort", e.Interface);
        Assert.Equal(48, e.MinVerticalHz);
        Assert.Equal(200, e.MaxVerticalHz);
        Assert.Equal(418, e.MaxLuminanceNits);
        Assert.Equal((2560, 1440), (e.Preferred!.Width, e.Preferred.Height));
    }

    [Fact]
    public void Philips278e1HasFourKPreferredTimingAndNoHdr()
    {
        var e = Load("edid-philips-278e1.bin");
        Assert.Equal("PHL", e.PnpId);
        Assert.Equal("PHL 278E1", e.MonitorName);
        Assert.Equal(49687, e.ProductCode);
        Assert.Equal((3840, 2160), (e.Preferred!.Width, e.Preferred.Height));
        Assert.Equal(23, e.MinVerticalHz);
        Assert.Equal(75, e.MaxVerticalHz);
        Assert.False(e.HdrStaticMetadataBlock);
        Assert.Null(e.MaxLuminanceNits);
        Assert.Equal(2024, e.Year);
        Assert.Equal(21, e.Week);
    }

    [Fact]
    public void ScrubbedFixturesCarryNoSerialNumber()
    {
        foreach (var name in new[] { "edid-ug27pj.bin", "edid-mfg27f4q.bin", "edid-philips-278e1.bin", "edid-philips-278e1-truncated.bin" })
        {
            var e = Load(name);
            Assert.Equal(0u, e.NumericSerial);
            Assert.True(e.SerialText is null or "SCRUBBED", name);
        }
    }

    [Fact]
    public void TruncatedEdidIsFlaggedNotTrusted()
    {
        // The base block says one extension block follows, but only 128 bytes were stored.
        var e = Load("edid-philips-278e1-truncated.bin");
        Assert.Equal(1, e.ExtensionBlocks);
        Assert.False(e.ExtensionChecksumsOk);
        Assert.True(e.ChecksumOk);
    }

    [Fact]
    public void WrongHeaderAndShortInputAreRejected()
    {
        var bytes = HwFixture.Bytes("edid-ug27pj.bin");
        bytes[0] = 0x01;
        Assert.Null(EdidParser.Parse(bytes));
        Assert.Null(EdidParser.Parse(new byte[64]));
        Assert.Null(EdidParser.Parse([]));
    }

    [Fact]
    public void FlippedByteBreaksTheChecksum()
    {
        var bytes = HwFixture.Bytes("edid-ug27pj.bin");
        bytes[30] ^= 0x10;
        Assert.False(EdidParser.Parse(bytes)!.ChecksumOk);
    }

    [Fact]
    public void CtaLuminanceCodesFollowTheFormula()
    {
        // Block 1: CTA-861 extension with one HDR static metadata block, max luminance code 0x80 = 50 * 2^4 = 800 cd/m2.
        var edid = BaseBlock(extensions: 1);
        var cta = new byte[128];
        cta[0] = 0x02;
        cta[1] = 0x03;
        cta[2] = 4 + 7;
        cta[4] = (7 << 5) | 6;
        cta[5] = 6;
        cta[6] = 0x0D;
        cta[7] = 0x01;
        cta[8] = 0x80;
        cta[9] = 0x80;
        cta[10] = 0x00;
        Fix(cta);
        var e = EdidParser.Parse([.. edid, .. cta])!;
        Assert.True(e.HdrStaticMetadataBlock);
        Assert.True(e.HdrPq);
        Assert.True(e.HdrHlg);
        Assert.Equal(800, e.MaxLuminanceNits);
        Assert.Equal(0, e.MinLuminanceCdM2x10000);
    }

    [Fact]
    public void WeekFFMeansModelYear()
    {
        var edid = BaseBlock(extensions: 0);
        edid[16] = 0xFF;
        edid[17] = 33;
        Fix(edid);
        var e = EdidParser.Parse(edid)!;
        Assert.True(e.WeekIsModelYear);
        Assert.Null(e.Week);
        Assert.Equal(2023, e.Year);
    }

    [Fact]
    public void ZeroSizeFieldsAreNotASize()
    {
        var edid = BaseBlock(extensions: 0);
        edid[21] = 0;
        edid[22] = 0;
        Fix(edid);
        var e = EdidParser.Parse(edid)!;
        Assert.Null(e.WidthCm);
        Assert.Null(e.PhysicalSizeMm);
    }

    [Fact]
    public void AspectRatioOnlySizeIsNotASize()
    {
        var edid = BaseBlock(extensions: 0);
        edid[21] = 16;
        edid[22] = 0;
        Fix(edid);
        Assert.Null(EdidParser.Parse(edid)!.PhysicalSizeMm);
    }

    internal static byte[] BaseBlock(int extensions)
    {
        var b = new byte[128];
        byte[] header = [0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0];
        header.CopyTo(b, 0);
        b[8] = 0x10;
        b[9] = 0xAC;
        b[10] = 0x34;
        b[11] = 0x12;
        b[16] = 10;
        b[17] = 30;
        b[18] = 1;
        b[19] = 4;
        b[20] = 0xA5;
        b[21] = 60;
        b[22] = 34;
        b[126] = (byte)extensions;
        Fix(b);
        return b;
    }

    internal static void Fix(byte[] block)
    {
        var sum = 0;
        for (var i = 0; i < 127; i++)
        {
            sum += block[i];
        }

        block[127] = (byte)((256 - (sum & 0xFF)) & 0xFF);
    }
}

public class DisplayBuilderTests
{
    private static ActiveDisplayPath Path(int technology = 5, string? name = "Generic PnP Monitor") =>
        new(0x1234, 0, 0, 4352, technology, 143.99, 2560, 1440, name, "\\\\?\\DISPLAY#X", 0, 0, false);

    [Fact]
    public void NoEdidMeansEveryEdidValueIsUnsupportedWithAReason()
    {
        var d = DisplayBuilder.Build(Path(), null, null, "Some GPU", 2026);
        Assert.Equal(ValueStatus.Unsupported, d.Manufacturer.Status);
        Assert.Equal(ValueStatus.Unsupported, d.Model.Status);
        Assert.Equal(ValueStatus.Unsupported, d.PhysicalWidthMm.Status);
        Assert.Equal(ValueStatus.Unsupported, d.PreferredResolution.Status);
        Assert.Equal(ValueStatus.Unsupported, d.Details!.EdidChecksumOk.Status);
        Assert.Equal(ValueStatus.Unsupported, d.HdrEnabled.Status);
        Assert.NotNull(d.Details.SerialNumber.Detail);
        // What Windows does know is still reported.
        Assert.Equal("2560x1440", d.CurrentResolution.Data);
        Assert.Equal(143.99, d.RefreshHz.Data, 2);
        Assert.Equal("Some GPU", d.GpuName.Data);
        Assert.Equal(DisplayConnection.Hdmi, d.Connection.Data);
    }

    [Fact]
    public void FixtureEdidFillsTheRecord()
    {
        var d = DisplayBuilder.Build(Path(10), HwFixture.Bytes("edid-mfg27f4q.bin"), new AdvancedColorState(true, false, true, 8, "src", null), "GPU", 2026);
        Assert.Equal("MFG27F4Q", d.Model.Data);
        Assert.Equal(2025, d.ManufactureYear.Data);
        Assert.Equal(27, d.ManufactureWeek.Data);
        Assert.Equal("2560x1440", d.PreferredResolution.Data);
        Assert.Equal(DisplayConnection.DisplayPort, d.Connection.Data);
        Assert.False(d.HdrEnabled.Data);
        Assert.Equal(ValueStatus.Ok, d.HdrEnabled.Status);
        Assert.True(d.Details!.HdrSupported.Data);
        Assert.True(d.Details.HdrStaticMetadata.Data);
        Assert.Equal(418, d.Details.MaxLuminanceNits.Data);
        Assert.True(d.Details.EdidChecksumOk.Data);
        Assert.Equal(ValueStatus.Unsupported, d.Details.SerialNumber.Status);
        Assert.True(d.Details.DiagonalInches.Data is > 26 and < 28);
    }

    [Fact]
    public void PreferredTimingIsLabelledAsRecommendedNotNative()
    {
        var d = DisplayBuilder.Build(Path(), HwFixture.Bytes("edid-philips-278e1.bin"), null, null, 2026);
        Assert.Equal("3840x2160", d.PreferredResolution.Data);
        Assert.Contains("recommended", d.PreferredResolution.Detail, StringComparison.Ordinal);
        Assert.Contains("preferred", d.PreferredResolution.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownVendorIdBecomesAName()
    {
        var d = DisplayBuilder.Build(Path(), HwFixture.Bytes("edid-philips-278e1.bin"), null, null, 2026);
        Assert.Equal("Philips", d.Manufacturer.Data);
        Assert.Equal("PHL", d.Details!.PnpId.Data);
    }

    [Fact]
    public void UnknownVendorIdIsShownRawWithAnExplanation()
    {
        var d = DisplayBuilder.Build(Path(), HwFixture.Bytes("edid-ug27pj.bin"), null, null, 2026);
        Assert.Equal("WAM", d.Manufacturer.Data);
        Assert.Contains("not in the built-in list", d.Manufacturer.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ImplausibleYearIsNotReported()
    {
        // The fixture says 2022; asked "now" is 2020, so 2022 is in the future by more than a year.
        var d = DisplayBuilder.Build(Path(), HwFixture.Bytes("edid-ug27pj.bin"), null, null, 2020);
        Assert.Equal(ValueStatus.Unsupported, d.ManufactureYear.Status);
    }

    [Fact]
    public void TruncatedEdidReportsBadChecksumInsteadOfOk()
    {
        var d = DisplayBuilder.Build(Path(), HwFixture.Bytes("edid-philips-278e1-truncated.bin"), null, null, 2026);
        Assert.False(d.Details!.EdidChecksumOk.Data);
        Assert.NotNull(d.Details.EdidChecksumOk.Detail);
    }

    [Fact]
    public void WithoutWindowsHdrAnswerTheEdidBlockIsUsed()
    {
        var d = DisplayBuilder.Build(Path(), HwFixture.Bytes("edid-ug27pj.bin"), null, null, 2026);
        Assert.True(d.Details!.HdrSupported.Data);
        Assert.Contains("EDID", d.Details.HdrSupported.Source + d.Details.HdrSupported.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SerialIsReportedUnderTheRedactableLeafName()
    {
        // Craft a serial text descriptor (0xFF) so the leaf carries a value.
        var b = EdidParserTests.BaseBlock(0);
        b[54 + 3] = 0xFF;
        System.Text.Encoding.ASCII.GetBytes("ABC123456\n    ").CopyTo(b, 54 + 5);
        EdidParserTests.Fix(b);
        var d = DisplayBuilder.Build(Path(), b, null, null, 2026);
        Assert.Equal("ABC123456", d.Details!.SerialNumber.Data);
        var json = PcMieru.Core.PcMieruJson.Serialize(d.Details);
        Assert.Contains("\"serialNumber\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, DisplayConnection.Vga)]
    [InlineData(4, DisplayConnection.Dvi)]
    [InlineData(5, DisplayConnection.Hdmi)]
    [InlineData(10, DisplayConnection.DisplayPort)]
    [InlineData(11, DisplayConnection.Internal)]
    [InlineData(unchecked((int)0x80000000), DisplayConnection.Internal)]
    [InlineData(-1, DisplayConnection.Other)]
    [InlineData(15, DisplayConnection.Other)]
    public void OutputTechnologyMapsToConnection(int technology, DisplayConnection expected) =>
        Assert.Equal(expected, DisplayBuilder.Connection(technology).Data);

    [Fact]
    public void ManufacturerWordIsByteSwappedBeforeDecoding()
    {
        // EDID bytes 0x10 0xAC are "DEL"; the CCD API hands them over as the little-endian word 0xAC10.
        Assert.Equal("DEL", DisplayBuilder.PnpFromWord(0xAC10));
        Assert.Null(DisplayBuilder.PnpFromWord(0));
    }
}
