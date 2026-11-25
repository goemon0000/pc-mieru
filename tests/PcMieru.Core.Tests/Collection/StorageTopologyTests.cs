using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Collectors;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Tests.Collection;

/// <summary>
/// The storage topology the Parts Life board reads (DESIGN-C 2.3): what kind each physical drive is and which local volumes are on it. The pure
/// parts (kind from what Windows says, the volume fold, the drive-number match) run everywhere; the refresher runs on a fake reader and a fake
/// clock so its schedule, its "unknown rather than guessed" failures and its Dispose are exact.
/// </summary>
public sealed class StorageTopologyTests
{
    // ---- the kind of a drive ----

    [Theory]
    [InlineData(StorageBus.Nvme, false, null, DiskKind.Nvme)]
    [InlineData(StorageBus.Nvme, false, false, DiskKind.Nvme)]
    [InlineData(StorageBus.Sata, false, false, DiskKind.Ssd)]
    [InlineData(StorageBus.Sata, false, true, DiskKind.Hdd)]
    [InlineData(StorageBus.Raid, false, true, DiskKind.Hdd)]
    [InlineData(StorageBus.Scsi, false, false, DiskKind.Ssd)]
    [InlineData(StorageBus.Sata, false, null, DiskKind.Unknown)]
    [InlineData(StorageBus.Usb, true, false, DiskKind.Unknown)]
    [InlineData(StorageBus.Nvme, true, false, DiskKind.Unknown)]
    [InlineData(StorageBus.Virtual, false, false, DiskKind.Unknown)]
    [InlineData(StorageBus.Iscsi, false, true, DiskKind.Unknown)]
    [InlineData(StorageBus.Spaces, false, false, DiskKind.Unknown)]
    [InlineData(StorageBus.Unknown, false, false, DiskKind.Unknown)]
    public void A_drive_kind_is_only_what_windows_confirmed(StorageBus bus, bool removable, bool? seekPenalty, DiskKind expected) =>
        Assert.Equal(expected, WindowsStorageTopologyReader.Classify(bus, removable, seekPenalty));

    // ---- the volumes ----

    private static StorageCollector.VolumeInfo Vol(string letter, long total, long free, params (int Disk, long Start, long Length)[] extents) =>
        new(letter, "", "NTFS", Value.Ok(total, "fake"), Value.Ok(free, "fake"), extents, null);

    [Fact]
    public void The_same_volume_under_two_letters_is_one_entry_and_the_first_letter_wins()
    {
        var folded = WindowsStorageTopologyReader.Fold(
        [
            Vol("C:", 1000, 400, (0, 1024, 1000)),
            Vol("D:", 1000, 400, (0, 1024, 1000)),
            Vol("E:", 500, 100, (1, 0, 500)),
        ]);

        Assert.Equal(["C:", "E:"], folded.Select(static v => v.Letter));
        Assert.Equal(600, folded[0].UsedBytes);
        Assert.Equal([0], folded[0].DriveNumbers);
        Assert.False(folded[0].SpansDrives);
    }

    [Fact]
    public void A_volume_on_two_drives_is_listed_once_and_spans()
    {
        var folded = WindowsStorageTopologyReader.Fold([Vol("S:", 2000, 500, (2, 0, 1000), (3, 0, 1000))]);

        var v = Assert.Single(folded);
        Assert.Equal([2, 3], v.DriveNumbers);
        Assert.True(v.SpansDrives);
    }

    [Fact]
    public void Removable_unreadable_and_extentless_volumes_are_left_out_not_counted_as_empty()
    {
        var removable = Vol("F:", 100, 10, (4, 0, 100)) with { Fixed = false };
        var noSize = new StorageCollector.VolumeInfo("G:", "", "NTFS", Value.Denied<long>("x", "denied"), Value.Ok(1L, "fake"), [(5, 0, 1)], null);
        var noExtents = Vol("H:", 100, 10);
        var impossible = Vol("I:", 100, 200, (6, 0, 100));
        var zero = Vol("J:", 0, 0, (7, 0, 1));

        Assert.Empty(WindowsStorageTopologyReader.Fold([removable, noSize, noExtents, impossible, zero]));
    }

    // ---- the drive number match ----

    [Theory]
    [InlineData("PhysicalDrive0", true, 0)]
    [InlineData("PhysicalDrive12", true, 12)]
    [InlineData("PhysicalDrive", false, 0)]
    [InlineData("PhysicalDrive-1", false, 0)]
    [InlineData("PhysicalDrive3x", false, 0)]
    [InlineData("0 C: D:", false, 0)]
    [InlineData("physicaldrive1", false, 0)]
    [InlineData(null, false, 0)]
    public void The_drive_number_is_read_from_the_physical_disk_id_only(string? id, bool ok, int expected)
    {
        Assert.Equal(ok, StorageTopology.TryDriveNumber(id, out var n));
        Assert.Equal(expected, n);
    }

    [Fact]
    public void Volumes_on_a_drive_exclude_a_volume_that_spans_and_are_empty_when_unread()
    {
        var drives = new[] { new StorageDrive(0, DiskKind.Nvme, 1000, "a|1"), new StorageDrive(1, DiskKind.Hdd, 2000, "b|2") };
        var topo = new StorageTopology(drives,
        [
            new StorageVolumeUse("C:", 1000, 500, [0]),
            new StorageVolumeUse("S:", 2000, 500, [0, 1]),
            new StorageVolumeUse("E:", 500, 100, [1]),
        ]);

        Assert.Equal(["C:"], topo.VolumesOn(0).Select(static v => v.Letter));
        Assert.Equal(["E:"], topo.VolumesOn(1).Select(static v => v.Letter));
        Assert.Empty(topo.VolumesOn(9));
        Assert.Empty(new StorageTopology(drives, null).VolumesOn(0));
    }

    // ---- the contract ----

    [Fact]
    public void The_storage_topology_never_reaches_the_json_so_the_snapshot_is_unchanged()
    {
        var snapshot = FakeSnapshots.Create(Fixture.FixedTime, tick: 2);
        var topo = new StorageTopology([new StorageDrive(0, DiskKind.Nvme, 1000, "a|1")], [new StorageVolumeUse("C:", 1000, 500, [0])]);
        var withStorage = snapshot with { System = snapshot.System with { Storage = topo } };

        Assert.NotNull(withStorage.System.Storage);
        var plain = PcMieruJson.Serialize(snapshot);
        var json = PcMieruJson.Serialize(withStorage);
        Assert.Equal(plain, json);
        Assert.DoesNotContain("storage\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("driveNumbers", json, StringComparison.OrdinalIgnoreCase);
        Assert.Null(PcMieruJson.Deserialize<Snapshot>(json)!.System.Storage);
    }

    [Fact]
    public void A_snapshot_made_without_a_source_says_nothing_about_storage() =>
        Assert.Null(FakeSnapshots.Create(Fixture.FixedTime, tick: 2).System.Storage);

    // ---- the refresher ----

    private sealed class FakeReader : IStorageTopologyReader
    {
        public IReadOnlyList<StorageDrive>? Drives { get; set; } = [new StorageDrive(0, DiskKind.Nvme, 1000, "a|1")];

        public IReadOnlyList<StorageVolumeUse>? Volumes { get; set; } = [new StorageVolumeUse("C:", 1000, 500, [0])];

        public bool Throw { get; set; }

        public int DriveReads { get; private set; }

        public int VolumeReads { get; private set; }

        /// <summary>Runs inside <see cref="ReadDrives"/>, after the read has started: the moment a row can change under a read in flight.</summary>
        public Action? DuringDriveRead { get; set; }

        public IReadOnlyList<StorageDrive>? ReadDrives()
        {
            DriveReads++;
            DuringDriveRead?.Invoke();
            return Throw ? throw new InvalidOperationException("boom") : Drives;
        }

        public IReadOnlyList<StorageVolumeUse>? ReadVolumes()
        {
            VolumeReads++;
            return Throw ? throw new InvalidOperationException("boom") : Volumes;
        }
    }

    private static DiskInfo Row(int number) =>
        new($"PhysicalDrive{number}", $"{number} C:", Value.Ok(1.0, "fake"), Value.Ok(1.0, "fake"), Value.Ok(1.0, "fake"), Value.Ok(1.0, "fake"));

    [Fact]
    public void Nothing_is_known_before_the_first_read()
    {
        using var refresher = new StorageTopologyRefresher(new FakeReader(), () => 0);
        Assert.Null(refresher.Current);
    }

    [Fact]
    public void The_first_pass_reads_both_and_publishes()
    {
        var reader = new FakeReader();
        using var refresher = new StorageTopologyRefresher(reader, () => 0);

        refresher.Step();

        var t = refresher.Current;
        Assert.NotNull(t);
        Assert.Equal(DiskKind.Nvme, Assert.Single(t.Drives).Kind);
        Assert.Equal("C:", Assert.Single(t.Volumes!).Letter);
        Assert.Equal((1, 1), (reader.DriveReads, reader.VolumeReads));
    }

    [Fact]
    public void Volumes_are_read_every_thirty_seconds_and_the_drives_every_sixty()
    {
        long now = 0;
        var reader = new FakeReader();
        using var refresher = new StorageTopologyRefresher(reader, () => now);

        refresher.Step();
        now = 29_999;
        refresher.Step();
        Assert.Equal((1, 1), (reader.DriveReads, reader.VolumeReads));

        now = 30_000;
        refresher.Step();
        Assert.Equal((1, 2), (reader.DriveReads, reader.VolumeReads));

        now = 59_999;
        refresher.Step();
        Assert.Equal((1, 2), (reader.DriveReads, reader.VolumeReads));

        now = 60_000;
        refresher.Step();
        Assert.Equal((2, 3), (reader.DriveReads, reader.VolumeReads));
    }

    [Fact]
    public void A_failed_drive_read_makes_the_kinds_unknown_and_a_later_good_one_brings_them_back()
    {
        long now = 0;
        var reader = new FakeReader();
        using var refresher = new StorageTopologyRefresher(reader, () => now);
        refresher.Step();
        Assert.NotNull(refresher.Current);

        reader.Drives = null;
        now = 60_000;
        refresher.Step();
        Assert.Null(refresher.Current);

        reader.Drives = [new StorageDrive(0, DiskKind.Ssd, 1000, "a|1")];
        now = 120_000;
        refresher.Step();
        Assert.Equal(DiskKind.Ssd, Assert.Single(refresher.Current!.Drives).Kind);
    }

    [Fact]
    public void A_reader_that_throws_is_the_same_as_could_not_read()
    {
        var reader = new FakeReader { Throw = true };
        using var refresher = new StorageTopologyRefresher(reader, () => 0);

        refresher.Step();

        Assert.Null(refresher.Current);
    }

    [Fact]
    public void Unreadable_volumes_keep_the_drive_kinds_and_say_the_volumes_are_unknown()
    {
        var reader = new FakeReader { Volumes = null };
        using var refresher = new StorageTopologyRefresher(reader, () => 0);

        refresher.Step();

        Assert.Equal(DiskKind.Nvme, Assert.Single(refresher.Current!.Drives).Kind);
        Assert.Null(refresher.Current.Volumes);
    }

    [Fact]
    public void A_row_with_no_drive_here_reads_the_drives_again_but_not_more_often_than_every_ten_seconds()
    {
        long now = 0;
        var reader = new FakeReader();
        using var refresher = new StorageTopologyRefresher(reader, () => now);
        refresher.Step();

        // The same numbers: nothing to ask for.
        refresher.NoteDisks([Row(0)]);
        now = 1_000;
        refresher.Step();
        Assert.Equal(1, reader.DriveReads);

        // A disk row for a drive that is not known (a device came): re-read, but only after the gap.
        refresher.NoteDisks([Row(0), Row(1)]);
        now = 9_999;
        refresher.Step();
        Assert.Equal(1, reader.DriveReads);

        reader.Drives = [new StorageDrive(0, DiskKind.Nvme, 1000, "a|1"), new StorageDrive(1, DiskKind.Hdd, 2000, "b|2")];
        now = 10_000;
        refresher.Step();
        Assert.Equal(2, reader.DriveReads);
        Assert.Equal(2, refresher.Current!.Drives.Count);

        // Now the rows and the drives agree again, so no further request.
        refresher.NoteDisks([Row(0), Row(1)]);
        now = 25_000;
        refresher.Step();
        Assert.Equal(2, reader.DriveReads);
    }

    [Fact]
    public void A_drive_number_reused_by_another_device_is_seen_at_the_next_drive_check()
    {
        // The number 1 was a hard disk; it is unplugged and an NVMe takes the number. The kind follows the new device, never the old one.
        long now = 0;
        var reader = new FakeReader { Drives = [new StorageDrive(0, DiskKind.Nvme, 1000, "a|1"), new StorageDrive(1, DiskKind.Hdd, 2000, "old|9")] };
        using var refresher = new StorageTopologyRefresher(reader, () => now);
        refresher.Step();
        Assert.Equal(DiskKind.Hdd, refresher.Current!.Drives[1].Kind);

        reader.Drives = [new StorageDrive(0, DiskKind.Nvme, 1000, "a|1"), new StorageDrive(1, DiskKind.Nvme, 4000, "new|7")];
        now = StorageTopologyRefresher.DriveEveryMs;
        refresher.Step();

        var drive = refresher.Current!.Drives[1];
        Assert.Equal(DiskKind.Nvme, drive.Kind);
        Assert.Equal("new|7", drive.Identity);
    }

    [Fact]
    public void Noting_disks_before_anything_is_known_asks_for_nothing()
    {
        var reader = new FakeReader();
        using var refresher = new StorageTopologyRefresher(reader, () => 0);

        refresher.NoteDisks([Row(0)]);
        refresher.Step();

        Assert.Equal(1, reader.DriveReads);
        Assert.NotNull(refresher.Current);
    }

    [Fact]
    public async Task Dispose_stops_the_loop_and_it_reads_nothing_after_that()
    {
        var reader = new FakeReader();
        var refresher = new StorageTopologyRefresher(reader, () => 0);
        refresher.Start();
        await WaitFor(() => refresher.Current is not null);

        refresher.Dispose();
        await refresher.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(refresher.Completion.IsCompleted);

        var reads = reader.DriveReads;
        refresher.Step();
        refresher.Start();
        Assert.Equal(reads, reader.DriveReads);
        Assert.True(refresher.Completion.IsCompleted);
    }

    [Fact]
    public void Dispose_before_start_means_start_never_runs_anything()
    {
        var reader = new FakeReader();
        var refresher = new StorageTopologyRefresher(reader, () => 0);

        refresher.Dispose();
        refresher.Start();

        Assert.True(refresher.Completion.IsCompleted);
        Assert.Equal(0, reader.DriveReads);
        Assert.Null(refresher.Current);
    }

    [Fact]
    public void A_pass_that_finishes_after_dispose_publishes_nothing()
    {
        var reader = new DisposingReader();
        var refresher = new StorageTopologyRefresher(reader, () => 0);
        reader.Owner = refresher;

        refresher.Step();

        Assert.Null(refresher.Current);
    }

    [Fact]
    public void Dispose_during_the_drive_read_does_not_start_the_volume_read_and_publishes_nothing()
    {
        var reader = new DisposingReader();
        var refresher = new StorageTopologyRefresher(reader, () => 0);
        reader.Owner = refresher;

        refresher.Step();

        Assert.Equal((1, 0), (reader.DriveReads, reader.VolumeReads));
        Assert.Null(refresher.Current);
        Assert.Null(refresher.Settled);
    }

    [Fact]
    public void Dispose_during_the_volume_read_publishes_nothing()
    {
        var reader = new DisposingReader { DisposeInVolumes = true };
        var refresher = new StorageTopologyRefresher(reader, () => 0);
        reader.Owner = refresher;

        refresher.Step();

        Assert.Equal((1, 1), (reader.DriveReads, reader.VolumeReads));
        Assert.Null(refresher.Current);
        Assert.Null(refresher.Settled);
    }

    [Fact]
    public void After_dispose_the_published_topology_is_gone()
    {
        var refresher = new StorageTopologyRefresher(new FakeReader(), () => 0);
        refresher.Step();
        Assert.NotNull(refresher.Current);
        Assert.NotNull(refresher.Settled);

        refresher.Dispose();

        Assert.Null(refresher.Current);
        Assert.Null(refresher.Settled);
    }

    private sealed class DisposingReader : IStorageTopologyReader
    {
        public StorageTopologyRefresher? Owner { get; set; }

        public bool DisposeInVolumes { get; set; }

        public int DriveReads { get; private set; }

        public int VolumeReads { get; private set; }

        public IReadOnlyList<StorageDrive>? ReadDrives()
        {
            DriveReads++;
            if (!DisposeInVolumes)
            {
                Owner?.Dispose();
            }

            return [new StorageDrive(0, DiskKind.Nvme, 1, "a|1")];
        }

        public IReadOnlyList<StorageVolumeUse>? ReadVolumes()
        {
            VolumeReads++;
            if (DisposeInVolumes)
            {
                Owner?.Dispose();
            }

            return [];
        }
    }

    // ---- volumes are never read closer than thirty seconds, even when an invalidation forces the drives early ----

    [Fact]
    public void An_early_drive_read_does_not_read_the_volumes_and_publishes_them_as_unknown_until_they_are_due()
    {
        long now = 0;
        var reader = new FakeReader();
        using var refresher = new StorageTopologyRefresher(reader, () => now);
        refresher.Step();
        Assert.Equal((1, 1), (reader.DriveReads, reader.VolumeReads));
        refresher.NoteDisks([Row(0)]);

        reader.Drives = [new StorageDrive(0, DiskKind.Nvme, 1000, "a|1"), new StorageDrive(1, DiskKind.Hdd, 2000, "b|2")];
        refresher.NoteDisks([Row(0), Row(1)]);
        now = 10_000;
        refresher.Step();

        Assert.Equal((2, 1), (reader.DriveReads, reader.VolumeReads));
        Assert.Equal(2, refresher.Current!.Drives.Count);
        Assert.Null(refresher.Current.Volumes);

        now = 29_999;
        refresher.Step();
        Assert.Equal((2, 1), (reader.DriveReads, reader.VolumeReads));
        Assert.Null(refresher.Current.Volumes);

        now = 30_000;
        refresher.Step();
        Assert.Equal((2, 2), (reader.DriveReads, reader.VolumeReads));
        Assert.Equal("C:", Assert.Single(refresher.Current!.Volumes!).Letter);
        Assert.Equal(2, refresher.Current.Drives.Count);
    }

    // ---- Settled: a drive number that changes hands never earns for the old kind ----

    [Fact]
    public void The_first_note_of_the_disks_nulls_nothing()
    {
        var reader = new FakeReader();
        using var refresher = new StorageTopologyRefresher(reader, () => 0);
        refresher.Step();

        refresher.NoteDisks([Row(0)]);

        Assert.NotNull(refresher.Settled);
        Assert.Same(refresher.Current, refresher.Settled);

        // Even a first look that disagrees with the known drives is only a request to read again, not a change since the last frame.
        using var other = new StorageTopologyRefresher(new FakeReader(), () => 0);
        other.Step();
        other.NoteDisks([Row(0), Row(1)]);
        Assert.Same(other.Current, other.Settled);
    }

    [Fact]
    public void Unchanged_rows_leave_settled_equal_to_current_through_every_kind_of_pass()
    {
        long now = 0;
        var reader = new FakeReader();
        using var refresher = new StorageTopologyRefresher(reader, () => now);

        foreach (var t in new long[] { 0, 1_000, 30_000, 60_000, 90_000, 120_000 })
        {
            now = t;
            refresher.NoteDisks([Row(0)]);
            refresher.Step();
            Assert.NotNull(refresher.Settled);
            Assert.Same(refresher.Current, refresher.Settled);
        }

        Assert.Equal(3, reader.DriveReads);
    }

    [Fact]
    public void A_drive_number_that_vanishes_and_comes_back_is_unsettled_until_a_read_that_started_after_it_is_published()
    {
        long now = 0;
        var reader = new FakeReader { Drives = [new StorageDrive(0, DiskKind.Nvme, 1000, "a|1"), new StorageDrive(1, DiskKind.Hdd, 2000, "old|9")] };
        using var refresher = new StorageTopologyRefresher(reader, () => now);
        refresher.Step();
        refresher.NoteDisks([Row(0), Row(1)]);
        Assert.Same(refresher.Current, refresher.Settled);

        // The hard disk is pulled and another drive takes the number: the number is missing from one frame, then back.
        now = 1_000;
        refresher.NoteDisks([Row(0)]);
        Assert.Null(refresher.Settled);
        refresher.NoteDisks([Row(0), Row(1)]);
        Assert.Null(refresher.Settled);
        Assert.Equal(DiskKind.Hdd, refresher.Current!.Drives[1].Kind);

        // Still inside the gap: nothing was read, so nothing is settled; the old answer is not served as settled either.
        reader.Drives = [new StorageDrive(0, DiskKind.Nvme, 1000, "a|1"), new StorageDrive(1, DiskKind.Nvme, 4000, "new|7")];
        now = 9_999;
        refresher.Step();
        Assert.Equal(1, reader.DriveReads);
        Assert.Null(refresher.Settled);

        now = 10_000;
        refresher.Step();
        Assert.Equal(2, reader.DriveReads);
        var settled = refresher.Settled;
        Assert.NotNull(settled);
        Assert.Equal(DiskKind.Nvme, settled.Drives[1].Kind);
        Assert.Equal("new|7", settled.Drives[1].Identity);
        Assert.Null(settled.Volumes);

        // And it stays settled while the rows stay as they are.
        refresher.NoteDisks([Row(0), Row(1)]);
        Assert.Same(refresher.Current, refresher.Settled);
    }

    [Fact]
    public void A_read_that_started_before_a_change_and_was_published_after_it_stays_unsettled()
    {
        long now = 0;
        var reader = new FakeReader { Drives = [new StorageDrive(0, DiskKind.Nvme, 1000, "a|1"), new StorageDrive(1, DiskKind.Hdd, 2000, "old|9")] };
        using var refresher = new StorageTopologyRefresher(reader, () => now);
        refresher.Step();
        refresher.NoteDisks([Row(0), Row(1)]);

        // The regular pass starts; while it is inside the OS call the number 1 vanishes from the rows. The read may have seen either side.
        now = StorageTopologyRefresher.DriveEveryMs;
        reader.DuringDriveRead = () => refresher.NoteDisks([Row(0)]);
        refresher.Step();
        reader.DuringDriveRead = null;

        Assert.NotNull(refresher.Current);
        Assert.Null(refresher.Settled);

        // The change asked for another read (the flag was cleared before the read began, so this one is not lost); one that starts later settles it.
        reader.Drives = [new StorageDrive(0, DiskKind.Nvme, 1000, "a|1")];
        now += StorageTopologyRefresher.InvalidateGapMs;
        refresher.Step();
        Assert.Equal(3, reader.DriveReads);
        Assert.Same(refresher.Current, refresher.Settled);
        Assert.Single(refresher.Settled!.Drives);
    }

    [Fact]
    public void A_volume_read_waits_while_the_rows_have_changed_and_the_drives_have_not_been_read_again()
    {
        long now = 0;
        var reader = new FakeReader();
        using var refresher = new StorageTopologyRefresher(reader, () => now);
        refresher.Step();
        refresher.NoteDisks([Row(0)]);

        // A change at 20 s: an early drive read at 25 s (volumes not due; the same drives, so the cached volumes stay).
        now = 20_000;
        refresher.NoteDisks([Row(0), Row(1)]);
        now = 25_000;
        refresher.Step();
        Assert.Equal((2, 1), (reader.DriveReads, reader.VolumeReads));
        Assert.Same(refresher.Current, refresher.Settled);

        // Another change at 28 s. At 30 s the volumes are due but the drive gap has not passed: no volume-only read, which would be thrown away and
        // would use up the spacing. The old read is not settled by anything.
        now = 28_000;
        refresher.NoteDisks([Row(0)]);
        now = 30_000;
        refresher.Step();
        Assert.Equal((2, 1), (reader.DriveReads, reader.VolumeReads));
        Assert.Null(refresher.Settled);
        now = 34_999;
        refresher.Step();
        Assert.Equal((2, 1), (reader.DriveReads, reader.VolumeReads));

        // The drives are read again (the gap has passed) and the volumes, long due, are read with them.
        now = 35_000;
        refresher.Step();
        Assert.Equal((3, 2), (reader.DriveReads, reader.VolumeReads));
        Assert.Same(refresher.Current, refresher.Settled);
        Assert.NotNull(refresher.Settled!.Volumes);
    }

    [Fact]
    public void A_tick_uses_the_topology_it_captured_before_its_window_and_only_while_no_change_has_been_noted()
    {
        long now = 0;
        var reader = new FakeReader();
        using var refresher = new StorageTopologyRefresher(reader, () => now);

        // Nothing published at the top of the tick: a topology that lands before the window is noted is not used for this tick.
        var none = refresher.Capture();
        refresher.Step();
        refresher.NoteDisks([Row(0)]);
        Assert.NotNull(refresher.Settled);
        Assert.Null(refresher.SettledFor(none));

        // The tick holds the first topology; a regular pass publishes a newer one (same generation) before the tick's NoteDisks.
        var captured = refresher.Capture();
        now = StorageTopologyRefresher.DriveEveryMs;
        reader.Drives = [new StorageDrive(0, DiskKind.Ssd, 1000, "a|1")];
        refresher.Step();
        refresher.NoteDisks([Row(0)]);
        Assert.Equal(DiskKind.Ssd, refresher.Settled!.Drives[0].Kind);
        Assert.Same(captured!.Topology, refresher.SettledFor(captured));
        Assert.Equal(DiskKind.Nvme, refresher.SettledFor(captured)!.Drives[0].Kind);

        // The same tick's rows changed: the captured topology is dropped, and so is the newer one until a read that started after the change lands.
        var again = refresher.Capture();
        refresher.NoteDisks([Row(0), Row(1)]);
        Assert.Null(refresher.SettledFor(again));
        Assert.Null(refresher.Settled);
    }

    [Fact]
    public void A_number_the_reader_never_finds_is_asked_for_once_and_not_every_ten_seconds()
    {
        long now = 0;
        var reader = new FakeReader();
        using var refresher = new StorageTopologyRefresher(reader, () => now);
        refresher.Step();

        // The first frame lists number 64, which the reader does not report: one early read, which does not resolve it.
        refresher.NoteDisks([Row(0), Row(64)]);
        now = 10_000;
        refresher.Step();
        Assert.Equal((2, 1), (reader.DriveReads, reader.VolumeReads));

        // The same rows on every later frame: not asked for again, the volumes get read when due and stay known.
        foreach (var t in new long[] { 11_000, 20_000, 30_000, 40_000, 59_000 })
        {
            now = t;
            refresher.NoteDisks([Row(0), Row(64)]);
            refresher.Step();
            Assert.Equal(2, reader.DriveReads);
        }

        Assert.Equal(2, reader.VolumeReads);
        Assert.NotNull(refresher.Current!.Volumes);
        Assert.Same(refresher.Current, refresher.Settled);

        // The regular pass still looks again (at 70 s, a minute after the early read); so does a change in the rows.
        now = 69_999;
        refresher.NoteDisks([Row(0), Row(64)]);
        refresher.Step();
        Assert.Equal(2, reader.DriveReads);
        now = 70_000;
        refresher.NoteDisks([Row(0), Row(64)]);
        refresher.Step();
        Assert.Equal(3, reader.DriveReads);

        refresher.NoteDisks([Row(0), Row(64), Row(65)]);
        now = 80_000;
        refresher.Step();
        Assert.Equal(4, reader.DriveReads);
    }

    [Fact]
    public void An_early_drive_read_that_finds_the_same_drives_keeps_the_volumes_and_one_that_finds_other_drives_does_not()
    {
        long now = 0;
        var reader = new FakeReader { Drives = [new StorageDrive(0, DiskKind.Nvme, 1000, "a|1"), new StorageDrive(1, DiskKind.Hdd, 2000, "b|2")] };
        using var refresher = new StorageTopologyRefresher(reader, () => now);
        refresher.Step();
        refresher.NoteDisks([Row(0), Row(1)]);

        // A row flickers: an early read at 10 s finds the same number-to-identity map, so the volumes (read at 0) are not thrown away.
        refresher.NoteDisks([Row(0)]);
        refresher.NoteDisks([Row(0), Row(1)]);
        now = 10_000;
        refresher.Step();
        Assert.Equal((2, 1), (reader.DriveReads, reader.VolumeReads));
        Assert.Equal("C:", Assert.Single(refresher.Current!.Volumes!).Letter);
        Assert.Same(refresher.Current, refresher.Settled);

        // The same number now names another device: the old volumes may name the old drive.
        reader.Drives = [new StorageDrive(0, DiskKind.Nvme, 1000, "a|1"), new StorageDrive(1, DiskKind.Nvme, 4000, "new|7")];
        refresher.NoteDisks([Row(0)]);
        refresher.NoteDisks([Row(0), Row(1)]);
        now = 20_000;
        refresher.Step();
        Assert.Equal((3, 1), (reader.DriveReads, reader.VolumeReads));
        Assert.Null(refresher.Current!.Volumes);
        Assert.Same(refresher.Current, refresher.Settled);

        now = 30_000;
        refresher.Step();
        Assert.Equal((3, 2), (reader.DriveReads, reader.VolumeReads));
        Assert.NotNull(refresher.Current!.Volumes);
    }

    [Fact]
    public void The_regular_drive_pass_reads_the_volumes_even_when_the_last_volume_read_was_just_under_thirty_seconds_ago()
    {
        long now = 0;
        var reader = new FakeReader();
        using var refresher = new StorageTopologyRefresher(reader, () => now);
        refresher.Step();

        // The volume read ran a poll late (30.5 s), so the regular pass at 60 s is only 29.5 s after it.
        now = 30_500;
        refresher.Step();
        now = 60_000;
        refresher.Step();

        Assert.Equal((2, 3), (reader.DriveReads, reader.VolumeReads));
        Assert.NotNull(refresher.Current!.Volumes);
    }

    [Fact]
    public void An_outage_of_the_disk_rows_is_one_change_when_it_starts_and_one_when_it_ends_and_not_one_per_frame()
    {
        long now = 0;
        var reader = new FakeReader();
        using var refresher = new StorageTopologyRefresher(reader, () => now);
        refresher.Step();
        refresher.NoteDisks([Row(0)]);

        // The rows go away (an unreadable PDH window is passed as no rows): a change, so the old read is no longer settled.
        now = 1_000;
        refresher.NoteDisks([]);
        Assert.Null(refresher.Settled);
        now = 10_000;
        refresher.Step();
        Assert.Equal(2, reader.DriveReads);
        Assert.Same(refresher.Current, refresher.Settled);

        // Empty after empty is no change and, the last read having started with these rows, no mismatch to ask about.
        foreach (var t in new long[] { 11_000, 15_000, 25_000, 35_000, 45_000 })
        {
            now = t;
            refresher.NoteDisks([]);
            refresher.Step();
            Assert.Equal(2, reader.DriveReads);
            Assert.Same(refresher.Current, refresher.Settled);
        }

        // The rows come back: a change again (the number may belong to another drive now), settled by the next read.
        now = 46_000;
        refresher.NoteDisks([Row(0)]);
        Assert.Null(refresher.Settled);
        now = 56_000;
        refresher.Step();
        Assert.Equal(3, reader.DriveReads);
        Assert.Same(refresher.Current, refresher.Settled);
    }

    [Fact]
    public void A_change_noted_while_nothing_is_published_still_outdates_the_read_in_flight()
    {
        var reader = new FakeReader();
        using var refresher = new StorageTopologyRefresher(reader, () => 0);
        refresher.NoteDisks([Row(0)]);
        reader.DuringDriveRead = () => refresher.NoteDisks([Row(0), Row(1)]);

        refresher.Step();

        Assert.NotNull(refresher.Current);
        Assert.Null(refresher.Settled);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    // ---- wired into the source ----

    [Fact]
    public async Task The_source_publishes_the_topology_and_Dispose_stops_its_refresher()
    {
        var reader = new FakeReader();
        var source = new AggregateSnapshotSource(static () => DateTime.UtcNow, null, static () => new NoPdh(), new NoGpu(), static () => 0L, null, reader);

        // The first sample starts the loop; the topology shows up on a later one (never waited for on the sampling thread).
        _ = await source.SampleAsync();
        await WaitFor(() => reader.DriveReads > 0);
        Snapshot last = await source.SampleAsync();
        for (var i = 0; i < 200 && last.System.Storage is null; i++)
        {
            await Task.Delay(10);
            last = await source.SampleAsync();
        }

        Assert.Equal(DiskKind.Nvme, Assert.Single(last.System.Storage!.Drives).Kind);

        source.Dispose();
        await Task.Delay(50);
        var reads = reader.DriveReads;
        await Task.Delay(1_200);
        Assert.Equal(reads, reader.DriveReads);
    }

    [Fact]
    public async Task A_source_made_without_a_storage_reader_has_no_topology_and_starts_no_thread()
    {
        using var source = new AggregateSnapshotSource(static () => DateTime.UtcNow, null, static () => new NoPdh(), new NoGpu(), static () => 0L, null);

        Assert.Null((await source.SampleAsync()).System.Storage);
    }

    private sealed class NoPdh : IPdhSampling
    {
        public bool IsOpen => true;

        public void Reset()
        {
        }

        public PdhSystemReading Sample() => new([], Value.Ok(3000.0, "fake"), Value.Ok(0, "fake"));

        public void Dispose()
        {
        }
    }

    private sealed class NoGpu : IGpuSampling
    {
        public void Reset()
        {
        }

        public GpuReading Sample(bool active) => new([], new Dictionary<uint, double>(), true, null, Value.Ok(0, "fake"));

        public void Dispose()
        {
        }
    }
}
