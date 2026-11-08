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
        var client = new StandIn(OperationCode.Ok, Data((HelperStorageReader.SmartData, B64(table)), (HelperStorageReader.SmartThresholds, B64(limits))));
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
        var client = new StandIn(OperationCode.Ok, Data((HelperStorageReader.SmartData, B64(table))));

        var raw = await new HelperStorageReader(() => client).ReadRawAsync(1, StorageHealthKind.Sata, CancellationToken.None);

        Assert.Equal(table, raw);
    }

    [Fact]
    public async Task ADamagedThresholdSectorIsLeftOutNotPassedOnAsIfItWereOne()
    {
        var table = SmartTable(9, 99, 99, 42);
        var client = new StandIn(OperationCode.Ok, Data((HelperStorageReader.SmartData, B64(table)), (HelperStorageReader.SmartThresholds, B64(new byte[100]))));

        var raw = await new HelperStorageReader(() => client).ReadRawAsync(1, StorageHealthKind.Sata, CancellationToken.None);

        Assert.Equal(table, raw);
    }

    [Fact]
    public async Task NvmeAskedForNvmeHealthAndReturnsTheLogPage()
    {
        var page = new byte[512];
        page[3] = 100;
        var client = new StandIn(OperationCode.Ok, Data((HelperStorageReader.NvmeHealth, B64(page))));

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
        var client = new StandIn(code, Data((HelperStorageReader.SmartData, B64(table))));

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
        var client = new StandIn(OperationCode.Ok, Data((HelperStorageReader.SmartData, text), (HelperStorageReader.NvmeHealth, text)));
        var reader = new HelperStorageReader(() => client);

        Assert.Null(await reader.ReadRawAsync(1, StorageHealthKind.Sata, CancellationToken.None));
        Assert.Null(await reader.ReadRawAsync(1, StorageHealthKind.Nvme, CancellationToken.None));
    }

    [Fact]
    public async Task TheOtherKindsKeyIsNotTakenForTheRequestedOne()
    {
        var client = new StandIn(OperationCode.Ok, Data((HelperStorageReader.NvmeHealth, B64(new byte[512]))));

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
        var client = new StandIn(OperationCode.Ok, Data((HelperStorageReader.SmartData, B64(new byte[512]))));

        Assert.Null(await new HelperStorageReader(() => client).ReadRawAsync(drive, StorageHealthKind.Sata, CancellationToken.None));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task TheHelperIsLookedUpWhenAReadNeedsItNotWhenTheReaderIsMade()
    {
        IHelperClient? registered = null;
        var reader = new HelperStorageReader(() => registered);

        // Management registers the Helper after Hardware has built this reader.
        registered = new StandIn(OperationCode.Ok, Data((HelperStorageReader.SmartData, B64(SmartTable(9, 99, 99, 42)))));

        Assert.NotNull(await reader.ReadRawAsync(1, StorageHealthKind.Sata, CancellationToken.None));
    }

    [Fact]
    public async Task WhatTheAdapterReturnsIsWhatTheHealthParserExpects()
    {
        // The end of the chain on bytes: Helper answer -> adapter -> parser, with a table and its threshold sector.
        var table = SmartTable(5, 100, 100, 0);
        var limits = Thresholds(5, 36);
        var client = new StandIn(OperationCode.Ok, Data((HelperStorageReader.SmartData, B64(table)), (HelperStorageReader.SmartThresholds, B64(limits))));

        var raw = await new HelperStorageReader(() => client).ReadRawAsync(2, StorageHealthKind.Sata, CancellationToken.None);
        var health = StorageHealthParsers.FromSmartBlob(raw!);

        Assert.True(health.IsOk, health.Detail);
        var attribute = Assert.Single(health.Data!.Attributes);
        Assert.Equal(5, attribute.Id);
        Assert.Equal(36, attribute.Threshold);
    }
}
