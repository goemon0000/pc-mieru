using System.Text.Json;
using Microsoft.Win32;
using PcMieru.Wire;

namespace PcMieru.Core.Management;

/// <summary>What an approval value was before the first change PC Mieru made to it (the strings are <see cref="StartupOriginal.ToData"/>).</summary>
public sealed record StartupUndoRecord(string Provider, string Name, string Existed, string Kind, string Bytes);

/// <summary>
/// The first original approval value per startup entry, kept in <c>%LOCALAPPDATA%\PCMieru\startup-undo.json</c>. The
/// FIRST record is the one kept: toggling an entry twice must not make "undo" a no-op. Removed again once undone.
/// </summary>
internal sealed class StartupUndoStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, StartupUndoRecord>? _records;

    public StartupUndoStore(string? path = null) =>
        _path = path ?? Path.Combine(ManagementFolder.Path, "startup-undo.json");

    /// <summary>Entry names are case-insensitive in the registry and in the file system, so the key is lower case.</summary>
    public static string KeyFor(string id) => id.ToLowerInvariant();

    public bool Contains(string id)
    {
        lock (_gate)
        {
            return Load().ContainsKey(KeyFor(id));
        }
    }

    public StartupUndoRecord? Get(string id)
    {
        lock (_gate)
        {
            return Load().TryGetValue(KeyFor(id), out var record) ? record : null;
        }
    }

    public void SetIfAbsent(string id, StartupUndoRecord record)
    {
        lock (_gate)
        {
            var records = Load();
            if (records.TryAdd(KeyFor(id), record))
            {
                Save(records);
            }
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            var records = Load();
            if (records.Remove(KeyFor(id)))
            {
                Save(records);
            }
        }
    }

    private Dictionary<string, StartupUndoRecord> Load()
    {
        if (_records is not null)
        {
            return _records;
        }

        _records = new Dictionary<string, StartupUndoRecord>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize(File.ReadAllText(_path), ManagementJsonContext.Default.DictionaryStringStartupUndoRecord);
                if (loaded is not null)
                {
                    _records = new Dictionary<string, StartupUndoRecord>(loaded, StringComparer.Ordinal);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file is the same as no record: there is just nothing to undo.
        }

        return _records;
    }

    private void Save(Dictionary<string, StartupUndoRecord> records)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(records, ManagementJsonContext.Default.DictionaryStringStartupUndoRecord));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the in-memory copy still serves this run.
        }
    }
}

/// <summary>
/// The real <see cref="IStartupManager"/>. It lists Run keys (HKCU, HKLM, HKLM Wow6432Node), both Startup folders,
/// boot/logon scheduled tasks and packaged-app startup tasks, and switches the first two kinds the way Task Manager
/// does: by flipping the StartupApproved value, never by deleting or editing the entry. Scheduled tasks and packaged
/// apps are list-only. User-side entries are switched in this process; machine-side ones need the Helper (one UAC
/// prompt) unless PC Mieru already runs elevated. The first original value is kept for <see cref="IStartupUndo"/>.
/// </summary>
public sealed class StartupManager : IStartupManager, IStartupUndo
{
    private static readonly StartupProvider[] Order =
    [
        StartupProvider.RunHkcu,
        StartupProvider.RunHklm,
        StartupProvider.RunHklm32,
        StartupProvider.StartupFolderUser,
        StartupProvider.StartupFolderCommon,
        StartupProvider.ScheduledTask,
        StartupProvider.PackagedApp,
    ];

    private readonly IStartupSource _source;
    private readonly StartupLocations _locations;
    private readonly IHelperClient _helper;
    private readonly Func<bool> _isElevated;
    private readonly StartupUndoStore _undo;
    private readonly Func<long> _nowFileTime;

    public StartupManager(IHelperClient helper)
        : this(StartupLocations.System(), helper, ElevationCheck.IsElevated, new StartupUndoStore(), () => DateTime.UtcNow.ToFileTimeUtc(), null)
    {
    }

    internal StartupManager(
        StartupLocations locations, IHelperClient helper, Func<bool> isElevated, StartupUndoStore undo, Func<long> nowFileTime, IStartupSource? source)
    {
        _locations = locations;
        _source = source ?? new SystemStartupSource(locations);
        _helper = helper;
        _isElevated = isElevated;
        _undo = undo;
        _nowFileTime = nowFileTime;
    }

    /// <summary>The places the Helper may change (and that need administrator rights).</summary>
    internal static bool IsMachineSide(StartupProvider provider) =>
        provider is StartupProvider.RunHklm or StartupProvider.RunHklm32 or StartupProvider.StartupFolderCommon;

    internal static bool IsToggleable(StartupProvider provider) => StartupApprovedCodec.ApprovedSubKey(provider) is not null;

    internal static string ProviderText(StartupProvider provider)
    {
        var name = provider.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    internal static string IdOf(StartupProvider provider, string name) => ProviderText(provider) + ":" + name;

    /// <summary>"provider:name", split at the FIRST colon (a name may contain colons).</summary>
    internal static bool TryParseId(string? id, out StartupProvider provider, out string name)
    {
        provider = default;
        name = "";
        if (id is null)
        {
            return false;
        }

        var colon = id.IndexOf(':');
        if (colon <= 0 || colon == id.Length - 1)
        {
            return false;
        }

        var text = id[..colon];
        foreach (var candidate in Order)
        {
            if (ProviderText(candidate).Equals(text, StringComparison.OrdinalIgnoreCase))
            {
                provider = candidate;
                name = id[(colon + 1)..];
                return true;
            }
        }

        return false;
    }

    public Task<IReadOnlyList<StartupItem>> ListAsync(CancellationToken ct = default) =>
        Task.Run(() => List(ct), ct);

    public bool HasUndo(string id) => _undo.Contains(id);

    public Task<OperationResult> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default) =>
        SetCoreAsync(id, enabled, ct);

    public Task<OperationResult> UndoAsync(string id, CancellationToken ct = default) =>
        UndoCoreAsync(id, ct);

    private IReadOnlyList<StartupItem> List(CancellationToken ct)
    {
        var elevated = _isElevated();
        var items = new List<StartupItem>();
        foreach (var provider in Order)
        {
            ct.ThrowIfCancellationRequested();
            var entries = _source.Read(provider, ct).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (entries.Count == 0)
            {
                continue;
            }

            if (!IsToggleable(provider))
            {
                foreach (var entry in entries)
                {
                    items.Add(new StartupItem(
                        IdOf(provider, entry.IdName ?? entry.Name),
                        provider,
                        entry.Name,
                        entry.Command,
                        entry.ListedState ?? StartupState.Unknown,
                        CanToggle: false,
                        NeedsHelper: false,
                        Detail: OperationMapper.Text(MessageKeys.StartupListOnly, null)));
                }

                continue;
            }

            using var approvedRoot = _locations.OpenApprovedRoot(provider);
            var approvedPath = ApprovedPath(provider);
            foreach (var entry in entries)
            {
                var state = ReadApproval(approvedRoot, approvedPath, entry.Name);
                var canToggle = state != StartupState.Unknown;
                items.Add(new StartupItem(
                    IdOf(provider, entry.Name),
                    provider,
                    entry.Name,
                    entry.Command,
                    state,
                    canToggle,
                    canToggle && IsMachineSide(provider) && !elevated,
                    canToggle
                        ? null
                        : OperationMapper.Text(MessageKeys.StartupUnknownState, new Dictionary<string, string> { ["name"] = entry.Name })));
            }
        }

        return items;
    }

    private string ApprovedPath(StartupProvider provider) =>
        _locations.ApprovedRoot(provider) + "\\" + StartupApprovedCodec.ApprovedSubKey(provider);

    private static StartupState ReadApproval(RegistryKey? root, string path, string name)
    {
        if (root is null)
        {
            return StartupState.Unknown;
        }

        try
        {
            return StartupApprovedToggle.Read(root, path, name, out _, out _) switch
            {
                ApprovalState.Enabled => StartupState.Enabled,
                ApprovalState.Disabled => StartupState.Disabled,
                _ => StartupState.Unknown,
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return StartupState.Unknown;
        }
    }

    private async Task<OperationResult> SetCoreAsync(string id, bool enable, CancellationToken ct)
    {
        if (!TryParseId(id, out var provider, out var wanted))
        {
            return NotFound(id);
        }

        if (!IsToggleable(provider))
        {
            return OperationMapper.ToResult(OperationCode.Unsupported, MessageKeys.StartupListOnly);
        }

        if (ct.IsCancellationRequested)
        {
            return new OperationResult(OperationCode.Cancelled, "");
        }

        // Only the one place that is needed is read, and the entry must be there: an approval is never made up for a missing entry.
        var (entry, state) = await Task.Run(() => Find(provider, wanted, ct), CancellationToken.None).ConfigureAwait(false);
        if (entry is null)
        {
            return NotFound(wanted);
        }

        var name = entry.Name;
        var named = ("name", name);
        if (state == StartupState.Unknown)
        {
            return OperationMapper.ToResult(OperationCode.Unsupported, MessageKeys.StartupUnknownState, null, named);
        }

        if (state == (enable ? StartupState.Enabled : StartupState.Disabled))
        {
            return OperationMapper.ToResult(OperationCode.Ok, enable ? MessageKeys.StartupAlreadyEnabled : MessageKeys.StartupAlreadyDisabled, null, named);
        }

        var undoId = IdOf(provider, name);
        if (!IsMachineSide(provider) || _isElevated())
        {
            return await Task.Run(() => ApplyInProcess(provider, name, enable, undoId), CancellationToken.None).ConfigureAwait(false);
        }

        // Machine side without elevation: the Helper (one UAC prompt). A name it would refuse is refused here, before the prompt.
        var request = new HelperRequest(HelperOperation.StartupHklmSet, "", "") { Provider = provider, ValueName = name, Enable = enable };
        if (!IsAcceptedByHelper(request))
        {
            return OperationMapper.ToResult(OperationCode.InvalidArguments, MessageKeys.StartupError, null, ("code", "bad-name"));
        }

        var outcome = await _helper.RunAsync(request, ct).ConfigureAwait(false);
        if (outcome.Code == OperationCode.Ok
            && OperationMapper.ActionFromHelper(outcome) is { } action
            && StartupOriginal.FromData(action.Data) is { } original)
        {
            _undo.SetIfAbsent(undoId, ToRecord(provider, name, original));
        }

        return OperationMapper.FromHelper(outcome);
    }

    private (RawStartupEntry? Entry, StartupState State) Find(StartupProvider provider, string name, CancellationToken ct)
    {
        var entry = _source.Read(provider, ct).FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return (null, StartupState.Unknown);
        }

        using var root = _locations.OpenApprovedRoot(provider);
        return (entry, ReadApproval(root, ApprovedPath(provider), entry.Name));
    }

    private OperationResult ApplyInProcess(StartupProvider provider, string name, bool enable, string undoId)
    {
        using var root = _locations.OpenApprovedRoot(provider);
        if (root is null)
        {
            return OperationMapper.ToResult(OperationCode.Error, MessageKeys.StartupError, null, ("code", "no-store"));
        }

        var result = StartupApprovedToggle.Apply(root, _locations.ApprovedRoot(provider), provider, name, enable, _nowFileTime(), out var original);
        if (result.Code == OperationCode.Ok && original is not null)
        {
            _undo.SetIfAbsent(undoId, ToRecord(provider, name, original));
        }

        return OperationMapper.ToResult(result);
    }

    private static StartupUndoRecord ToRecord(StartupProvider provider, string name, StartupOriginal original)
    {
        var data = original.ToData();
        return new StartupUndoRecord(
            ProviderText(provider), name, data[StartupOriginal.KeyExisted], data[StartupOriginal.KeyKind], data[StartupOriginal.KeyBytes]);
    }

    private static StartupOriginal? FromRecord(StartupUndoRecord record) =>
        StartupOriginal.FromData(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StartupOriginal.KeyExisted] = record.Existed,
            [StartupOriginal.KeyKind] = record.Kind,
            [StartupOriginal.KeyBytes] = record.Bytes,
        });

    private async Task<OperationResult> UndoCoreAsync(string id, CancellationToken ct)
    {
        if (!TryParseId(id, out var provider, out var wanted) || _undo.Get(id) is not { } record)
        {
            return OperationMapper.ToResult(
                OperationCode.NotFound, MessageKeys.StartupNothingToUndo, null, ("id", id));
        }

        var name = record.Name;
        var original = FromRecord(record);
        if (original is null || !IsToggleable(provider))
        {
            // A record that cannot be applied is worse than none: it would keep offering an undo that never works.
            _undo.Remove(id);
            return OperationMapper.ToResult(OperationCode.Error, MessageKeys.StartupError, null, ("code", "bad-undo-record"));
        }

        if (ct.IsCancellationRequested)
        {
            return new OperationResult(OperationCode.Cancelled, "");
        }

        var undoId = IdOf(provider, name);
        if (!IsMachineSide(provider) || _isElevated())
        {
            return await Task.Run(() => RestoreInProcess(provider, name, original, undoId), CancellationToken.None).ConfigureAwait(false);
        }

        // Through the Helper the value is put back to the original STATE (the Helper has no "write these bytes").
        var target = StartupApprovedCodec.Decode(original.Kind, original.Existed ? original.Bytes : null);
        if (target == ApprovalState.Unknown)
        {
            _undo.Remove(id);
            return OperationMapper.ToResult(OperationCode.Error, MessageKeys.StartupError, null, ("code", "bad-undo-record"));
        }

        var current = await Task.Run(() => ReadCurrent(provider, name), CancellationToken.None).ConfigureAwait(false);
        var restored = OperationMapper.ToResult(OperationCode.Ok, MessageKeys.StartupRestored, null, ("name", name));
        var enable = target == ApprovalState.Enabled;
        if (current == (enable ? StartupState.Enabled : StartupState.Disabled))
        {
            _undo.Remove(undoId);
            return restored;
        }

        var request = new HelperRequest(HelperOperation.StartupHklmSet, "", "") { Provider = provider, ValueName = name, Enable = enable };
        if (!IsAcceptedByHelper(request))
        {
            return OperationMapper.ToResult(OperationCode.InvalidArguments, MessageKeys.StartupError, null, ("code", "bad-name"));
        }

        var outcome = await _helper.RunAsync(request, ct).ConfigureAwait(false);
        if (outcome.Code != OperationCode.Ok)
        {
            return OperationMapper.FromHelper(outcome);
        }

        _undo.Remove(undoId);
        return restored;
    }

    private OperationResult RestoreInProcess(StartupProvider provider, string name, StartupOriginal original, string undoId)
    {
        using var root = _locations.OpenApprovedRoot(provider);
        if (root is null)
        {
            return OperationMapper.ToResult(OperationCode.Error, MessageKeys.StartupError, null, ("code", "no-store"));
        }

        var result = StartupApprovedToggle.Restore(root, _locations.ApprovedRoot(provider), provider, name, original);
        if (result.Code == OperationCode.Ok)
        {
            _undo.Remove(undoId);
        }

        return OperationMapper.ToResult(result);
    }

    private StartupState ReadCurrent(StartupProvider provider, string name)
    {
        using var root = _locations.OpenApprovedRoot(provider);
        return ReadApproval(root, ApprovedPath(provider), name);
    }

    /// <summary>True when the Helper's own argument check would accept this request (a bad name never costs a UAC prompt).</summary>
    private static bool IsAcceptedByHelper(HelperRequest request)
    {
        const string nonce = "ValidationNonce0000";
        var probe = request with { Nonce = nonce, ResultPath = @"C:\" + nonce + ".json" };
        return HelperRequest.TryParse(probe.ToArgs(), out _, out _);
    }

    private static OperationResult NotFound(string name) =>
        OperationMapper.ToResult(
            OperationCode.NotFound, MessageKeys.StartupNotFound, null, ("name", name));
}
