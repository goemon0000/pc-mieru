using PcMieru.Core.Management;
using PcMieru.Wire;

namespace PcMieru.Gui;

/// <summary>
/// The Startup page of a smoke run (with or without --fakes): a list that lives in memory, so the run never reads HKCU/HKLM Run, StartupApproved or the real Startup folder (the owner's sign-in
/// entries would show in the page, and in a screenshot). It is not an <see cref="IFakeImplementation"/>: a real-data smoke run is not "sample data" because of it (the title and the checks that
/// ask for real data stay as they were). <see cref="Register"/> is handed to <see cref="PcMieruServices.Create"/> before any window exists.
/// </summary>
internal sealed class SmokeStartupManager : IStartupManager, IStartupUndo
{
    private readonly Dictionary<string, StartupState> _before = [];

    private readonly List<StartupItem> _items =
    [
        new("runHkcu:SmokeSample", StartupProvider.RunHkcu, "SmokeSample", "\"C:\\Smoke\\Sample.exe\" --silent", StartupState.Enabled, true, false),
        new("runHkcu:SmokeOff", StartupProvider.RunHkcu, "SmokeOff", "\"C:\\Smoke\\Off.exe\"", StartupState.Disabled, true, false),
        new("runHklm:SmokeMachine", StartupProvider.RunHklm, "SmokeMachine", "%windir%\\system32\\Smoke.exe", StartupState.Enabled, true, true),
        new("scheduledTask:SmokeTask", StartupProvider.ScheduledTask, "SmokeTask", "Smoke.exe", StartupState.Unknown, false, false, "list only in v1"),
    ];

    /// <summary>The registration for <see cref="PcMieruServices.Create"/>'s <c>configure</c>: both startup slots hold this stand-in.</summary>
    internal static void Register(ServiceRegistry registry)
    {
        var startup = new SmokeStartupManager();
        registry.Set<IStartupManager>(startup);
        registry.Set<IStartupUndo>(startup);
    }

    public Task<IReadOnlyList<StartupItem>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StartupItem>>(_items.ToArray());

    public Task<OperationResult> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0)
        {
            return Task.FromResult(OperationResult.Fail(OperationCode.NotFound, $"no startup item '{id}'"));
        }

        if (!_items[index].CanToggle)
        {
            return Task.FromResult(OperationResult.Fail(OperationCode.Unsupported, "this entry is list-only"));
        }

        _before.TryAdd(id, _items[index].State);
        _items[index] = _items[index] with { State = enabled ? StartupState.Enabled : StartupState.Disabled };
        return Task.FromResult(OperationResult.Success(enabled ? "enabled (smoke)" : "disabled (smoke)"));
    }

    public bool HasUndo(string id) => _before.ContainsKey(id);

    public Task<OperationResult> UndoAsync(string id, CancellationToken ct = default)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0 || !_before.Remove(id, out var state))
        {
            return Task.FromResult(OperationResult.Fail(OperationCode.NotFound, $"nothing to undo for '{id}'"));
        }

        _items[index] = _items[index] with { State = state };
        return Task.FromResult(OperationResult.Success("undone (smoke)"));
    }
}
