using PcMieru.Checks.Storage;

namespace PcMieru.Checks.Tests.Storage;

/// <summary>
/// The v2 disk key against the text v0.7.4 wrote (DESIGN-A 5.2). Every expected key below is a literal string, never built with the
/// code under test: a change in any character orphans every baseline v0.7.4 stored, so the literals are the contract.
/// </summary>
public class DiskKeyTests
{
    [Fact]
    public void The_example_of_the_design_is_reproduced_letter_for_letter()
    {
        // DESIGN-A 5.2: serial "SN%7|A_" (the % and the | are escaped, the _ is kept), model with the ATA vendor in front.
        var key = DiskKey.Of("SN%7|A_", "eui.0123456789abcdef", "ATA Example SSD", 1000204886016, 11, "0:0:0:0");

        Assert.Equal("v2|sn=SN%257%7CA_|uid=eui.0123456789abcdef|model=ATA Example SSD|size=1000204886016|bus=Sata", key.Key);
        Assert.True(key.Strong);
        Assert.Equal("SN%7|A_", key.Label);
    }

    [Fact]
    public void A_nvme_disk_with_a_serial_and_an_eui_id()
    {
        var key = DiskKey.Of("0025_38B5_21A0_1234.", "eui.0025385b21a01234", "Samsung SSD 980 PRO 2TB", 2000398934016, 17, "");

        // The trailing dot is removed upstream (ComposeSerial), not here: the key keeps what it is given, trimmed.
        Assert.Equal("v2|sn=0025_38B5_21A0_1234.|uid=eui.0025385b21a01234|model=Samsung SSD 980 PRO 2TB|size=2000398934016|bus=Nvme", key.Key);
    }

    [Fact]
    public void Serial_and_model_are_trimmed_and_the_rest_is_used_as_given()
    {
        var key = DiskKey.Of("  WD-WX12A  ", "naa.50014ee2b8c0ffee", "  WDC WD40EZRZ-00GXCB0  ", 4000787030016, 11, " 1:0:3:0 ");

        Assert.Equal("v2|sn=WD-WX12A|uid=naa.50014ee2b8c0ffee|model=WDC WD40EZRZ-00GXCB0|size=4000787030016|bus=Sata", key.Key);
    }

    [Fact]
    public void The_location_is_only_in_the_key_when_asked_for()
    {
        var without = DiskKey.Of("S3Z9NB0K123456A", "", "Samsung SSD 860 EVO 500GB", 500107862016, 11, "1:0:2:0");
        var with = DiskKey.Of("S3Z9NB0K123456A", "", "Samsung SSD 860 EVO 500GB", 500107862016, 11, "1:0:2:0", withLocation: true);

        Assert.Equal("v2|sn=S3Z9NB0K123456A|uid=|model=Samsung SSD 860 EVO 500GB|size=500107862016|bus=Sata", without.Key);
        Assert.Equal("v2|sn=S3Z9NB0K123456A|uid=|model=Samsung SSD 860 EVO 500GB|size=500107862016|bus=Sata|loc=1:0:2:0", with.Key);
    }

    [Fact]
    public void The_location_is_escaped_like_every_other_text()
    {
        var key = DiskKey.Of("ABC", "", "M", 1, 11, "a|b%c", withLocation: true);

        Assert.EndsWith("|loc=a%7Cb%25c", key.Key);
    }

    [Fact]
    public void A_bus_number_the_legacy_enum_did_not_have_is_written_as_a_number()
    {
        Assert.EndsWith("|bus=99", DiskKey.Of("ABC", "", "M", 1, 99, "").Key);
        Assert.EndsWith("|bus=20", DiskKey.Of("ABC", "", "M", 1, 20, "").Key);
        Assert.EndsWith("|bus=-1", DiskKey.Of("ABC", "", "M", 1, -1, "").Key);
    }

    [Theory]
    [InlineData(0, "Unknown")]
    [InlineData(1, "Scsi")]
    [InlineData(2, "Atapi")]
    [InlineData(3, "Ata")]
    [InlineData(4, "Ieee1394")]
    [InlineData(5, "Ssa")]
    [InlineData(6, "Fibre")]
    [InlineData(7, "Usb")]
    [InlineData(8, "Raid")]
    [InlineData(9, "iScsi")]
    [InlineData(10, "Sas")]
    [InlineData(11, "Sata")]
    [InlineData(12, "Sd")]
    [InlineData(13, "Mmc")]
    [InlineData(14, "Virtual")]
    [InlineData(15, "FileBackedVirtual")]
    [InlineData(16, "Spaces")]
    [InlineData(17, "Nvme")]
    [InlineData(18, "Scm")]
    [InlineData(19, "Ufs")]
    public void The_bus_names_are_the_legacy_enum_names_with_their_odd_capitals(int bus, string name) =>
        Assert.Equal(name, DiskKey.BusName(bus));

    [Fact]
    public void A_percent_is_escaped_before_the_bar_so_the_percent_of_the_bar_is_not_escaped_again()
    {
        Assert.Equal("%257C", DiskKey.Escape("%7C"));
        Assert.Equal("a%7Cb", DiskKey.Escape("a|b"));
        Assert.Equal("%7C%25", DiskKey.Escape("|%"));
        Assert.Equal("%257%7C", DiskKey.Escape("%7|"));
        Assert.Equal("plain_text-1.2", DiskKey.Escape("plain_text-1.2"));
        Assert.Equal("", DiskKey.Escape(""));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("0", false)]
    [InlineData("000000000000", false)]
    [InlineData("0 0 0", false)]
    [InlineData("..__--", false)]
    [InlineData("0000_0000", false)]
    [InlineData("0000000000001", true)]
    [InlineData("A", true)]
    [InlineData("S3Z9NB0K123456A", true)]
    [InlineData("__a__", true)]
    public void A_serial_is_usable_when_it_holds_an_ascii_letter_or_a_digit_other_than_zero(string serial, bool usable) =>
        Assert.Equal(usable, DiskKey.UsableSerial(serial));

    [Fact]
    public void A_non_ascii_letter_does_not_make_a_serial_usable()
    {
        Assert.False(DiskKey.UsableSerial("éé"));
        Assert.False(DiskKey.UsableSerial("١٢"));
    }

    [Fact]
    public void A_disk_is_strong_with_a_usable_serial_or_with_a_unique_id_and_weak_with_neither()
    {
        Assert.True(DiskKey.Of("ABC123", "", "M", 1, 11, "").Strong);
        Assert.True(DiskKey.Of("0000", "eui.0123456789abcdef", "M", 1, 17, "").Strong);
        Assert.True(DiskKey.Of("", "naa.50014ee2b8c0ffee", "M", 1, 11, "").Strong);

        var weak = DiskKey.Of("000000000000", "", "Generic USB Disk", 500107862016, 7, "");
        Assert.False(weak.Strong);
        Assert.Equal("v2|sn=000000000000|uid=|model=Generic USB Disk|size=500107862016|bus=Usb", weak.Key);
    }

    [Fact]
    public void The_label_is_the_serial_when_it_is_usable_else_the_model()
    {
        Assert.Equal("ABC123", DiskKey.Of(" ABC123 ", "", "Some Model", 1, 11, "").Label);
        Assert.Equal("Some Model", DiskKey.Of("0000", "eui.0123456789abcdef", "Some Model", 1, 17, "").Label);
        Assert.Equal("Some Model", DiskKey.Of("", "", "Some Model", 1, 11, "").Label);
    }

    [Fact]
    public void Two_disks_of_one_model_with_different_serials_have_different_keys_and_a_swap_is_visible_in_the_key()
    {
        var a = DiskKey.Of("SERIAL_A", "", "Samsung SSD 860 EVO 500GB", 500107862016, 11, "0:0:0:0");
        var b = DiskKey.Of("SERIAL_B", "", "Samsung SSD 860 EVO 500GB", 500107862016, 11, "0:0:0:0");
        var aAgain = DiskKey.Of("SERIAL_A", "", "Samsung SSD 860 EVO 500GB", 500107862016, 11, "1:0:5:0");

        Assert.NotEqual(a.Key, b.Key);
        Assert.Equal(a.Key, aAgain.Key);
    }

    [Fact]
    public void Two_weak_disks_of_one_model_share_a_key_which_is_why_a_weak_key_is_never_recorded()
    {
        var a = DiskKey.Of("", "", "Generic USB Disk", 500107862016, 7, "0:0:0:0");
        var b = DiskKey.Of("", "", "Generic USB Disk", 500107862016, 7, "0:0:1:0");

        Assert.Equal(a.Key, b.Key);
        Assert.False(a.Strong);
        Assert.False(b.Strong);
    }

    [Fact]
    public void The_size_is_written_in_invariant_digits_including_zero_for_an_unread_geometry()
    {
        Assert.Equal("v2|sn=ABC|uid=|model=M|size=0|bus=Sata", DiskKey.Of("ABC", "", "M", 0, 11, "").Key);
        Assert.Equal("v2|sn=ABC|uid=|model=M|size=9223372036854775807|bus=Sata", DiskKey.Of("ABC", "", "M", long.MaxValue, 11, "").Key);
    }

    [Fact]
    public void The_key_does_not_depend_on_the_culture_of_the_machine()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            Assert.Equal("v2|sn=ABC|uid=|model=M|size=1000204886016|bus=Nvme", DiskKey.Of("ABC", "", "M", 1000204886016, 17, "").Key);
            Assert.Equal("-5", DiskKey.BusName(-5));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }
}
