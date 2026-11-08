namespace PcMieru.Core.Hardware.Sensors;

/// <summary>
/// Which GPU power numbers are a measurement and which are only what a driver says. The AMD driver (ADLX) answers a "GPU power"
/// number for an integrated GPU that moves with the load of the whole system and may not be what the chip really draws (see
/// docs/contract-requests/C.md, section 8). A front end therefore keeps such a number out of every summary and list and shows
/// it only in the GPU's own detail view, next to the note the owner of that view localizes. A board total (ADLX
/// GPUTotalBoardPower) and NVML's reading are measurements and are not touched by this.
/// </summary>
public static class GpuPowerSource
{
    /// <summary>The <see cref="Value{T}.Source"/> of the ADLX reading that is the driver's own "GPU power" and not the board total.</summary>
    public const string DriverReport = "adlx:GPUPower";

    /// <summary>True when this power value is the driver's report (shown with a note, in the GPU detail view only).</summary>
    public static bool IsDriverReport(Value<double> power) =>
        power.IsOk && string.Equals(power.Source, DriverReport, StringComparison.Ordinal);
}
