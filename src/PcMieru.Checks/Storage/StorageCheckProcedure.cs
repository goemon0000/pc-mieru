using PcMieru.Core.Hardware;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Checks.Storage;

/// <summary>
/// The storage check (DESIGN-A §3.3, §14.4) as a procedure of the run framework: take a baseline ticket, read the drives, let the baseline
/// compare and record them, then write one block of rows per disk.
/// <list type="number">
/// <item><see cref="BaselineEngine.BeginRun"/> before the first byte is read, so that a drive swapped during the read is not taken for the same one.</item>
/// <item><see cref="IStorageInspectionSource.InspectAsync"/> with <see cref="CheckRequest.AllowElevation"/>. A source that throws (other than a
/// cancel) fails the run as a whole: there is nothing to judge.</item>
/// <item><see cref="BaselineEngine.Complete"/> on a worker thread (it may wait for the baseline's lock for seconds, so never on the caller's).</item>
/// <item>Rows: <c>storage.devices</c>, then per disk <see cref="StorageJudge.Rows"/> in the order the baseline numbered the disks.</item>
/// </list>
/// </summary>
internal sealed class StorageCheckProcedure : ICheckProcedure
{
    public CheckKind Kind => CheckKind.Storage;

    public async Task ExecuteAsync(CheckContext context, CancellationToken ct)
    {
        var services = context.Services;
        var collector = context.Collector;

        context.Stage(CheckStage.Collecting);
        var ticket = services.Baseline.BeginRun();

        StorageInspection inspection;
        try
        {
            inspection = await services.Storage.InspectAsync(context.Request.AllowElevation, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new CheckRunFailedException(CheckRunFailureCodes.InspectionFailed, CheckRunner.Describe(ex), ex);
        }

        ct.ThrowIfCancellationRequested();
        context.Stage(CheckStage.Judging);

        // Off the caller's thread: the baseline waits up to 15 s for its lock, and its files are on a disk.
        var baseline = await Task.Run(() => services.Baseline.Complete(ticket, inspection), CancellationToken.None).ConfigureAwait(false);
        if (baseline.ReadFailed)
        {
            collector.Note(CheckNotes.StorageBaselineReadFailed);
        }

        if (baseline.NotesAbandonedImport)
        {
            collector.Note(CheckNotes.StorageLegacyImportAbandoned);
        }

        collector.Add(Devices(inspection));
        foreach (var disk in baseline.Disks)
        {
            var drive = inspection.Drives[disk.DriveIndex];
            collector.AddRange(StorageJudge.Rows(drive, disk, StorageJudge.SubjectOf(drive, disk.DiskId)));
        }
    }

    // How many drives there are, counting the slots that could not be looked at; those are named as "partial" (the number is of what was seen).
    private static CheckItem Devices(StorageInspection inspection) =>
        CheckItem.Create(
            "storage.devices",
            CheckValue.Int(inspection.Drives.Count),
            ItemVerdict.Info,
            CheckSource.StorageQuery,
            inspection.UnreadableSlots > 0 ? CheckReason.Partial : null);
}
