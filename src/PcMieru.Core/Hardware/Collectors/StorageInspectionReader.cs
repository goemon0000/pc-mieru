// Provenance: the walk (0 to 63, a missing drive skipped, the descriptor read before health, identity read again after it) follows the
// owner's pl-sensors StorageReader.ReadAll / ReadOne (src/PartsLife.Checks/Storage/StorageReader.cs, MIT, same owner).

using System.Runtime.InteropServices;
using PcMieru.Core.Hardware.Collectors;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;
using PcMieru.Core.Hardware.Storage;
using DiskIdentity = PcMieru.Wire.DiskIdentity;

namespace PcMieru.Core.Hardware;

/// <summary>
/// Real <see cref="IStorageInspectionSource"/>. One walk over the physical drives, each one looked at once: its identity is read with a
/// no-access handle (v0.7.4's four queries), its health is read the way <see cref="RealStorageHealthProvider"/> reads it, and on the
/// handle the health bytes came through the identity is read again so the two can be compared. Drives only the Helper can read are
/// asked for in ONE batch at the end (one UAC prompt), and the Helper brings its own re-read identity with the bytes. Nothing is
/// written to a drive and nothing is cached: each call is a fresh enumeration.
/// </summary>
public sealed class RealStorageInspectionSource(IElevatedStorageReader? elevated = null, Func<DateTime>? clock = null) : IStorageInspectionSource
{
    private const string Source = "ioctl:StorageDeviceProperty";

    private readonly Func<DateTime> _clock = clock ?? (static () => DateTime.UtcNow);

    public Task<StorageInspection> InspectAsync(bool allowElevation, CancellationToken ct = default) =>
        Task.Run(() => InspectCoreAsync(allowElevation, ct), ct);

    /// <summary>One drive of the walk before the batch is resolved: either already final (could not be identified) or waiting for its health.</summary>
    private sealed record Row(int Number, StorageInspectionDrive? Final, DiskIdentity? Identity, string Firmware, int StepIndex, DateTimeOffset At);

    private async Task<StorageInspection> InspectCoreAsync(bool allowElevation, CancellationToken ct)
    {
        var startedAt = Now();
        var provider = new RealStorageHealthProvider(elevated);
        var rows = new List<Row>();
        var steps = new List<RealStorageHealthProvider.Step>();
        var local = new Dictionary<int, ElevatedDriveRead>();
        var unreadableSlots = 0;

        for (var n = 0; n < StorageCollector.MaxDisks; n++)
        {
            ct.ThrowIfCancellationRequested();
            var number = n;
            var path = $@"\\.\PhysicalDrive{number}";
            DiskIdentity? identity = null;
            var firmware = string.Empty;
            var openError = 0;
            using (var h = Kernel32.OpenDeviceNoAccess(path))
            {
                if (h.IsInvalid)
                {
                    openError = Marshal.GetLastPInvokeError();
                }
                else
                {
                    identity = DiskIdentity.Read((code, input, length) => Ioctl.Call(h, code, input.Length == 0 ? null : input, length, out _), out firmware);
                }
            }

            switch (Classify(openError, identity is not null))
            {
                case SlotOutcome.Absent:
                    continue;
                case SlotOutcome.Unreadable:
                    unreadableSlots++;
                    rows.Add(openError != 0
                        ? Unreadable(number, Probe.FromWin32<StorageHealth>(openError, Source, "the drive"), startedAt)
                        : Unreadable(number, Value.Failed<StorageHealth>(Source, "could not identify the drive"), Now()));
                    continue;
            }

            // Readable: Classify said the descriptor came back, so there is an identity.
            var known = identity!;
            var step = provider.ReadByBus(path, number, known.Model, known.Bus, allowElevation, e => local[number] = e);
            rows.Add(new Row(number, null, known, firmware, steps.Count, Now()));
            steps.Add(step);
        }

        var resolved = await RealStorageHealthProvider.ResolveWithEvidenceAsync(steps, local, elevated, ct).ConfigureAwait(false);
        var afterBatch = Now();

        var drives = new List<StorageInspectionDrive>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Final is { } done)
            {
                drives.Add(done);
                continue;
            }

            var identity = row.Identity!;
            var step = steps[row.StepIndex];
            var result = resolved[row.StepIndex];
            var evidence = result.Evidence;
            var hasBytes = evidence?.Bytes is not null;
            var confirmation = hasBytes ? InspectionParsers.Confirm(identity, evidence!.Identity) : StorageIdentityConfirmation.NoHealth;
            var counters = hasBytes ? InspectionParsers.FromBytes(step.Kind, evidence!.Bytes, evidence.ErrorLog, evidence.PredictFailure) : null;

            // A drive that waited for the Helper was sampled when the batch came back, not when the walk passed it.
            drives.Add(new StorageInspectionDrive(
                row.Number,
                StorageRecognition.Recognized,
                identity.Model,
                identity.Serial,
                row.Firmware,
                identity.Bus,
                identity.SizeBytes,
                identity.UniqueId,
                identity.Location,
                step.Health is null ? afterBatch : row.At,
                result.Health.Health,
                confirmation,
                counters));
        }

        return new StorageInspection(startedAt, unreadableSlots, drives);
    }

    /// <summary>How one slot of the walk (PhysicalDrive0 to 63) came out.</summary>
    internal enum SlotOutcome
    {
        /// <summary>Nobody has this drive number (open error 2 or 3). It is not a drive, so it is not counted anywhere.</summary>
        Absent,

        /// <summary>
        /// The slot is there but could not be looked at: the open failed with any error other than 2 or 3 (access denied, sharing violation,
        /// a dying drive...), or it opened and its descriptor was missing. Counted in <see cref="StorageInspection.UnreadableSlots"/>.
        /// </summary>
        Unreadable,

        /// <summary>The device descriptor was read.</summary>
        Readable,
    }

    /// <summary>
    /// The one place that says what an unreadable slot is (DESIGN-A 5.2). <paramref name="openError"/> is the Win32 error of opening the
    /// drive with no access (0 when it opened); <paramref name="descriptorRead"/> is whether the device descriptor came back.
    /// </summary>
    internal static SlotOutcome Classify(int openError, bool descriptorRead)
    {
        if (openError != 0)
        {
            return StorageCollector.IsAbsent(openError) ? SlotOutcome.Absent : SlotOutcome.Unreadable;
        }

        return descriptorRead ? SlotOutcome.Readable : SlotOutcome.Unreadable;
    }

    private DateTimeOffset Now()
    {
        var t = _clock();
        return t.Kind == DateTimeKind.Unspecified ? new DateTimeOffset(t, TimeSpan.Zero) : new DateTimeOffset(t);
    }

    private static Row Unreadable(int number, Value<StorageHealth> why, DateTimeOffset at) =>
        new(
            number,
            new StorageInspectionDrive(number, StorageRecognition.Unreadable, string.Empty, string.Empty, string.Empty, 0, 0, string.Empty, string.Empty, at, why, StorageIdentityConfirmation.NoHealth, null),
            null,
            string.Empty,
            -1,
            at);
}
