// Provenance: the order of the four identity queries and what is kept of each follow the owner's pl-sensors StorageReader.ReadOne
// (src/PartsLife.Checks/Storage/StorageReader.cs, MIT, same owner).
using System.Globalization;

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

    /// <summary>Puts the six values into a data map under the plain "identity.*" keys (the batch adds its own "drive.&lt;n&gt;." in front).</summary>
    public void WriteTo(IDictionary<string, string> data)
    {
        data[HelperDataKeys.IdentitySerial] = Serial;
        data[HelperDataKeys.IdentityUniqueId] = UniqueId;
        data[HelperDataKeys.IdentityLocation] = Location;
        data[HelperDataKeys.IdentityModel] = Model;
        data[HelperDataKeys.IdentitySizeBytes] = SizeBytes.ToString(CultureInfo.InvariantCulture);
        data[HelperDataKeys.IdentityBus] = Bus.ToString(CultureInfo.InvariantCulture);
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
