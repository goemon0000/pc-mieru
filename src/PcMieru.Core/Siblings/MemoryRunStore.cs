using System.Text.Json.Serialization;
using Microsoft.Win32;
using PcMieru.Wire;

namespace PcMieru.Core.Siblings;

/// <summary>The seven calls of <see cref="IRunStore"/>, so a failure can be put on any one of them.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<RunStoreCall>))]
public enum RunStoreCall
{
    ReadRun,
    WriteRun,
    DeleteRun,
    ReadApproval,
    WriteApproval,
    DeleteApproval,
    Flush,
}

/// <summary>
/// The stand-in store of every test and every smoke run: two dictionaries, no registry. A failure can be put on any call (<see cref="FailOn"/>, <see cref="DenyOn"/>), every call is
/// logged (<see cref="Calls"/>, with the value name), and a value can be put in place without a call (<see cref="Seed"/>) so a state the registry could be in can be built directly.
/// Thread safe: the settings page calls it from a worker thread.
/// </summary>
public sealed class MemoryRunStore : IRunStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RunValue> _run = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ApprovalValue> _approved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<RunStoreCall, Exception> _failures = [];
    private readonly List<(RunStoreCall Call, string Name)> _calls = [];
    private int _flushes;

    /// <summary>Every call made so far, in order, with the value name it was made for.</summary>
    public IReadOnlyList<(RunStoreCall Call, string Name)> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>The names of every Run value now in the store.</summary>
    public IReadOnlyList<string> RunNames
    {
        get
        {
            lock (_gate)
            {
                return [.. _run.Keys];
            }
        }
    }

    /// <summary>The names of every StartupApproved\Run value now in the store.</summary>
    public IReadOnlyList<string> ApprovalNames
    {
        get
        {
            lock (_gate)
            {
                return [.. _approved.Keys];
            }
        }
    }

    /// <summary>Makes every later call of this kind throw <paramref name="failure"/> (until <see cref="Heal"/>).</summary>
    public void FailOn(RunStoreCall call, Exception failure)
    {
        lock (_gate)
        {
            _failures[call] = failure;
        }
    }

    /// <summary>Makes every later call of this kind throw "access denied" (<see cref="UnauthorizedAccessException"/>).</summary>
    public void DenyOn(RunStoreCall call) => FailOn(call, new UnauthorizedAccessException("access denied (injected)"));

    /// <summary>Takes every injected failure away.</summary>
    public void Heal()
    {
        lock (_gate)
        {
            _failures.Clear();
        }
    }

    /// <summary>Puts a Run value in place without a call (and without the log).</summary>
    public void SeedRun(string name, string text, RegistryValueKind kind = RegistryValueKind.String)
    {
        lock (_gate)
        {
            _run[name] = new RunValue(kind, kind is RegistryValueKind.String or RegistryValueKind.ExpandString ? text : null);
        }
    }

    /// <summary>Puts a StartupApproved\Run value in place without a call (and without the log).</summary>
    public void SeedApproval(string name, byte[] bytes, RegistryValueKind kind = RegistryValueKind.Binary)
    {
        lock (_gate)
        {
            _approved[name] = new ApprovalValue(kind, bytes);
        }
    }

    public RunValue? ReadRun(string name)
    {
        lock (_gate)
        {
            Enter(RunStoreCall.ReadRun, name);
            return _run.GetValueOrDefault(name);
        }
    }

    public void WriteRun(string name, string text, RegistryValueKind kind = RegistryValueKind.String)
    {
        lock (_gate)
        {
            Enter(RunStoreCall.WriteRun, name);
            if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
            {
                throw new ArgumentException("a Run value is a string", nameof(kind));
            }

            _run[name] = new RunValue(kind, text);
        }
    }

    public void DeleteRun(string name)
    {
        lock (_gate)
        {
            Enter(RunStoreCall.DeleteRun, name);
            _run.Remove(name);
        }
    }

    public ApprovalValue? ReadApproval(string name)
    {
        lock (_gate)
        {
            Enter(RunStoreCall.ReadApproval, name);
            return _approved.GetValueOrDefault(name);
        }
    }

    public void WriteApproval(string name, byte[] bytes, RegistryValueKind kind = RegistryValueKind.Binary)
    {
        lock (_gate)
        {
            Enter(RunStoreCall.WriteApproval, name);
            _approved[name] = new ApprovalValue(kind, (byte[])bytes.Clone());
        }
    }

    public void DeleteApproval(string name)
    {
        lock (_gate)
        {
            Enter(RunStoreCall.DeleteApproval, name);
            _approved.Remove(name);
        }
    }

    /// <summary>How many times <see cref="Flush"/> was called (the memory store has nothing to flush).</summary>
    public int FlushCount
    {
        get
        {
            lock (_gate)
            {
                return _flushes;
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            _flushes++;
            _calls.Add((RunStoreCall.Flush, string.Empty));
            if (_failures.TryGetValue(RunStoreCall.Flush, out var failure))
            {
                throw failure;
            }
        }
    }

    private void Enter(RunStoreCall call, string name)
    {
        _calls.Add((call, name));
        if (_failures.TryGetValue(call, out var failure))
        {
            throw failure;
        }
    }
}
