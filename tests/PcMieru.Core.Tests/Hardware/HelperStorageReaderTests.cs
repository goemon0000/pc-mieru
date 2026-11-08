using PcMieru.Core.Hardware;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Storage;
using PcMieru.Wire;

namespace PcMieru.Core.Tests.Hardware;

/// <summary>
/// The adapter between the elevated Helper's base64 answer and the health parsers. The Helper itself is never started here (that would
/// raise a UAC prompt); a stand-in client returns what the real one is documented to return.
/// </summary>
public class HelperStorageReaderTests
{
    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes);

    private static byte[] SmartTable(byte attributeId, byte current, byte worst, byte raw)
    {
        var t = new byte[512];
        t[0] = 0x10;
        t[2] = attributeId;
        t[5] = current;
        t[6] = worst;
        t[7] = raw;
        return t;
    }

    private static byte[] Thresholds(byte attributeId, byte threshold)
    {
        var t = new byte[512];
        t[2] = attributeId;
        t[3] = threshold;
        return t;
    }

    private static Dictionary<string, string> Data(params (string Key, string Value)[] items) =>
        items.ToDictionary(i => i.Key, i => i.Value);

    private sealed class StandIn(OperationCode code, Dictionary<string, string>? data, bool declined = false) : IHelperClient
    {
        public List<HelperRequest> Requests { get; } = [];

        public Task<HelperOutcome> RunAsync(HelperRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            var result = data is null && !declined ? null : new HelperResult(request.Nonce, HelperOperations.Get(request.Op).Name, code, null, data);
            return Task.FromResult(new HelperOutcome(code, declined, result));
        }
    }

    [Fact]
    public async Task SataAskedForSmartReadWithTheDriveNumberAndReturnsTableThenThresholds()
    {
        var table = SmartTable(9, 99, 99, 42);
        var limits = Thresholds(9, 0);
        var client = new StandIn(OperationCode.Ok, Data((HelperDataKeys.SmartData, B64(table)), (HelperDataKeys.SmartThresholds, B64(limits))));
        var reader = new HelperStorageReader(() => client);

        var raw = await reader.ReadRawAsync(3, StorageHealthKind.Sata, CancellationToken.None);

        var request = Assert.Single(client.Requests);
        Assert.Equal(HelperOperation.SmartRead, request.Op);
        Assert.Equal(3, request.DriveNumber);
        Assert.NotNull(raw);
        Assert.Equal(1024, raw!.Length);
        Assert.Equal(table, raw[..512]);
        Assert.Equal(limits, raw[512..]);
    }

    [Fact]
    public async Task SataWithoutThresholdsReturnsJustTheTable()
    {
        var table = SmartTable(9, 99, 99, 42);
        var client = new StandIn(OperationCode.Ok, Data((HelperDataKeys.SmartData, B64(table))));

        var raw = await new HelperStorageReader(() => client).ReadRawAsync(1, StorageHealthKind.Sata, CancellationToken.None);

        Assert.Equal(table, raw);
    }

    [Fact]
    public async Task ADamagedThresholdSectorIsLeftOutNotPassedOnAsIfItWereOne()
    {
        var table = SmartTable(9, 99, 99, 42);
        var client = new StandIn(OperationCode.Ok, Data((HelperDataKeys.SmartData, B64(table)), (HelperDataKeys.SmartThresholds, B64(new byte[100]))));

        var raw = await new HelperStorageReader(() => client).ReadRawAsync(1, StorageHealthKind.Sata, CancellationToken.None);

        Assert.Equal(table, raw);
    }

    [Fact]
    public async Task NvmeAskedForNvmeHealthAndReturnsTheLogPage()
    {
        var page = new byte[512];
        page[3] = 100;
        var client = new StandIn(OperationCode.Ok, Data((HelperDataKeys.NvmeHealth, B64(page))));

        var raw = await new HelperStorageReader(() => client).ReadRawAsync(0, StorageHealthKind.Nvme, CancellationToken.None);

        Assert.Equal(HelperOperation.NvmeHealth, Assert.Single(client.Requests).Op);
        Assert.Equal(page, raw);
    }

    [Theory]
    [InlineData(OperationCode.Denied)]
    [InlineData(OperationCode.Unsupported)]
    [InlineData(OperationCode.Error)]
    [InlineData(OperationCode.NotImplemented)]
    public async Task AHelperThatDidNotSucceedGivesNothing(OperationCode code)
    {
        var table = SmartTable(9, 99, 99, 42);
        var client = new StandIn(code, Data((HelperDataKeys.SmartData, B64(table))));

        Assert.Null(await new HelperStorageReader(() => client).ReadRawAsync(1, StorageHealthKind.Sata, CancellationToken.None));
    }

    [Fact]
    public async Task ADeclinedUacPromptGivesNothing()
    {
        var client = new StandIn(OperationCode.Cancelled, null, declined: true);

        Assert.Null(await new HelperStorageReader(() => client).ReadRawAsync(1, StorageHealthKind.Sata, CancellationToken.None));
    }

    [Fact]
    public async Task SuccessWithoutDataGivesNothing()
    {
        // This is what the sample (Fake) Helper answers: Ok and no bytes.
        var client = new StandIn(OperationCode.Ok, null);

        Assert.Null(await new HelperStorageReader(() => client).ReadRawAsync(1, StorageHealthKind.Sata, CancellationToken.None));
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("")]
    [InlineData("AAAA")]
    public async Task BytesThatAreNotOneSectorAreRefused(string text)
    {
        var client = new StandIn(OperationCode.Ok, Data((HelperDataKeys.SmartData, text), (HelperDataKeys.NvmeHealth, text)));
        var reader = new HelperStorageReader(() => client);

        Assert.Null(await reader.ReadRawAsync(1, StorageHealthKind.Sata, CancellationToken.None));
        Assert.Null(await reader.ReadRawAsync(1, StorageHealthKind.Nvme, CancellationToken.None));
    }

    [Fact]
    public async Task TheOtherKindsKeyIsNotTakenForTheRequestedOne()
    {
        var client = new StandIn(OperationCode.Ok, Data((HelperDataKeys.NvmeHealth, B64(new byte[512]))));

        Assert.Null(await new HelperStorageReader(() => client).ReadRawAsync(1, StorageHealthKind.Sata, CancellationToken.None));
    }

    [Fact]
    public async Task WithoutAHelperRegisteredNothingIsAsked()
    {
        Assert.Null(await new HelperStorageReader(() => null).ReadRawAsync(1, StorageHealthKind.Sata, CancellationToken.None));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public async Task ADriveNumberTheHelperCouldNotAcceptIsNotSentAtAll(int drive)
    {
        var client = new StandIn(OperationCode.Ok, Data((HelperDataKeys.SmartData, B64(new byte[512]))));

        Assert.Null(await new HelperStorageReader(() => client).ReadRawAsync(drive, StorageHealthKind.Sata, CancellationToken.None));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task TheHelperIsLookedUpWhenAReadNeedsItNotWhenTheReaderIsMade()
    {
        IHelperClient? registered = null;
        var reader = new HelperStorageReader(() => registered);

        // Management registers the Helper after Hardware has built this reader.
        registered = new StandIn(OperationCode.Ok, Data((HelperDataKeys.SmartData, B64(SmartTable(9, 99, 99, 42)))));

        Assert.NotNull(await reader.ReadRawAsync(1, StorageHealthKind.Sata, CancellationToken.None));
    }

    [Fact]
    public async Task WhatTheAdapterReturnsIsWhatTheHealthParserExpects()
    {
        // The end of the chain on bytes: Helper answer -> adapter -> parser, with a table and its threshold sector.
        var table = SmartTable(5, 100, 100, 0);
        var limits = Thresholds(5, 36);
        var client = new StandIn(OperationCode.Ok, Data((HelperDataKeys.SmartData, B64(table)), (HelperDataKeys.SmartThresholds, B64(limits))));

        var raw = await new HelperStorageReader(() => client).ReadRawAsync(2, StorageHealthKind.Sata, CancellationToken.None);
        var health = StorageHealthParsers.FromSmartBlob(raw!);

        Assert.True(health.IsOk, health.Detail);
        var attribute = Assert.Single(health.Data!.Attributes);
        Assert.Equal(5, attribute.Id);
        Assert.Equal(36, attribute.Threshold);
    }

    // ---- several drives, one elevated run (disk-health-all) ----

    /// <summary>The Helper as the batch request documents it: every drive's keys under "drive.&lt;n&gt;.", its own code, and an overall code.</summary>
    private sealed class BatchStandIn(OperationCode overall, Func<int, Dictionary<string, string>?> perDrive, bool declined = false) : IHelperClient
    {
        public List<HelperRequest> Requests { get; } = [];

        public Task<HelperOutcome> RunAsync(HelperRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (declined)
            {
                return Task.FromResult(new HelperOutcome(OperationCode.Cancelled, true, null));
            }

            var data = new Dictionary<string, string>();
            foreach (var n in request.Drives?.Numbers ?? [])
            {
                var one = perDrive(n);
                data[HelperDataKeys.ForDrive(n, HelperDataKeys.DriveCode)] = one is null ? ((int)OperationCode.Denied).ToString() : "0";
                foreach (var (key, value) in one ?? [])
                {
                    data[HelperDataKeys.ForDrive(n, key)] = value;
                }
            }

            return Task.FromResult(new HelperOutcome(overall, false, new HelperResult(request.Nonce, HelperOperations.Get(request.Op).Name, overall, null, data)));
        }
    }

    private static Dictionary<string, string> SataDrive(byte raw, bool withThresholds = true)
    {
        var data = Data((HelperDataKeys.SmartData, B64(SmartTable(9, 99, 99, raw))));
        if (withThresholds)
        {
            data[HelperDataKeys.SmartThresholds] = B64(Thresholds(9, 0));
        }

        return data;
    }

    private static Dictionary<string, string> NvmeDrive(byte percentUsed)
    {
        var page = new byte[512];
        page[5] = percentUsed;
        return Data((HelperDataKeys.NvmeHealth, B64(page)));
    }

    private static readonly (int, StorageHealthKind)[] TwoSataAndANvme =
        [(2, StorageHealthKind.Sata), (0, StorageHealthKind.Nvme), (1, StorageHealthKind.Sata)];

    [Fact]
    public async Task SeveralDrivesAreAskedForInOneRequestWithTheDriveNumbersOnly()
    {
        var client = new BatchStandIn(OperationCode.Ok, n => n == 0 ? NvmeDrive(7) : SataDrive((byte)(40 + n)));

        var answers = await new HelperStorageReader(() => client).ReadRawManyAsync(TwoSataAndANvme, CancellationToken.None);

        var request = Assert.Single(client.Requests);
        Assert.Equal(HelperOperation.DiskHealthAll, request.Op);
        Assert.Equal([0, 1, 2], request.Drives!.Numbers);
        Assert.Null(request.DriveNumber);
        Assert.Equal(3, answers.Count);
        Assert.Equal(1024, answers[1]!.Length);
        Assert.Equal(1024, answers[2]!.Length);
        Assert.Equal(512, answers[0]!.Length);
        Assert.Equal(7, answers[0]![5]);

        // each drive got ITS OWN bytes (the raw attribute value differs per drive), not a neighbour's
        Assert.Equal(41, answers[1]![7]);
        Assert.Equal(42, answers[2]![7]);
    }

    [Fact]
    public async Task ADriveThatFailedInARunThatPartlyWorkedIsNullAndTheOthersKeepTheirBytes()
    {
        var client = new BatchStandIn(OperationCode.PartialSuccess, n => n == 1 ? null : n == 0 ? NvmeDrive(3) : SataDrive(50));

        var answers = await new HelperStorageReader(() => client).ReadRawManyAsync(TwoSataAndANvme, CancellationToken.None);

        Assert.Single(client.Requests);
        Assert.Null(answers[1]);
        Assert.NotNull(answers[0]);
        Assert.NotNull(answers[2]);
    }

    [Fact]
    public async Task ADriveWhoseOwnCodeIsNotOkIsNullEvenIfItsBytesAreThere()
    {
        // A tampered or confused result file: the exit code says Ok, the drive's own code says denied, but a table is sitting under its key.
        var client = new BatchStandIn(OperationCode.Ok, n => null);
        var withBytes = new HelperStorageReader(() => new Wrapped(client, d => d[HelperDataKeys.ForDrive(1, HelperDataKeys.SmartData)] = B64(SmartTable(9, 99, 99, 1))));

        var answers = await withBytes.ReadRawManyAsync([(1, StorageHealthKind.Sata)], CancellationToken.None);

        Assert.Null(answers[1]);
    }

    private sealed class Wrapped(IHelperClient inner, Action<Dictionary<string, string>> change) : IHelperClient
    {
        public async Task<HelperOutcome> RunAsync(HelperRequest request, CancellationToken ct = default)
        {
            var outcome = await inner.RunAsync(request, ct);
            change(outcome.Result!.Data!);
            return outcome;
        }
    }

    [Fact]
    public async Task ADeclinedPromptLeavesEveryDriveNullAfterOnePrompt()
    {
        var client = new BatchStandIn(OperationCode.Cancelled, _ => null, declined: true);

        var answers = await new HelperStorageReader(() => client).ReadRawManyAsync(TwoSataAndANvme, CancellationToken.None);

        Assert.Single(client.Requests);
        Assert.All(answers.Values, Assert.Null);
        Assert.Equal(3, answers.Count);
    }

    [Theory]
    [InlineData(OperationCode.Denied)]
    [InlineData(OperationCode.Error)]
    [InlineData(OperationCode.Unsupported)]
    public async Task ARunThatEndedInFailureGivesNothingForAnyDrive(OperationCode overall)
    {
        var client = new BatchStandIn(overall, n => SataDrive(1));

        var answers = await new HelperStorageReader(() => client).ReadRawManyAsync(TwoSataAndANvme, CancellationToken.None);

        Assert.All(answers.Values, Assert.Null);
    }

    [Fact]
    public async Task NumbersTheHelperWouldRefuseAndRepeatedNumbersNeverMakeARequestOfTheirOwn()
    {
        var client = new BatchStandIn(OperationCode.Ok, n => SataDrive(5));

        var answers = await new HelperStorageReader(() => client).ReadRawManyAsync(
            [(-1, StorageHealthKind.Sata), (256, StorageHealthKind.Sata), (3, StorageHealthKind.Sata), (3, StorageHealthKind.Sata)],
            CancellationToken.None);

        var request = Assert.Single(client.Requests);
        Assert.Equal([3], request.Drives!.Numbers);
        Assert.Equal([3], answers.Keys.ToArray());
        Assert.NotNull(answers[3]);
    }

    [Fact]
    public async Task NothingToAskOrNoHelperMeansNoRequest()
    {
        var client = new BatchStandIn(OperationCode.Ok, n => SataDrive(5));

        Assert.Empty(await new HelperStorageReader(() => client).ReadRawManyAsync([], CancellationToken.None));
        Assert.Empty(await new HelperStorageReader(() => client).ReadRawManyAsync([(-5, StorageHealthKind.Sata)], CancellationToken.None));
        Assert.Empty(client.Requests);

        var none = await new HelperStorageReader(() => null).ReadRawManyAsync(TwoSataAndANvme, CancellationToken.None);
        Assert.All(none.Values, Assert.Null);
    }

    [Fact]
    public async Task MoreDrivesThanOneRequestMayCarryAreSplitIntoFullBatches()
    {
        var client = new BatchStandIn(OperationCode.Ok, n => SataDrive(5));
        var drives = Enumerable.Range(0, DriveList.MaxCount + 4).Select(n => (n, StorageHealthKind.Sata)).ToArray();

        var answers = await new HelperStorageReader(() => client).ReadRawManyAsync(drives, CancellationToken.None);

        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(DriveList.MaxCount, client.Requests[0].Drives!.Numbers.Count);
        Assert.Equal(4, client.Requests[1].Drives!.Numbers.Count);
        Assert.All(answers.Values, Assert.NotNull);
    }

    [Fact]
    public async Task TheHealthProviderAsksOnceForAllTheDrivesThatWaitForTheHelper()
    {
        // Two SATA drives and a SATA drive that already has an answer, and one NVMe that waits too: ONE request, one prompt.
        var done = Value.Unsupported<StorageHealth>("test", "already answered");
        IReadOnlyList<RealStorageHealthProvider.Step> steps =
        [
            new(0, "Sata A", StorageHealthKind.Sata, null),
            new(1, "Answered", StorageHealthKind.Sata, done),
            new(2, "Sata B", StorageHealthKind.Sata, null),
            new(3, "Nvme C", StorageHealthKind.Nvme, null),
        ];
        var client = new BatchStandIn(OperationCode.Ok, n => n == 3 ? NvmeDrive(9) : SataDrive(5));

        var list = await RealStorageHealthProvider.ResolveAsync(steps, new HelperStorageReader(() => client), CancellationToken.None);

        var request = Assert.Single(client.Requests);
        Assert.Equal([0, 2, 3], request.Drives!.Numbers);
        Assert.Equal([0, 1, 2, 3], list.Select(d => d.PhysicalDriveNumber).ToArray());
        Assert.Equal(done, list[1].Health);
        Assert.True(list[0].Health.IsOk, list[0].Health.Detail);
        Assert.True(list[2].Health.IsOk, list[2].Health.Detail);
        Assert.True(list[3].Health.IsOk, list[3].Health.Detail);
        Assert.Equal(9.0, list[3].Health.Data!.PercentageUsed.Data);
    }

    [Fact]
    public async Task TheHealthProviderDoesNotAskAtAllWhenNothingWaits()
    {
        var client = new BatchStandIn(OperationCode.Ok, n => SataDrive(5));
        IReadOnlyList<RealStorageHealthProvider.Step> steps =
        [
            new(0, "Denied one", StorageHealthKind.Sata, Value.Denied<StorageHealth>("test", "not allowed")),
        ];

        var list = await RealStorageHealthProvider.ResolveAsync(steps, new HelperStorageReader(() => client), CancellationToken.None);

        Assert.Empty(client.Requests);
        Assert.Equal(ValueStatus.Denied, list[0].Health.Status);
    }

    [Fact]
    public async Task ADrivesThatCouldNotBeReadIsDeniedWithTheReasonNotZeros()
    {
        var client = new BatchStandIn(OperationCode.PartialSuccess, n => n == 1 ? null : SataDrive(5));
        IReadOnlyList<RealStorageHealthProvider.Step> steps =
        [
            new(0, "Good", StorageHealthKind.Sata, null),
            new(1, "Bad", StorageHealthKind.Sata, null),
        ];

        var list = await RealStorageHealthProvider.ResolveAsync(steps, new HelperStorageReader(() => client), CancellationToken.None);

        Assert.True(list[0].Health.IsOk);
        Assert.Equal(ValueStatus.Denied, list[1].Health.Status);
        Assert.Null(list[1].Health.Data);
    }

    [Fact]
    public async Task AReaderThatThrowsFailsEveryWaitingDriveAndLeavesTheAnsweredOnesAlone()
    {
        var done = Value.Unsupported<StorageHealth>("test", "already answered");
        IReadOnlyList<RealStorageHealthProvider.Step> steps =
        [
            new(0, "A", StorageHealthKind.Sata, null),
            new(1, "B", StorageHealthKind.Sata, done),
        ];

        var list = await RealStorageHealthProvider.ResolveAsync(steps, new ThrowingReader(), CancellationToken.None);

        Assert.Equal(ValueStatus.Error, list[0].Health.Status);
        Assert.Equal(done, list[1].Health);
    }

    private sealed class ThrowingReader : IElevatedStorageReader
    {
        public Task<byte[]?> ReadRawAsync(int physicalDriveNumber, StorageHealthKind kind, CancellationToken ct) => throw new InvalidOperationException("boom");

        public Task<IReadOnlyDictionary<int, byte[]?>> ReadRawManyAsync(IReadOnlyList<(int PhysicalDriveNumber, StorageHealthKind Kind)> drives, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class SingleOnlyReader(Dictionary<int, byte[]?> bytes) : IElevatedStorageReader
    {
        public List<int> Asked { get; } = [];

        public Task<byte[]?> ReadRawAsync(int physicalDriveNumber, StorageHealthKind kind, CancellationToken ct)
        {
            Asked.Add(physicalDriveNumber);
            return Task.FromResult(bytes.GetValueOrDefault(physicalDriveNumber));
        }
    }

    [Fact]
    public async Task AReaderWithoutABatchMethodIsAskedOneDriveAtATime()
    {
        // The default member of the interface: a reader that only knows the single read still works.
        var reader = new SingleOnlyReader(new() { [0] = null, [1] = new byte[512] });

        var answers = await ((IElevatedStorageReader)reader).ReadRawManyAsync([(0, StorageHealthKind.Sata), (1, StorageHealthKind.Nvme)], CancellationToken.None);

        Assert.Equal([0, 1], reader.Asked);
        Assert.Null(answers[0]);
        Assert.NotNull(answers[1]);
    }
}
