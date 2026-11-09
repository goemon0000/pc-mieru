using System.Diagnostics;
using System.IO.Compression;
using PcMieru.Core.Collection;
using Xunit.Abstractions;

namespace PcMieru.Core.Tests.Collection;

public sealed class VersionInfoReaderTests(ITestOutputHelper output)
{
    [Fact]
    public void A_system_dll_gives_company_product_and_description()
    {
        var v = VersionInfoReader.Read(Path.Combine(Environment.SystemDirectory, "kernel32.dll"));
        output.WriteLine($"kernel32: product='{v.Product.Data}' description='{v.Description.Data}' company='{v.Company.Data}'");

        Assert.True(v.Product.IsOk, v.Product.Detail);
        Assert.True(v.Description.IsOk, v.Description.Detail);
        Assert.Contains("Microsoft", v.Company.Data);
        Assert.Equal(VersionInfoReader.Source, v.Company.Source);
    }

    [Fact]
    public void The_description_of_a_system_exe_comes_from_its_localized_resource()
    {
        var v = VersionInfoReader.Read(Path.Combine(Environment.SystemDirectory, "svchost.exe"));
        output.WriteLine($"svchost: description='{v.Description.Data}'");

        Assert.True(v.Description.IsOk, v.Description.Detail);
        Assert.False(string.IsNullOrWhiteSpace(v.Description.Data));
    }

    [Fact]
    public void A_file_without_a_version_resource_is_Unsupported_for_every_field()
    {
        var folder = Directory.CreateTempSubdirectory("pcmieru-ver-");
        try
        {
            var plain = Path.Combine(folder.FullName, "plain.exe");
            File.WriteAllBytes(plain, new byte[2048]);

            var v = VersionInfoReader.Read(plain);

            Assert.Equal(ValueStatus.Unsupported, v.Product.Status);
            Assert.Equal(ValueStatus.Unsupported, v.Description.Status);
            Assert.Equal(ValueStatus.Unsupported, v.Company.Status);
            Assert.False(string.IsNullOrEmpty(v.Company.Detail));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_missing_file_is_an_error_not_an_exception()
    {
        var v = VersionInfoReader.Read(Path.Combine(Path.GetTempPath(), "pcmieru-no-such-file.exe"));

        Assert.Equal(ValueStatus.Error, v.Company.Status);
    }

    [Theory]
    [InlineData("Microsoft Corporation\0junk", "Microsoft Corporation")]
    [InlineData("  spaced   out \t text ", "spaced out text")]
    [InlineData("line1\r\nline2\u0007", "line1 line2")]
    [InlineData("", "")]
    public void Resource_text_is_cleaned(string raw, string expected)
    {
        Assert.Equal(expected, VersionInfoReader.Clean(raw));
    }

    [Fact]
    public void Resource_text_is_capped()
    {
        Assert.Equal(200, VersionInfoReader.Clean(new string('x', 5000)).Length);
    }
}

public sealed class ServiceMapTests(ITestOutputHelper output)
{
    [Trait("Category", "RealMachine")]
    [Fact]
    public void The_real_table_lists_services_by_the_process_that_hosts_them()
    {
        var map = new ServiceMap();
        var hosted = Process.GetProcessesByName("svchost").Select(p => (uint)p.Id).ToArray();
        Assert.NotEmpty(hosted);

        var counts = hosted.Select(pid => (pid, names: map.NamesOf(pid))).Where(x => x.names.Count > 0).ToList();
        output.WriteLine($"{hosted.Length} svchost processes, {counts.Count} host services, e.g. {string.Join(", ", counts.Take(3).Select(c => $"{c.pid}:[{string.Join(",", c.names.Take(3))}]"))}");

        Assert.NotEmpty(counts);
        Assert.Equal(1, map.Loads);
    }

    [Trait("Category", "RealMachine")]
    [Fact]
    public void A_process_that_hosts_no_service_gets_an_empty_list()
    {
        Assert.Empty(new ServiceMap().NamesOf((uint)Environment.ProcessId));
        Assert.Empty(new ServiceMap().NamesOf(0));
    }

    [Fact]
    public void The_table_is_reused_until_it_is_old_then_reloaded()
    {
        var loads = 0;
        var map = new ServiceMap(() =>
        {
            loads++;
            return new Dictionary<uint, string[]> { [10] = ["Alpha", "Beta"] };
        }, TimeSpan.FromMilliseconds(200));

        Assert.Equal(["Alpha", "Beta"], map.NamesOf(10));
        Assert.Empty(map.NamesOf(11));
        Assert.Equal(1, loads);

        Thread.Sleep(300);
        _ = map.NamesOf(10);

        Assert.Equal(2, loads);
    }

    [Fact]
    public void A_failed_refresh_keeps_the_previous_table()
    {
        var fail = false;
        var map = new ServiceMap(() => fail ? null : new Dictionary<uint, string[]> { [10] = ["Alpha"] }, TimeSpan.FromMilliseconds(50));
        Assert.Equal(["Alpha"], map.NamesOf(10));

        fail = true;
        Thread.Sleep(120);

        Assert.Equal(["Alpha"], map.NamesOf(10));
    }
}

public sealed class PngEncoderTests
{
    /// <summary>A strict little decoder for exactly what the encoder writes, so the test does not trust the encoder to check itself.</summary>
    internal static (int Width, int Height, byte[] Rgba) Decode(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        var offset = 8;
        var width = 0;
        var height = 0;
        using var idat = new MemoryStream();
        var sawEnd = false;
        while (offset < png.Length)
        {
            var length = (int)ReadUInt32(png, offset);
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            var crc = ReadUInt32(png, offset + 8 + length);
            Assert.Equal(crc, PngEncoder.Crc32(png.AsSpan(offset + 4, 4), data));
            switch (type)
            {
                case "IHDR":
                    width = (int)ReadUInt32(png, offset + 8);
                    height = (int)ReadUInt32(png, offset + 12);
                    Assert.Equal(8, data[8]);
                    Assert.Equal(6, data[9]);
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
                case "IEND":
                    sawEnd = true;
                    break;
            }

            offset += 12 + length;
        }

        Assert.True(sawEnd);
        Assert.Equal(png.Length, offset);
        idat.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
        {
            zlib.CopyTo(inflated);
        }

        var raw = inflated.ToArray();
        var stride = (width * 4) + 1;
        Assert.Equal(stride * height, raw.Length);
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, raw[y * stride]); // filter none
            Array.Copy(raw, (y * stride) + 1, rgba, y * width * 4, width * 4);
        }

        return (width, height, rgba);
    }

    private static uint ReadUInt32(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

    [Fact]
    public void The_checksum_matches_the_known_value_for_IEND()
    {
        // The CRC of an empty IEND chunk is the same in every PNG file ever written.
        Assert.Equal(0xAE426082u, PngEncoder.Crc32("IEND"u8, []));
    }

    [Fact]
    public void Pixels_survive_a_round_trip_with_blue_and_red_swapped_back()
    {
        var bgra = new byte[3 * 2 * 4];
        for (var i = 0; i < bgra.Length; i++)
        {
            bgra[i] = (byte)(i * 11);
        }

        var png = PngEncoder.EncodeBgra(bgra, 3, 2);
        var (width, height, rgba) = Decode(png);

        Assert.Equal((3, 2), (width, height));
        for (var p = 0; p < 6; p++)
        {
            Assert.Equal(bgra[(p * 4) + 2], rgba[p * 4]); // R
            Assert.Equal(bgra[(p * 4) + 1], rgba[(p * 4) + 1]); // G
            Assert.Equal(bgra[p * 4], rgba[(p * 4) + 2]); // B
            Assert.Equal(bgra[(p * 4) + 3], rgba[(p * 4) + 3]); // A
        }
    }

    [Fact]
    public void Wrong_sizes_are_refused()
    {
        Assert.Throws<ArgumentException>(() => PngEncoder.EncodeBgra(new byte[10], 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => PngEncoder.EncodeBgra([], 0, 0));
    }
}

public sealed class ProcessIconSourceTests(ITestOutputHelper output)
{
    private static string System32(string name) => Path.Combine(Environment.SystemDirectory, name);

    [Trait("Category", "RealMachine")]
    [Fact]
    public async Task The_real_icon_of_an_exe_comes_back_as_a_valid_png_of_the_requested_size()
    {
        var source = new ProcessIconSource();

        var icon = await source.GetPngAsync(System32("cmd.exe"), 32);

        Assert.True(icon.IsOk, icon.Detail);
        var (width, height, rgba) = PngEncoderTests.Decode(icon.Data!);
        output.WriteLine($"cmd.exe icon: {width}x{height}, {icon.Data!.Length} bytes");
        Assert.Equal((32, 32), (width, height));
        Assert.Contains(Enumerable.Range(0, rgba.Length / 4), i => rgba[(i * 4) + 3] > 0);
    }

    [Trait("Category", "RealMachine")]
    [Fact]
    public async Task A_small_size_gives_a_smaller_image()
    {
        var icon = await new ProcessIconSource().GetPngAsync(System32("notepad.exe"), 16);

        Assert.True(icon.IsOk, icon.Detail);
        var (width, height, _) = PngEncoderTests.Decode(icon.Data!);
        Assert.Equal((16, 16), (width, height));
    }

    [Fact]
    public async Task A_file_without_an_icon_is_Unsupported()
    {
        var self = typeof(ProcessIconSource).Assembly.Location;

        var icon = await new ProcessIconSource().GetPngAsync(self, 32);

        Assert.Equal(ValueStatus.Unsupported, icon.Status);
        Assert.False(string.IsNullOrEmpty(icon.Detail));
    }

    [Fact]
    public async Task A_missing_file_or_empty_path_is_a_value_not_an_exception()
    {
        var source = new ProcessIconSource();

        Assert.Equal(ValueStatus.Error, (await source.GetPngAsync(Path.Combine(Path.GetTempPath(), "pcmieru-nothing.exe"))).Status);
        Assert.Equal(ValueStatus.Unsupported, (await source.GetPngAsync("  ")).Status);
    }

    [Fact]
    public async Task The_same_icon_is_extracted_once_even_when_asked_in_parallel()
    {
        var calls = 0;
        var release = new ManualResetEventSlim(false);
        var source = new ProcessIconSource((_, _) =>
        {
            Interlocked.Increment(ref calls);
            release.Wait(5000);
            return Value.Ok(new byte[] { 1, 2, 3 }, "test");
        });
        var path = typeof(ProcessIconSource).Assembly.Location;

        var all = Enumerable.Range(0, 12).Select(_ => source.GetPngAsync(path, 32)).ToList();
        await Task.Delay(100);
        release.Set();
        var results = await Task.WhenAll(all);
        var again = await source.GetPngAsync(path, 32);

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Equal(new byte[] { 1, 2, 3 }, r.Data));
        Assert.Equal(new byte[] { 1, 2, 3 }, again.Data);
        Assert.Equal(1, source.Extractions);
    }

    [Fact]
    public async Task A_different_size_is_a_different_icon()
    {
        var sizes = new List<int>();
        var source = new ProcessIconSource((_, size) =>
        {
            lock (sizes)
            {
                sizes.Add(size);
            }

            return Value.Ok(new byte[] { (byte)size }, "test");
        });
        var path = typeof(ProcessIconSource).Assembly.Location;

        _ = await source.GetPngAsync(path, 16);
        _ = await source.GetPngAsync(path, 32);
        _ = await source.GetPngAsync(path, 32);
        _ = await source.GetPngAsync(path, 100_000); // clamped, not refused

        Assert.Equal([16, 32, 256], sizes.Order().ToArray());
    }

    [Fact]
    public async Task A_failed_extraction_is_not_remembered_but_an_answer_is()
    {
        var calls = 0;
        var source = new ProcessIconSource((_, _) => ++calls == 1
            ? throw new InvalidOperationException("transient")
            : Value.Ok(new byte[] { 9 }, "test"));
        var path = typeof(ProcessIconSource).Assembly.Location;

        var first = await source.GetPngAsync(path);
        var second = await source.GetPngAsync(path);
        var third = await source.GetPngAsync(path);

        Assert.Equal(ValueStatus.Error, first.Status);
        Assert.True(second.IsOk);
        Assert.True(third.IsOk);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task The_cache_is_bounded()
    {
        var folder = Directory.CreateTempSubdirectory("pcmieru-icons-");
        try
        {
            var source = new ProcessIconSource((_, _) => Value.Ok(new byte[] { 1 }, "test"));
            for (var i = 0; i < 600; i++)
            {
                var file = Path.Combine(folder.FullName, $"f{i}.exe");
                await File.WriteAllBytesAsync(file, [1]);
                _ = await source.GetPngAsync(file);
            }

            Assert.InRange(source.CachedCount, 1, 512);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_cancelled_caller_stops_waiting()
    {
        var release = new ManualResetEventSlim(false);
        var source = new ProcessIconSource((_, _) =>
        {
            release.Wait(5000);
            return Value.Ok(new byte[] { 1 }, "test");
        });
        using var cts = new CancellationTokenSource(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetPngAsync(typeof(ProcessIconSource).Assembly.Location, 32, cts.Token));

        release.Set();
    }
}
