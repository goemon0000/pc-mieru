// Provenance: the order of the four identity queries and what is kept of each follow the owner's pl-sensors StorageReader.ReadOne
// (src/PartsLife.Checks/Storage/StorageReader.cs, MIT, same owner).
using System.Globalization;
using System.Text.Json;

namespace PcMieru.Wire;

/// <summary>
/// What tells one physical disk from another, extracted exactly as v0.7.4 did (see <see cref="StorageProtocol"/>): the model with its
/// vendor in front, the serial without trailing dots, the unique id ("eui.&lt;hex&gt;" / "naa.&lt;hex&gt;" or empty), the SCSI location
/// ("Port:Path:Target:Lun" or empty), the size in bytes (0 when the geometry could not be read) and the raw STORAGE_BUS_TYPE number.
/// This is the value the Helper hands back under <c>drive.&lt;n&gt;.identity.*</c> and the value Core reads before the health read;
/// the two are compared by Core only. Nothing here is a display string.
/// </summary>
public sealed record DiskIdentity(string Model, string Serial, string UniqueId, string Location, long SizeBytes, int Bus)
{
    /// <summary>
    /// Runs the four identity queries v0.7.4 ran, on whatever handle <paramref name="ioctl"/> talks to (access 0, read or read/write all
    /// answer them), and returns what they said. A query that fails leaves its field at the old default (empty / 0): the descriptor is the
    /// only one that matters, and without it the answer is null. <paramref name="firmware"/> is the product revision, which is shown but
    /// not part of the identity. <paramref name="ioctl"/> takes (control code, input, output length) and returns null on failure.
    /// </summary>
    public static DiskIdentity? Read(Func<uint, byte[], int, byte[]?> ioctl, out string firmware)
    {
        firmware = "";
        var descriptor = ioctl(StorageProtocol.IoctlStorageQueryProperty, StorageProtocol.BuildDeviceQuery(), 1024);
        if (descriptor is null)
        {
            return null;
        }

        var fields = StorageProtocol.ParseDescriptorFields(descriptor);
        if (fields is not { } f)
        {
            return null;
        }

        var size = 0L;
        var geometry = ioctl(StorageProtocol.IoctlDiskGetDriveGeometryEx, [], 256);
        if (geometry is not null)
        {
            size = StorageProtocol.ParseGeometrySize(geometry);
        }

        var uniqueId = "";
        var ids = ioctl(StorageProtocol.IoctlStorageQueryProperty, StorageProtocol.BuildDeviceIdQuery(), 1024);
        if (ids is not null)
        {
            uniqueId = StorageProtocol.ParseDeviceUniqueId(ids);
        }

        var location = "";
        var address = ioctl(StorageProtocol.IoctlScsiGetAddress, [], 8);
        if (address is not null)
        {
            location = StorageProtocol.ParseScsiAddress(address);
        }

        firmware = f.Revision;
        return new DiskIdentity(StorageProtocol.ComposeModel(f.Vendor, f.Product), StorageProtocol.ComposeSerial(f.Serial), uniqueId, location, size, f.BusType);
    }

    /// <summary>
    /// The most one drive's identity may add to the Helper's answer, in bytes of the JSON the answer is written as (DESIGN-A 5.1): the
    /// six keys, each with its "drive.&lt;n&gt;." prefix, quotes, colon and comma, and the six values after JSON escaping. Strings from a
    /// descriptor are not bounded by anything but the 1024-byte answer, and a control character costs six bytes (a "u00xx" escape), so without
    /// this a 16-drive batch could outgrow the result file limit and lose the health bytes of every drive.
    /// </summary>
    public const int MaxJsonBytes = 2048;

    // "drive.255." : the longest prefix the batch puts in front of a key (the request range is 0 to 255).
    private const int LongestDrivePrefix = 10;

    // The two quotes of the key, the colon, the two quotes of the value and the comma.
    private const int PerPairOverhead = 6;

    /// <summary>
    /// How many bytes the six keys and values take in the batch answer, counted the way the answer is written (the serializer's default
    /// encoder: control characters, quotes, backslashes and the HTML-sensitive characters are escaped). A string that cannot be encoded at
    /// all (an unpaired surrogate) is counted as <see cref="int.MaxValue"/>.
    /// </summary>
    internal int JsonBytes()
    {
        long total = 0;
        foreach (var (key, value) in Pairs())
        {
            total += LongestDrivePrefix + key.Length + PerPairOverhead;
            try
            {
                total += JsonEncodedText.Encode(value).EncodedUtf8Bytes.Length;
            }
            catch (ArgumentException)
            {
                return int.MaxValue;
            }
        }

        return (int)Math.Min(total, int.MaxValue);
    }

    /// <summary>True when the six values fit the budget of <see cref="MaxJsonBytes"/>.</summary>
    public bool FitsHelperBudget => JsonBytes() <= MaxJsonBytes;

    /// <summary>
    /// Puts the six values into a data map under the plain "identity.*" keys (the batch adds its own "drive.&lt;n&gt;." in front), all six
    /// or none. When they would take more than <see cref="MaxJsonBytes"/> in the answer, NONE are written and false comes back: the
    /// drive's health still goes back, and Core reads the missing identity as "cannot be told apart from another disk" (Unconfirmed),
    /// never as "the same".
    /// </summary>
    public bool WriteTo(IDictionary<string, string> data)
    {
        if (!FitsHelperBudget)
        {
            return false;
        }

        foreach (var (key, value) in Pairs())
        {
            data[key] = value;
        }

        return true;
    }

    private IEnumerable<(string Key, string Value)> Pairs()
    {
        yield return (HelperDataKeys.IdentitySerial, Serial);
        yield return (HelperDataKeys.IdentityUniqueId, UniqueId);
        yield return (HelperDataKeys.IdentityLocation, Location);
        yield return (HelperDataKeys.IdentityModel, Model);
        yield return (HelperDataKeys.IdentitySizeBytes, SizeBytes.ToString(CultureInfo.InvariantCulture));
        yield return (HelperDataKeys.IdentityBus, Bus.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The identity one drive's keys carry in a batch answer, or null when any of the six is missing or not a number where a number is
    /// expected. That is what an older Helper (which never wrote them) looks like, and it must never be read as "the same disk".
    /// </summary>
    public static DiskIdentity? ReadFrom(IReadOnlyDictionary<string, string> data, int drive)
    {
        if (!data.TryGetValue(HelperDataKeys.ForDrive(drive, HelperDataKeys.IdentitySerial), out var serial)
            || !data.TryGetValue(HelperDataKeys.ForDrive(drive, HelperDataKeys.IdentityUniqueId), out var uniqueId)
            || !data.TryGetValue(HelperDataKeys.ForDrive(drive, HelperDataKeys.IdentityLocation), out var location)
            || !data.TryGetValue(HelperDataKeys.ForDrive(drive, HelperDataKeys.IdentityModel), out var model)
            || !data.TryGetValue(HelperDataKeys.ForDrive(drive, HelperDataKeys.IdentitySizeBytes), out var sizeText)
            || !data.TryGetValue(HelperDataKeys.ForDrive(drive, HelperDataKeys.IdentityBus), out var busText)
            || !long.TryParse(sizeText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var size)
            || !int.TryParse(busText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var bus))
        {
            return null;
        }

        return new DiskIdentity(model, serial, uniqueId, location, size, bus);
    }
}
