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

        public IReadOnlyList<StorageDrive>? ReadDrives()
        {
            DriveReads++;
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

    private sealed class DisposingReader : IStorageTopologyReader
    {
        public StorageTopologyRefresher? Owner { get; set; }

        public IReadOnlyList<StorageDrive>? ReadDrives()
        {
            Owner?.Dispose();
            return [new StorageDrive(0, DiskKind.Nvme, 1, "a|1")];
        }

        public IReadOnlyList<StorageVolumeUse>? ReadVolumes() => [];
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
