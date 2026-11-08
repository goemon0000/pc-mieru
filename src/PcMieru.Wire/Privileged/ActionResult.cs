using System.Globalization;

namespace PcMieru.Wire;

/// <summary>
/// What a privileged operation did, without any user-facing text: the Code, a message key (<see cref="MessageKeys"/>)
/// and the values to fill into it. Core turns the key into the user's language; the Helper puts the key into
/// <see cref="HelperResult.Message"/> and the values into <see cref="HelperResult.Data"/> as "arg.&lt;name&gt;".
/// <see cref="Data"/> carries machine-readable output (SMART bytes, values to remember for undo).
/// </summary>
public sealed record ActionResult(
    OperationCode Code,
    string MessageKey,
    IReadOnlyDictionary<string, string>? Args = null,
    IReadOnlyDictionary<string, string>? Data = null)
{
    private const string ArgPrefix = "arg.";

    public bool IsSuccess => Code == OperationCode.Ok;

    public static ActionResult Of(OperationCode code, string key, params (string Name, string Value)[] args)
    {
        Dictionary<string, string>? map = null;
        if (args.Length > 0)
        {
            map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in args)
            {
                map[name] = value;
            }
        }

        return new ActionResult(code, key, map);
    }

    public ActionResult WithData(IReadOnlyDictionary<string, string> data) => this with { Data = data };

    public ActionResult WithArg(string name, string value)
    {
        var map = Args is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(Args, StringComparer.Ordinal);
        map[name] = value;
        return this with { Args = map };
    }

    /// <summary>The file the Helper writes: key in Message, arguments as arg.&lt;name&gt;, then the data entries.</summary>
    public HelperResult ToHelperResult(string nonce, string opName)
    {
        Dictionary<string, string>? data = null;
        if (Args is { Count: > 0 } || Data is { Count: > 0 })
        {
            data = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Args is not null)
            {
                foreach (var (name, value) in Args)
                {
                    data[ArgPrefix + name] = value;
                }
            }

            if (Data is not null)
            {
                foreach (var (name, value) in Data)
                {
                    data[name] = value;
                }
            }
        }

        return new HelperResult(nonce, opName, Code, MessageKey, data);
    }

    /// <summary>The reverse of <see cref="ToHelperResult"/>. A message that is not one of our keys is kept as given.</summary>
    public static ActionResult FromHelperResult(HelperResult result)
    {
        Dictionary<string, string>? args = null;
        Dictionary<string, string>? data = null;
        if (result.Data is not null)
        {
            foreach (var (name, value) in result.Data)
            {
                if (name.StartsWith(ArgPrefix, StringComparison.Ordinal))
                {
                    (args ??= new Dictionary<string, string>(StringComparer.Ordinal))[name[ArgPrefix.Length..]] = value;
                }
                else
                {
                    (data ??= new Dictionary<string, string>(StringComparer.Ordinal))[name] = value;
                }
            }
        }

        return new ActionResult(result.Code, result.Message ?? "", args, data);
    }
}

/// <summary>Names of the machine-readable values in <see cref="ActionResult.Data"/> / <see cref="HelperResult.Data"/>.</summary>
public static class HelperDataKeys
{
    /// <summary>process-efficiency on: the priority class the process had (decimal Win32 class), to restore on "off".</summary>
    public const string PreviousPriority = "prev-priority";

    public const string PreviousControl = "prev-control";

    public const string PreviousState = "prev-state";

    /// <summary>"true" when the previous throttling state could be read; "false" means the defaults will be restored.</summary>
    public const string PreviousKnown = "prev-known";

    /// <summary>smart-read / nvme-health: the bus the drive is on ("ata", "nvme").</summary>
    public const string Bus = "bus";

    /// <summary>smart-read: base64 of the 512-byte SMART READ DATA sector (or the PREDICT_FAILURE vendor block).</summary>
    public const string SmartData = "smart.data";

    /// <summary>smart-read: base64 of the 512-byte SMART READ THRESHOLDS sector (absent if it could not be read).</summary>
    public const string SmartThresholds = "smart.thresholds";

    /// <summary>smart-read: "smart-rcv-drive-data" or "predict-failure" (the latter has no thresholds).</summary>
    public const string SmartSource = "smart.source";

    /// <summary>smart-read: "true"/"false", only with the predict-failure source.</summary>
    public const string PredictFailure = "smart.predict-failure";

    /// <summary>nvme-health: base64 of the 512-byte SMART / Health Information log page (0x02).</summary>
    public const string NvmeHealth = "nvme.health";

    /// <summary>nvme-health: base64 of the first 16 entries of the Error Information log page (0x01), if it could be read.</summary>
    public const string NvmeErrors = "nvme.errors";

    /// <summary>disk-health-all: what drive N answered (a decimal <see cref="OperationCode"/>); the rest of its keys follow under the same prefix.</summary>
    public const string DriveCode = "code";

    /// <summary>
    /// disk-health-all answers for several drives in one file, so each drive's keys carry "drive.&lt;n&gt;." in front:
    /// "drive.2.code", "drive.2.bus", "drive.2.smart.data", "drive.2.nvme.health" and so on.
    /// </summary>
    public static string ForDrive(int drive, string key) =>
        string.Concat("drive.", drive.ToString(CultureInfo.InvariantCulture), ".", key);
}
