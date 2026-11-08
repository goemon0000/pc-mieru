using System.IO.Compression;

namespace PcMieru.Core.Collection;

/// <summary>
/// The smallest PNG writer that does the job (8-bit RGBA, no interlace, filter 0), so icons can be handed to any UI as
/// plain bytes without a graphics library. Written from the PNG specification; the checksum table is computed, not copied.
/// </summary>
internal static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <param name="bgra">Top-down rows of B, G, R, A bytes (straight alpha), <c>width * height * 4</c> long.</param>
    public static byte[] EncodeBgra(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width is <= 0 or > 4096 || height is <= 0 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "the image size is out of range");
        }

        if (bgra.Length != width * height * 4)
        {
            throw new ArgumentException("the pixel data does not match the size", nameof(bgra));
        }

        var stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var row = (stride + 1) * y;
            raw[row] = 0; // filter type: none
            var source = bgra.Slice(y * stride, stride);
            for (var x = 0; x < width; x++)
            {
                raw[row + 1 + (x * 4)] = source[(x * 4) + 2];
                raw[row + 1 + (x * 4) + 1] = source[(x * 4) + 1];
                raw[row + 1 + (x * 4) + 2] = source[x * 4];
                raw[row + 1 + (x * 4) + 3] = source[(x * 4) + 3];
            }
        }

        using var compressed = new MemoryStream(raw.Length / 2 + 64);
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var output = new MemoryStream((int)compressed.Length + 128);
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        WriteBigEndian(header[..4], (uint)width);
        WriteBigEndian(header.Slice(4, 4), (uint)height);
        header[8] = 8; // bit depth
        header[9] = 6; // colour type: RGBA
        header[10] = 0; // compression
        header[11] = 0; // filter
        header[12] = 0; // interlace
        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    internal static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        WriteBigEndian(number, (uint)data.Length);
        output.Write(number);
        output.Write(type);
        output.Write(data);
        WriteBigEndian(number, Crc32(type, data));
        output.Write(number);
    }

    private static void WriteBigEndian(Span<byte> destination, uint value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
