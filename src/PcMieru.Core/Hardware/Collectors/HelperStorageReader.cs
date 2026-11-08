using PcMieru.Core.Hardware.Model;
using PcMieru.Wire;

namespace PcMieru.Core.Hardware;

/// <summary>
/// <see cref="IElevatedStorageReader"/> that asks the elevated Helper (smart-read / nvme-health) for the raw bytes of one drive. The Helper
/// sends the sectors back as base64 inside <see cref="HelperResult.Data"/>; nothing is parsed there. One call is one UAC prompt, so this is
/// only used when the user chose to read health with administrator rights. The key names are the Helper's (area D, HelperDataKeys); they are
/// repeated here as text so this file does not depend on how the Helper project is laid out.
/// </summary>
internal sealed class HelperStorageReader(Func<IHelperClient?> helper) : IElevatedStorageReader
{
    internal const string SmartData = "smart.data";
    internal const string SmartThresholds = "smart.thresholds";
    internal const string NvmeHealth = "nvme.health";

    private const int SectorLength = 512;

    public async Task<byte[]?> ReadRawAsync(int physicalDriveNumber, StorageHealthKind kind, CancellationToken ct)
    {
        var client = helper();
        if (client is null || physicalDriveNumber is < 0 or > 255)
        {
            return null;
        }

        // The real client replaces the nonce and result path with its own; the request only has to say what to read.
        var op = kind == StorageHealthKind.Nvme ? HelperOperation.NvmeHealth : HelperOperation.SmartRead;
        var outcome = await client.RunAsync(new HelperRequest(op, string.Empty, string.Empty) { DriveNumber = physicalDriveNumber }, ct).ConfigureAwait(false);
        if (outcome.Code != OperationCode.Ok || outcome.Result?.Data is not { } data)
        {
            return null;
        }

        return kind == StorageHealthKind.Nvme ? Sector(data, NvmeHealth) : Smart(data);
    }

    private static byte[]? Smart(Dictionary<string, string> data)
    {
        var table = Sector(data, SmartData);
        if (table is null)
        {
            return null;
        }

        // The thresholds are optional (the Helper leaves them out when it could not read them); a damaged one is left out too,
        // never passed on as if it were a threshold table.
        var thresholds = Sector(data, SmartThresholds);
        if (thresholds is null)
        {
            return table;
        }

        var both = new byte[SectorLength * 2];
        table.CopyTo(both, 0);
        thresholds.CopyTo(both, SectorLength);
        return both;
    }

    private static byte[]? Sector(Dictionary<string, string> data, string key)
    {
        if (!data.TryGetValue(key, out var text))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(text);
            return bytes.Length == SectorLength ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
