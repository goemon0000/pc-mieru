using System.Buffers.Binary;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Hardware.Collectors;

/// <summary>The mix format Windows uses for an endpoint: what the Sound settings page calls the default format.</summary>
internal sealed record AudioFormat(int SampleRateHz, int BitsPerSample, int Channels);

/// <summary>
/// Sound devices from the Windows Core Audio endpoint list. The sample rate and bit depth are the format Windows mixes at (the "default
/// format" in Sound settings), not the most the hardware can do, and nothing is opened or recorded.
/// </summary>
internal static class AudioCollector
{
    private const string Src = "mmdevice:IMMDeviceEnumerator";

    public static Value<IReadOnlyList<AudioEndpoint>> Collect()
    {
        try
        {
            var raw = MMDevice.Enumerate(out var hr);
            if (raw is null)
            {
                return Value.Failed<IReadOnlyList<AudioEndpoint>>(Src, $"audio endpoint list unavailable (HRESULT 0x{hr:X8})");
            }

            if (raw.Count == 0)
            {
                return Value.Unsupported<IReadOnlyList<AudioEndpoint>>(Src, "Windows lists no sound devices");
            }

            var list = raw.Select(AudioBuilder.Build).ToList();
            return Value.Ok<IReadOnlyList<AudioEndpoint>>(list, Src);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Probe.FromException<IReadOnlyList<AudioEndpoint>>(ex, Src);
        }
    }
}

/// <summary>The pure part of the audio collector. Unit tested with hand-made format blobs.</summary>
internal static class AudioBuilder
{
    private const string Src = "mmdevice:IMMDeviceEnumerator";
    private const string FormatSrc = "mmdevice:PKEY_AudioEngine_DeviceFormat";
    private const string FormatNote = "the format Windows mixes at, not the most the hardware can do";
    private const ushort WaveFormatExtensible = 0xFFFE;

    public static AudioEndpoint Build(RawAudioEndpoint raw)
    {
        var format = ParseFormat(raw.DeviceFormat);
        return new AudioEndpoint(
            string.IsNullOrWhiteSpace(raw.Name)
                ? Value.Unsupported<string>(Src, "Windows gave this device no name")
                : Value.Ok(raw.Name.Trim(), "mmdevice:PKEY_Device_FriendlyName"),
            Value.Ok(raw.IsInput ? AudioDirection.Input : AudioDirection.Output, Src),
            Probe.Note(raw.IsDefault, Src, "default device for everyday use"),
            format is null ? NoFormat<int>(raw) : Probe.Note(format.SampleRateHz, FormatSrc, FormatNote),
            format is null ? NoFormat<int>(raw) : Probe.Note(format.BitsPerSample, FormatSrc, FormatNote),
            new AudioDetails(
                Value.Ok(StateName(raw.State), Src),
                raw.FormFactor is { } f && FormFactorName(f) is { } name
                    ? Value.Ok(name, "mmdevice:PKEY_AudioEndpoint_FormFactor")
                    : Value.Unsupported<string>("mmdevice:PKEY_AudioEndpoint_FormFactor", "the connection type is not reported"),
                string.IsNullOrWhiteSpace(raw.Adapter)
                    ? Value.Unsupported<string>("mmdevice:PKEY_DeviceInterface_FriendlyName", "the sound adapter is not reported")
                    : Value.Ok(raw.Adapter.Trim(), "mmdevice:PKEY_DeviceInterface_FriendlyName"),
                format is null ? NoFormat<int>(raw) : Value.Ok(format.Channels, FormatSrc)));

        static Value<T> NoFormat<T>(RawAudioEndpoint r) =>
            Value.Unsupported<T>(FormatSrc, r.State == MMDevice.StateActive ? "Windows reports no format for this device" : "no format while the device is not connected");
    }

    /// <summary>
    /// Reads a WAVEFORMATEX / WAVEFORMATEXTENSIBLE blob. For the extensible form the bit depth is the valid bits (24 bits in a 32-bit
    /// container is shown as 24, like the Sound settings page). Null when the blob is too short or holds nonsense.
    /// </summary>
    public static AudioFormat? ParseFormat(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 16)
        {
            return null;
        }

        var tag = BinaryPrimitives.ReadUInt16LittleEndian(blob);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(blob[2..]);
        var rate = BinaryPrimitives.ReadUInt32LittleEndian(blob[4..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(blob[14..]);
        if (tag == WaveFormatExtensible && blob.Length >= 20)
        {
            var valid = BinaryPrimitives.ReadUInt16LittleEndian(blob[18..]);
            if (valid > 0 && valid <= bits)
            {
                bits = valid;
            }
        }

        if (channels == 0 || rate == 0 || rate > 1_000_000 || bits == 0)
        {
            return null;
        }

        return new AudioFormat((int)rate, bits, channels);
    }

    /// <summary>DEVICE_STATE_* as plain words.</summary>
    public static string StateName(uint state) => state switch
    {
        MMDevice.StateActive => "active",
        MMDevice.StateDisabled => "disabled",
        MMDevice.StateNotPresent => "not present",
        MMDevice.StateUnplugged => "unplugged",
        _ => "unknown",
    };

    /// <summary>EndpointFormFactor as the words people use. Null for "unknown" and for values this table does not know.</summary>
    public static string? FormFactorName(uint formFactor) => formFactor switch
    {
        0 => "Network device",
        1 => "Speakers",
        2 => "Line level",
        3 => "Headphones",
        4 => "Microphone",
        5 => "Headset",
        6 => "Handset",
        7 => "Digital (pass-through)",
        8 => "S/PDIF",
        9 => "HDMI / DisplayPort",
        _ => null,
    };
}
