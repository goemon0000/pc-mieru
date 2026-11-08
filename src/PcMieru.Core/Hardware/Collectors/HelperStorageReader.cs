using PcMieru.Core.Hardware.Model;
using PcMieru.Wire;

namespace PcMieru.Core.Hardware;

/// <summary>
/// <see cref="IElevatedStorageReader"/> that asks the elevated Helper for the raw bytes of drives. The Helper sends the sectors back as
/// base64 inside <see cref="HelperResult.Data"/>, under the names in <see cref="HelperDataKeys"/>; nothing is parsed there. One request is
/// one UAC prompt, so this is only used when the user chose to read health with administrator rights, and
/// <see cref="ReadRawManyAsync"/> puts every drive that needs it into a single request (disk-health-all). The single-drive
/// <see cref="ReadRawAsync"/> (smart-read / nvme-health) stays for callers that want exactly one drive.
/// </summary>
internal sealed class HelperStorageReader(Func<IHelperClient?> helper) : IElevatedStorageReader
{
    private const int SectorLength = 512;

    public async Task<byte[]?> ReadRawAsync(int physicalDriveNumber, StorageHealthKind kind, CancellationToken ct)
    {
        var client = helper();
        if (client is null || physicalDriveNumber is < 0 or > DriveList.MaxNumber)
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

        return Decode(data, kind, drive: null);
    }

    public async Task<IReadOnlyDictionary<int, byte[]?>> ReadRawManyAsync(IReadOnlyList<(int PhysicalDriveNumber, StorageHealthKind Kind)> drives, CancellationToken ct)
    {
        var answers = new Dictionary<int, byte[]?>();
        var asked = new List<(int Number, StorageHealthKind Kind)>();
        foreach (var (number, kind) in drives)
        {
            // What the Helper would refuse is never sent: a number outside 0..255, or a drive named twice (the first kind wins).
            if (number is >= 0 and <= DriveList.MaxNumber && answers.TryAdd(number, null))
            {
                asked.Add((number, kind));
            }
        }

        var client = asked.Count == 0 ? null : helper();
        if (client is null)
        {
            return answers;
        }

        // More drives than one request may carry is not a real PC; it still works, one prompt for each full batch.
        foreach (var batch in asked.Chunk(DriveList.MaxCount))
        {
            ct.ThrowIfCancellationRequested();
            if (!DriveList.TryCreate(batch.Select(d => d.Number), out var list))
            {
                continue;
            }

            var outcome = await client.RunAsync(new HelperRequest(HelperOperation.DiskHealthAll, string.Empty, string.Empty) { Drives = list }, ct).ConfigureAwait(false);

            // Ok = every drive was read, PartialSuccess = some were. Anything else (declined, denied, failed) leaves the batch as null.
            if (outcome.Code is not (OperationCode.Ok or OperationCode.PartialSuccess) || outcome.Result?.Data is not { } data)
            {
                continue;
            }

            foreach (var (number, kind) in batch)
            {
                // The exit code only says how the run went; each drive says for itself whether its bytes are good.
                if (data.TryGetValue(HelperDataKeys.ForDrive(number, HelperDataKeys.DriveCode), out var code)
                    && code == ((int)OperationCode.Ok).ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    answers[number] = Decode(data, kind, number);
                }
            }
        }

        return answers;
    }

    /// <summary>The bytes the health parsers read: the NVMe log page, or the SMART table (followed by the thresholds when they are good).</summary>
    private static byte[]? Decode(Dictionary<string, string> data, StorageHealthKind kind, int? drive)
    {
        string Key(string name) => drive is { } n ? HelperDataKeys.ForDrive(n, name) : name;

        if (kind == StorageHealthKind.Nvme)
        {
            return Sector(data, Key(HelperDataKeys.NvmeHealth));
        }

        var table = Sector(data, Key(HelperDataKeys.SmartData));
        if (table is null)
        {
            return null;
        }

        // The thresholds are optional (the Helper leaves them out when it could not read them); a damaged one is left out too,
        // never passed on as if it were a threshold table.
        var thresholds = Sector(data, Key(HelperDataKeys.SmartThresholds));
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
