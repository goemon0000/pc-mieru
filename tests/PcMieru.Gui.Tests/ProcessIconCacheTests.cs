using System.Drawing.Imaging;

namespace PcMieru.Gui.Tests;

/// <summary>The list's program icons: keyed by path, never blocking, never guessing, bounded, and quiet about failures.</summary>
public class ProcessIconCacheTests
{
    private static byte[] Png()
    {
        using var bitmap = new Bitmap(32, 32);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private sealed class StubIcons(Func<string, Value<byte[]>> answer) : IProcessIconSource
    {
        public int Calls;

        public Task<Value<byte[]>> GetPngAsync(string path, int size = 32, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.Run(() => answer(path), ct);
        }
    }

    private static Task Loaded(ProcessIconCache cache)
    {
        var source = new TaskCompletionSource();
        cache.Loaded += () => source.TrySetResult();
        return source.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task An_icon_is_fetched_once_off_the_calling_thread_and_announced()
    {
        var png = Png();
        var source = new StubIcons(_ => Value.Ok(png, "stub"));
        using var owner = new Control();
        using var cache = new ProcessIconCache(source, owner);
        var loaded = Loaded(cache);

        Assert.Null(cache.TryGet(@"C:\app\a.exe")); // first call: not there yet, never blocks
        await loaded;

        var icon = cache.TryGet(@"C:\app\a.exe");
        Assert.NotNull(icon);
        Assert.Same(icon, cache.TryGet(@"C:\APP\A.EXE")); // the same file by another spelling is the same entry
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task A_file_without_an_icon_is_remembered_and_not_asked_again()
    {
        var source = new StubIcons(_ => Value.Unsupported<byte[]>("stub", "no icon"));
        using var owner = new Control();
        using var cache = new ProcessIconCache(source, owner);
        var loaded = Loaded(cache);

        Assert.Null(cache.TryGet(@"C:\app\none.exe"));
        await loaded;
        for (var i = 0; i < 5; i++)
        {
            Assert.Null(cache.TryGet(@"C:\app\none.exe"));
        }

        Assert.Equal(1, source.Calls);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task A_transient_failure_is_asked_again_only_after_the_interval()
    {
        var now = 1_000L;
        var fail = true;
        var png = Png();
        var source = new StubIcons(_ => fail ? Value.Missing<byte[]>(ValueStatus.Timeout, "stub", "slow") : Value.Ok(png, "stub"));
        using var owner = new Control();
        using var cache = new ProcessIconCache(source, owner, () => Interlocked.Read(ref now));

        var first = Loaded(cache);
        Assert.Null(cache.TryGet(@"C:\app\slow.exe"));
        await first;
        Assert.Equal(1, source.Calls);

        // Repaints right after the failure do not hammer the source.
        Assert.Null(cache.TryGet(@"C:\app\slow.exe"));
        Interlocked.Add(ref now, 10_000);
        Assert.Null(cache.TryGet(@"C:\app\slow.exe"));
        Assert.Equal(1, source.Calls);

        // After the interval it asks again, and this time it works.
        fail = false;
        Interlocked.Add(ref now, 40_000);
        var second = Loaded(cache);
        Assert.Null(cache.TryGet(@"C:\app\slow.exe"));
        await second;
        Assert.Equal(2, source.Calls);
        Assert.NotNull(cache.TryGet(@"C:\app\slow.exe"));
    }

    [Fact]
    public async Task Bytes_that_are_not_an_image_count_as_no_icon()
    {
        var source = new StubIcons(_ => Value.Ok(new byte[] { 1, 2, 3, 4 }, "stub"));
        using var owner = new Control();
        using var cache = new ProcessIconCache(source, owner);
        var loaded = Loaded(cache);

        Assert.Null(cache.TryGet(@"C:\app\bad.exe"));
        await loaded;
        Assert.Null(cache.TryGet(@"C:\app\bad.exe"));
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public void An_empty_path_asks_nobody()
    {
        var source = new StubIcons(_ => Value.Ok(Png(), "stub"));
        using var owner = new Control();
        using var cache = new ProcessIconCache(source, owner);
        Assert.Null(cache.TryGet(string.Empty));
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task Dispose_frees_the_images_and_later_answers_are_dropped()
    {
        var png = Png();
        var source = new StubIcons(_ => Value.Ok(png, "stub"));
        using var owner = new Control();
        var cache = new ProcessIconCache(source, owner);
        var loaded = Loaded(cache);
        Assert.Null(cache.TryGet(@"C:\app\a.exe"));
        await loaded;
        var icon = cache.TryGet(@"C:\app\a.exe");
        Assert.NotNull(icon);

        cache.Dispose();

        Assert.Equal(0, cache.Count);
        Assert.Null(cache.TryGet(@"C:\app\a.exe")); // disposed: nothing is looked up any more
        Assert.Throws<ArgumentException>(() => icon!.Width.ToString()); // GDI+ says the image is gone
    }
}
