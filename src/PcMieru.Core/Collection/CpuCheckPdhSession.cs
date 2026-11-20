namespace PcMieru.Core.Collection;

/// <summary>
/// What the CPU check's own PDH query gives for one second: the effective clock, the utilisation and the package power. A value that could not
/// be had is null (or a power state that says why); it is never 0.
/// </summary>
internal readonly record struct CpuPdhSample(double? EffectiveMhz, double? UtilizationPercent, PackagePowerReading Power)
{
    /// <summary>A sample in which nothing could be read, whatever the reason.</summary>
    public static CpuPdhSample Missing(PackagePowerState power = PackagePowerState.NoValidSample) => new(null, null, new PackagePowerReading(power, 0));
}

/// <summary>
/// A PDH query of the CPU check, opened for one run and closed with it (DESIGN-A 6.2). It is not the always-on
/// <see cref="PdhSystemSampler"/>'s query and sends nothing to <c>SamplerLoop</c>: the check's sampling cannot disturb the live view, nor the
/// live view's reads the check's. The counter paths are English (PdhAddEnglishCounterW), so a Japanese Windows needs no name table.
/// <para>
/// One <see cref="Sample"/> is one collection: the frequency and the performance of a sample come from that same collection, and so do the
/// utilisation and the package power. No administrator rights are needed.
/// </para>
/// </summary>
internal sealed class CpuCheckPdhSession : IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly PdhArrayReader _reader = new();
    private readonly nint _frequency;
    private readonly nint _performance;
    private readonly nint _utilization;
    private readonly nint _energy;
    private bool _baseline;

    public CpuCheckPdhSession()
    {
        if (!_query.IsOpen)
        {
            return;
        }

        _frequency = _query.Add(@"\Processor Information(_Total)\Processor Frequency", out _);
        _performance = _query.Add(@"\Processor Information(_Total)\% Processor Performance", out _);
        _utilization = _query.Add(@"\Processor Information(_Total)\% Processor Time", out _);
        _energy = _query.Add(@"\Energy Meter(*)\Power", out _);

        // The first collection only starts PDH's clock: the first Sample, a second later, then has a window to take its rates over.
        _baseline = _query.Collect() == Pdh.Success;
    }

    public bool IsOpen => _query.IsOpen;

    /// <summary>One collection and what it holds. Call about once a second. Nothing that could not be read comes back as a number.</summary>
    public CpuPdhSample Sample()
    {
        var noCounter = _energy == 0 ? PackagePowerState.NoCounter : PackagePowerState.NoValidSample;
        if (!_query.IsOpen)
        {
            return CpuPdhSample.Missing(noCounter);
        }

        var status = _query.Collect();
        var hadBaseline = _baseline;
        _baseline = status == Pdh.Success;
        if (!hadBaseline || !_baseline)
        {
            return CpuPdhSample.Missing(noCounter);
        }

        return new CpuPdhSample(ReadClock(), ReadUtilization(), ReadPower());
    }

    public void Dispose() => _query.Dispose();

    private double? ReadClock()
    {
        if (_frequency == 0 || _performance == 0)
        {
            return null;
        }

        if (!PdhReadRule.TryReadTotal(_reader, _frequency, out var frequency, out _)
            || !PdhReadRule.TryReadTotal(_reader, _performance, out var performance, out _))
        {
            return null;
        }

        return PdhReadRule.EffectiveMhz(frequency, performance);
    }

    private double? ReadUtilization()
    {
        if (_utilization == 0 || !PdhReadRule.TryReadTotal(_reader, _utilization, out var load, out _) || !double.IsFinite(load))
        {
            return null;
        }

        return Math.Clamp(load, 0, 100);
    }

    private PackagePowerReading ReadPower()
    {
        if (_energy == 0)
        {
            return new PackagePowerReading(PackagePowerState.NoCounter, 0);
        }

        if (!_reader.Read(_energy))
        {
            // "No instance" is the answer of a machine whose meter lists nothing; any other failure is a sample that did not come.
            return new PackagePowerReading(_reader.Status == Pdh.NoInstance ? PackagePowerState.NoPackageInstance : PackagePowerState.NoValidSample, 0);
        }

        return PdhReadRule.PackagePower(_reader.Bytes, _reader.BaseAddress, _reader.Count);
    }
}
