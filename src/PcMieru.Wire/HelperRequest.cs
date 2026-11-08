using System.Globalization;

namespace PcMieru.Wire;

/// <summary>
/// A typed Helper request. It travels on the command line, fixed at ShellExecute time, so it cannot be
/// swapped after the UAC prompt. <see cref="ToArgs"/> and <see cref="TryParse"/> are the only two places
/// that know the argument format; the caller and the Helper both use them.
/// </summary>
public sealed record HelperRequest(HelperOperation Op, string Nonce, string ResultPath)
{
    public string? ServiceName { get; init; }
    public StartupProvider? Provider { get; init; }
    public string? ValueName { get; init; }
    public bool? Enable { get; init; }
    public uint? Pid { get; init; }
    public long? CreateTime { get; init; }
    public SettablePriority? Priority { get; init; }
    public int? DriveNumber { get; init; }

    /// <summary>disk-health-all: the drives to read, as a validated list of small integers (never a path).</summary>
    public DriveList? Drives { get; init; }

    /// <summary>process-efficiency off only: the priority class to restore (a Win32 *_PRIORITY_CLASS value, never realtime).</summary>
    public uint? PreviousPriorityClass { get; init; }

    /// <summary>process-efficiency off only: PROCESS_POWER_THROTTLING_STATE.ControlMask to restore (0 or 1).</summary>
    public uint? PreviousThrottleControl { get; init; }

    /// <summary>process-efficiency off only: PROCESS_POWER_THROTTLING_STATE.StateMask to restore (0 or 1).</summary>
    public uint? PreviousThrottleState { get; init; }

    public const int MaxNameLength = 256;

    /// <summary>The Win32 priority classes the Helper may restore. Realtime (0x100) is deliberately not here.</summary>
    public static bool IsRestorablePriorityClass(uint value) => value is 0x40 or 0x4000 or 0x20 or 0x8000 or 0x80;

    public IReadOnlyList<string> ToArgs()
    {
        var args = new List<string> { HelperOperations.Get(Op).Name };

        void Add(string key, string? value)
        {
            if (value is not null)
            {
                args.Add("--" + key);
                args.Add(value);
            }
        }

        Add("name", ServiceName ?? ValueName);
        Add("provider", Provider is { } p ? ProviderToWire(p) : null);
        Add("pid", Pid?.ToString(CultureInfo.InvariantCulture));
        Add("created", CreateTime?.ToString(CultureInfo.InvariantCulture));
        Add("priority", Priority is { } pr ? PriorityToWire(pr) : null);
        Add("drive", DriveNumber?.ToString(CultureInfo.InvariantCulture));
        Add("drives", Drives?.ToWire());
        Add("prev-priority", PreviousPriorityClass?.ToString(CultureInfo.InvariantCulture));
        Add("prev-control", PreviousThrottleControl?.ToString(CultureInfo.InvariantCulture));
        Add("prev-state", PreviousThrottleState?.ToString(CultureInfo.InvariantCulture));
        if (Enable is { } enable)
        {
            Add("state", Op == HelperOperation.StartupHklmSet ? (enable ? "enable" : "disable") : (enable ? "on" : "off"));
        }

        Add("result", ResultPath);
        Add("nonce", Nonce);
        return args;
    }

    /// <summary>Strict parse: unknown or duplicate options, missing options and bad values are all refused.</summary>
    public static bool TryParse(IReadOnlyList<string> args, out HelperRequest? request, out string error)
    {
        request = null;
        error = "";

        if (args.Count == 0)
        {
            error = "no operation given";
            return false;
        }

        if (!HelperOperations.TryGet(args[0], out var spec))
        {
            error = $"unknown operation '{args[0]}'";
            return false;
        }

        var allowed = new HashSet<string>(spec.Options, StringComparer.Ordinal) { "result", "nonce" };
        var accepted = new HashSet<string>(allowed, StringComparer.Ordinal);
        accepted.UnionWith(spec.OptionalOptions);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i += 2)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal) || key.Length <= 2)
            {
                error = $"unexpected argument '{key}'";
                return false;
            }

            key = key[2..];
            if (!accepted.Contains(key))
            {
                error = $"option --{key} is not accepted by {spec.Name}";
                return false;
            }

            if (i + 1 >= args.Count)
            {
                error = $"option --{key} needs a value";
                return false;
            }

            if (!values.TryAdd(key, args[i + 1]))
            {
                error = $"option --{key} given twice";
                return false;
            }
        }

        foreach (var required in allowed)
        {
            if (!values.ContainsKey(required))
            {
                error = $"missing option --{required}";
                return false;
            }
        }

        var nonce = values["nonce"];
        if (!IsValidNonce(nonce))
        {
            error = "nonce must be 16-64 ASCII letters or digits";
            return false;
        }

        var resultPath = values["result"];
        if (!IsValidResultPath(resultPath, nonce))
        {
            error = "result must be a local absolute path named <nonce>.json";
            return false;
        }

        var req = new HelperRequest(spec.Op, nonce, resultPath);
        switch (spec.Op)
        {
            case HelperOperation.ServiceStart:
            case HelperOperation.ServiceStop:
            case HelperOperation.ServiceRestart:
                if (!IsValidName(values["name"], allowSeparators: false))
                {
                    error = "invalid service name";
                    return false;
                }

                req = req with { ServiceName = values["name"] };
                break;

            case HelperOperation.StartupHklmSet:
                if (!TryProvider(values["provider"], out var provider))
                {
                    error = "provider must be run-hklm, run-hklm32 or startup-folder-common";
                    return false;
                }

                var isFolder = provider == StartupProvider.StartupFolderCommon;
                if (!IsValidName(values["name"], allowSeparators: !isFolder))
                {
                    error = "invalid value name";
                    return false;
                }

                if (values["state"] is not ("enable" or "disable"))
                {
                    error = "state must be enable or disable";
                    return false;
                }

                req = req with { Provider = provider, ValueName = values["name"], Enable = values["state"] == "enable" };
                break;

            case HelperOperation.ProcessTerminate:
            case HelperOperation.ProcessPriority:
            case HelperOperation.ProcessEfficiency:
                if (!uint.TryParse(values["pid"], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == 0)
                {
                    error = "invalid pid";
                    return false;
                }

                if (!long.TryParse(values["created"], NumberStyles.None, CultureInfo.InvariantCulture, out var created) || created <= 0)
                {
                    error = "invalid created time (FILETIME, decimal)";
                    return false;
                }

                req = req with { Pid = pid, CreateTime = created };
                if (spec.Op == HelperOperation.ProcessPriority)
                {
                    if (!TryPriority(values["priority"], out var priority))
                    {
                        error = "priority must be idle, below, normal, above or high";
                        return false;
                    }

                    req = req with { Priority = priority };
                }
                else if (spec.Op == HelperOperation.ProcessEfficiency)
                {
                    if (values["state"] is not ("on" or "off"))
                    {
                        error = "state must be on or off";
                        return false;
                    }

                    req = req with { Enable = values["state"] == "on" };
                    if (!TryRestoreValues(values, req.Enable == false, out var prevPriority, out var prevControl, out var prevState, out error))
                    {
                        return false;
                    }

                    req = req with { PreviousPriorityClass = prevPriority, PreviousThrottleControl = prevControl, PreviousThrottleState = prevState };
                }

                break;

            case HelperOperation.SmartRead:
            case HelperOperation.NvmeHealth:
                if (!int.TryParse(values["drive"], NumberStyles.None, CultureInfo.InvariantCulture, out var drive) || drive > 255)
                {
                    error = "invalid physical drive number";
                    return false;
                }

                req = req with { DriveNumber = drive };
                break;

            case HelperOperation.DiskHealthAll:
                if (!DriveList.TryParse(values["drives"], out var drives))
                {
                    error = $"drives must be 1 to {DriveList.MaxCount} distinct physical drive numbers (0-{DriveList.MaxNumber}), ascending, comma-separated";
                    return false;
                }

                req = req with { Drives = drives };
                break;
        }

        request = req;
        return true;
    }

    /// <summary>The three restore values travel together, only with "off", and only as values the Helper could itself have saved.</summary>
    private static bool TryRestoreValues(
        Dictionary<string, string> values, bool isOff, out uint? priority, out uint? control, out uint? state, out string error)
    {
        priority = control = state = null;
        error = "";
        var given = new[] { "prev-priority", "prev-control", "prev-state" }.Count(values.ContainsKey);
        if (given == 0)
        {
            return true;
        }

        if (!isOff || given != 3)
        {
            error = "--prev-priority, --prev-control and --prev-state go together and only with state off";
            return false;
        }

        if (!uint.TryParse(values["prev-priority"], NumberStyles.None, CultureInfo.InvariantCulture, out var p) || !IsRestorablePriorityClass(p)
            || !uint.TryParse(values["prev-control"], NumberStyles.None, CultureInfo.InvariantCulture, out var c) || c > 1
            || !uint.TryParse(values["prev-state"], NumberStyles.None, CultureInfo.InvariantCulture, out var s) || s > 1)
        {
            error = "invalid restore values";
            return false;
        }

        priority = p;
        control = c;
        state = s;
        return true;
    }

    public static bool IsValidNonce(string nonce)
    {
        if (nonce.Length is < 16 or > 64)
        {
            return false;
        }

        foreach (var c in nonce)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A local drive path, no UNC or device prefixes, file name exactly "&lt;nonce&gt;.json".</summary>
    public static bool IsValidResultPath(string path, string nonce)
    {
        if (path.Length is < 8 or > 259 || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        if (!char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
        {
            return false;
        }

        foreach (var c in path)
        {
            if (c < ' ' || c is '<' or '>' or '"' or '|' or '?' or '*' or '/')
            {
                return false;
            }
        }

        foreach (var part in path[3..].Split('\\'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.'))
            {
                return false;
            }
        }

        return path.EndsWith("\\" + nonce + ".json", StringComparison.Ordinal);
    }

    private static bool IsValidName(string name, bool allowSeparators)
    {
        if (name.Length is 0 or > MaxNameLength)
        {
            return false;
        }

        foreach (var c in name)
        {
            if (c < ' ' || c == '\u007f' || (!allowSeparators && c is '\\' or '/'))
            {
                return false;
            }
        }

        return true;
    }

    private static string ProviderToWire(StartupProvider p) => p switch
    {
        StartupProvider.RunHklm => "run-hklm",
        StartupProvider.RunHklm32 => "run-hklm32",
        StartupProvider.StartupFolderCommon => "startup-folder-common",
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, "the Helper only accepts HKLM-scope providers"),
    };

    private static bool TryProvider(string s, out StartupProvider provider)
    {
        (var ok, provider) = s switch
        {
            "run-hklm" => (true, StartupProvider.RunHklm),
            "run-hklm32" => (true, StartupProvider.RunHklm32),
            "startup-folder-common" => (true, StartupProvider.StartupFolderCommon),
            _ => (false, default(StartupProvider)),
        };
        return ok;
    }

    private static string PriorityToWire(SettablePriority p) => p switch
    {
        SettablePriority.Idle => "idle",
        SettablePriority.BelowNormal => "below",
        SettablePriority.Normal => "normal",
        SettablePriority.AboveNormal => "above",
        SettablePriority.High => "high",
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, null),
    };

    private static bool TryPriority(string s, out SettablePriority priority)
    {
        (var ok, priority) = s switch
        {
            "idle" => (true, SettablePriority.Idle),
            "below" => (true, SettablePriority.BelowNormal),
            "normal" => (true, SettablePriority.Normal),
            "above" => (true, SettablePriority.AboveNormal),
            "high" => (true, SettablePriority.High),
            _ => (false, default(SettablePriority)),
        };
        return ok;
    }
}
