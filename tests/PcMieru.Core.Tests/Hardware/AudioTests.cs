using System.Buffers.Binary;
using PcMieru.Core.Hardware.Collectors;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Tests.Hardware;

public class AudioTests
{
    private static byte[] Wave(ushort tag, ushort channels, uint rate, ushort bits, ushort? valid = null)
    {
        var b = new byte[valid is null ? 18 : 40];
        BinaryPrimitives.WriteUInt16LittleEndian(b, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), rate);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), rate * channels * bits / 8);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), (ushort)(channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), bits);
        if (valid is { } v)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), 22);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(18), v);
        }

        return b;
    }

    private static RawAudioEndpoint Endpoint(
        bool input = false, uint state = MMDevice.StateActive, bool isDefault = false, uint? form = 1, byte[]? format = null, string? name = "Speakers (Test Audio)", string? adapter = "Test Audio") =>
        new("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}", name, adapter, input, state, isDefault, form, format);

    [Fact]
    public void PlainPcmFormatIsReadAsIs()
    {
        var f = AudioBuilder.ParseFormat(Wave(1, 2, 48000, 16));
        Assert.Equal(new AudioFormat(48000, 16, 2), f);
    }

    [Fact]
    public void ExtensibleFormatUsesTheValidBitsLikeSoundSettings()
    {
        var f = AudioBuilder.ParseFormat(Wave(0xFFFE, 2, 96000, 32, 24));
        Assert.Equal(new AudioFormat(96000, 24, 2), f);
    }

    [Fact]
    public void ExtensibleFormatWithNonsenseValidBitsFallsBackToTheContainer()
    {
        Assert.Equal(32, AudioBuilder.ParseFormat(Wave(0xFFFE, 2, 48000, 32, 0))!.BitsPerSample);
        Assert.Equal(16, AudioBuilder.ParseFormat(Wave(0xFFFE, 2, 48000, 16, 32))!.BitsPerSample);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(15)]
    public void ShortBlobIsNotAFormat(int length) => Assert.Null(AudioBuilder.ParseFormat(new byte[length]));

    [Fact]
    public void ZeroChannelsRateOrBitsIsNotAFormat()
    {
        Assert.Null(AudioBuilder.ParseFormat(Wave(1, 0, 48000, 16)));
        Assert.Null(AudioBuilder.ParseFormat(Wave(1, 2, 0, 16)));
        Assert.Null(AudioBuilder.ParseFormat(Wave(1, 2, 48000, 0)));
        Assert.Null(AudioBuilder.ParseFormat(Wave(1, 2, 4_000_000_000, 16)));
    }

    [Fact]
    public void ActiveOutputShowsFormatLabelledAsWindowsMixFormat()
    {
        var e = AudioBuilder.Build(Endpoint(isDefault: true, format: Wave(1, 2, 48000, 24)));
        Assert.Equal(AudioDirection.Output, e.Direction.Data);
        Assert.True(e.IsDefault.Data);
        Assert.Equal(48000, e.SampleRateHz.Data);
        Assert.Equal(24, e.BitsPerSample.Data);
        Assert.Contains("not the most", e.SampleRateHz.Detail);
        Assert.Equal(2, e.Details!.Channels.Data);
        Assert.Equal("active", e.Details.State.Data);
        Assert.Equal("Speakers", e.Details.FormFactor.Data);
        Assert.Equal("Test Audio", e.Details.Adapter.Data);
        Assert.Equal("Speakers (Test Audio)", e.Name.Data);
    }

    [Fact]
    public void InputIsMarkedAsInput() =>
        Assert.Equal(AudioDirection.Input, AudioBuilder.Build(Endpoint(input: true, form: 4, format: Wave(1, 1, 16000, 16))).Direction.Data);

    [Fact]
    public void MissingFormatIsUnsupportedNotZero()
    {
        var active = AudioBuilder.Build(Endpoint(format: null));
        Assert.Equal(ValueStatus.Unsupported, active.SampleRateHz.Status);
        Assert.Equal(ValueStatus.Unsupported, active.BitsPerSample.Status);
        Assert.Equal(ValueStatus.Unsupported, active.Details!.Channels.Status);
        var unplugged = AudioBuilder.Build(Endpoint(state: MMDevice.StateUnplugged, format: null));
        Assert.Contains("not connected", unplugged.SampleRateHz.Detail);
        Assert.Equal("unplugged", unplugged.Details!.State.Data);
    }

    [Fact]
    public void MissingNameAdapterAndFormFactorAreSaidSo()
    {
        var e = AudioBuilder.Build(Endpoint(name: " ", adapter: null, form: null, format: Wave(1, 2, 48000, 16)));
        Assert.Equal(ValueStatus.Unsupported, e.Name.Status);
        Assert.Equal(ValueStatus.Unsupported, e.Details!.Adapter.Status);
        Assert.Equal(ValueStatus.Unsupported, e.Details.FormFactor.Status);
    }

    [Theory]
    [InlineData(1u, "Speakers")]
    [InlineData(3u, "Headphones")]
    [InlineData(4u, "Microphone")]
    [InlineData(5u, "Headset")]
    [InlineData(8u, "S/PDIF")]
    [InlineData(9u, "HDMI / DisplayPort")]
    public void FormFactorsGetPlainNames(uint form, string expected) => Assert.Equal(expected, AudioBuilder.FormFactorName(form));

    [Theory]
    [InlineData(10u)]
    [InlineData(99u)]
    public void UnknownFormFactorHasNoName(uint form) => Assert.Null(AudioBuilder.FormFactorName(form));

    [Trait("Category", "RealMachine")]
    [Fact]
    public void RealCollectionRunsOnThisMachineWithoutThrowing()
    {
        var result = AudioCollector.Collect();
        Assert.True(result.Status is ValueStatus.Ok or ValueStatus.Unsupported or ValueStatus.Error, result.Detail);
        if (result.Status == ValueStatus.Ok)
        {
            Assert.NotEmpty(result.Data!);
            Assert.True(result.Data!.Count(e => e.IsDefault.Data && e.Direction.Data == AudioDirection.Output) <= 1);
        }
    }
}
