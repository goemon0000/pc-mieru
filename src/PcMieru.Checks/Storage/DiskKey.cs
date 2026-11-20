// Provenance: the key text, the escaping, the bus names and the "usable serial" rule are those of the owner's pl-sensors
// BaselineIdentity (src/PartsLife.Checks/Storage/Baseline.cs) and BusType (Storage/DiskReport.cs), MIT, same owner.

using System.Globalization;
using System.Text;

namespace PcMieru.Checks.Storage;

/// <summary>
/// The key v0.7.4 filed a disk's baseline under, built letter for letter the same way (DESIGN-A 5.2), so a record written by v0.7.4
/// is found again and a record written here is found by v0.7.4. It is a pure function of what the device descriptor said: the inputs
/// are the legacy-compatible values (the model with its vendor in front, the serial without trailing dots, "eui." / "naa." ids, the
/// "Port:Path:Target:Lun" location, the raw bus number), NOT the display strings of the hardware page. A disk that cannot be told
/// from another one by its key alone (no usable serial and no unique id) is <see cref="Strong"/> = false and is never recorded or compared.
/// </summary>
/// <param name="Key">The text a baseline is filed under. Not a file name: it can hold anything the drive reported.</param>
/// <param name="Label">What to call the disk when nothing better is known: the serial when it is usable, else the model.</param>
/// <param name="Strong">A usable serial or a non-empty unique id: enough to tell this disk from another of the same model.</param>
public sealed record DiskKey(string Key, string Label, bool Strong)
{
    /// <summary>The version tag every key starts with (the baseline format v0.7.4 wrote).</summary>
    public const string Version = "v2";

    /// <summary>
    /// The key of a disk. <paramref name="serial"/> and <paramref name="model"/> are trimmed here (the legacy key did), the rest is used
    /// as given. <paramref name="withLocation"/> adds "|loc=" for telling apart two disks that report the same strong key at once.
    /// </summary>
    public static DiskKey Of(string serial, string uniqueId, string model, long sizeBytes, int bus, string location, bool withLocation = false)
    {
        var sn = serial.Trim();
        var strong = UsableSerial(sn) || uniqueId.Length > 0;
        var key = new StringBuilder(Version)
            .Append("|sn=").Append(Escape(sn))
            .Append("|uid=").Append(Escape(uniqueId))
            .Append("|model=").Append(Escape(model.Trim()))
            .Append("|size=").Append(sizeBytes.ToString(CultureInfo.InvariantCulture))
            .Append("|bus=").Append(BusName(bus));
        if (withLocation)
        {
            key.Append("|loc=").Append(Escape(location));
        }

        return new DiskKey(key.ToString(), UsableSerial(sn) ? sn : model, strong);
    }

    /// <summary>"000000000000" and blanks are "no serial" (some bridge boards answer that): at least one ASCII letter or digit other than 0 is needed.</summary>
    public static bool UsableSerial(string serial) =>
        serial.Any(ch => char.IsAsciiLetterOrDigit(ch) && ch != '0');

    /// <summary>"%" first, then "|": the order matters, or the "%" of "%7C" would be escaped again.</summary>
    public static string Escape(string s) => s.Replace("%", "%25", StringComparison.Ordinal).Replace("|", "%7C", StringComparison.Ordinal);

    /// <summary>The inverse of <see cref="Escape"/>, read left to right ("%257C" is the text "%7C", not "|").</summary>
    public static string Unescape(string s)
    {
        var text = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length && s[i + 1] == '7' && s[i + 2] == 'C')
            {
                text.Append('|');
                i += 2;
            }
            else if (s[i] == '%' && i + 2 < s.Length && s[i + 1] == '2' && s[i + 2] == '5')
            {
                text.Append('%');
                i += 2;
            }
            else
            {
                text.Append(s[i]);
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// The key text of a disk with the label that <see cref="Of"/> would have given it, taken from the key alone: the serial when it is
    /// usable, else the model. For finding out which file name v0.7.4 gave a key that is read back from a file. False when the text is
    /// not shaped like a key ("v2|sn=...|uid=...|model=...").
    /// </summary>
    public static bool TryFromKeyText(string key, out DiskKey diskKey)
    {
        diskKey = null!;
        var parts = key.Split('|');
        if (parts.Length < 4 || parts[0] != Version
            || !parts[1].StartsWith("sn=", StringComparison.Ordinal)
            || !parts[2].StartsWith("uid=", StringComparison.Ordinal)
            || !parts[3].StartsWith("model=", StringComparison.Ordinal))
        {
            return false;
        }

        var serial = Unescape(parts[1][3..]);
        var model = Unescape(parts[3][6..]);
        diskKey = new DiskKey(key, UsableSerial(serial) ? serial : model, parts[2].Length > 4 || UsableSerial(serial));
        return true;
    }

    /// <summary>
    /// What v0.7.4's <c>BusType.ToString()</c> gave for the STORAGE_BUS_TYPE number: the enum member name, or the decimal number for a
    /// value the enum did not have. Note the odd capitals ("iScsi", "Ieee1394") are the legacy spelling and part of the key.
    /// </summary>
    public static string BusName(int bus) => bus switch
    {
        0 => "Unknown",
        1 => "Scsi",
        2 => "Atapi",
        3 => "Ata",
        4 => "Ieee1394",
        5 => "Ssa",
        6 => "Fibre",
        7 => "Usb",
        8 => "Raid",
        9 => "iScsi",
        10 => "Sas",
        11 => "Sata",
        12 => "Sd",
        13 => "Mmc",
        14 => "Virtual",
        15 => "FileBackedVirtual",
        16 => "Spaces",
        17 => "Nvme",
        18 => "Scm",
        19 => "Ufs",
        _ => bus.ToString(CultureInfo.InvariantCulture),
    };
}
