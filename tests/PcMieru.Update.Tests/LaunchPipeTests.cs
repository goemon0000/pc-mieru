using System.Text;
using PcMieru.Update.Launch;

namespace PcMieru.Update.Tests;

/// <summary>
/// <see cref="PipeChannel"/> over the real pipe pair of <c>LaunchNative.TryCreatePipe</c> (an overlapped duplex named pipe, one handle per end): both directions, a write from one thread while another waits for a
/// line, the other end going away, lines that break the rules, and a handle that is not a pipe. Every wait is bounded.
/// </summary>
public sealed class LaunchPipeTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly List<IDisposable> _open = [];

    public void Dispose()
    {
        foreach (var item in _open)
        {
            item.Dispose();
        }
    }

    private (PipeChannel Server, PipeChannel Client) Pair()
    {
        Assert.True(LaunchNative.TryCreatePipe(out var server, out var client));
        var pair = (new PipeChannel(server), new PipeChannel(client));
        _open.Add(pair.Item1);
        _open.Add(pair.Item2);
        return pair;
    }

    private static string Receive(PipeChannel channel)
    {
        Assert.True(channel.TryReceive(Wait, out var line), "no line arrived");
        return line;
    }

    private static void WaitUntilClosed(PipeChannel channel)
    {
        var until = Environment.TickCount64 + 10_000;
        while (!channel.IsClosed && Environment.TickCount64 < until)
        {
            Thread.Sleep(10);
        }

        Assert.True(channel.IsClosed, "the channel did not notice that it was closed");
    }

    [Fact]
    public void A_line_goes_both_ways()
    {
        var (server, client) = Pair();
        Assert.True(server.Send("hello from the server"));
        Assert.Equal("hello from the server", Receive(client));
        Assert.True(client.Send("hello from the client"));
        Assert.Equal("hello from the client", Receive(server));
    }

    [Fact]
    public void Lines_arrive_whole_and_in_order()
    {
        var (server, client) = Pair();
        for (var i = 0; i < 200; i++)
        {
            Assert.True(server.Send("line " + i));
        }

        for (var i = 0; i < 200; i++)
        {
            Assert.Equal("line " + i, Receive(client));
        }
    }

    [Fact]
    public void Japanese_text_and_a_long_path_survive_as_UTF8()
    {
        var (server, client) = Pair();
        var text = "更新\t" + @"C:\Users\ユーザー\AppData\Local\PCMieru\update\0a1b2c3d-0123456789abcdef\cache\0123456789ab" + new string('x', 3000);
        Assert.True(server.Send(text));
        Assert.Equal(text, Receive(client));
    }

    [Fact]
    public async Task A_receive_that_waits_does_not_hold_up_a_send_on_the_same_channel()
    {
        var (server, client) = Pair();
        var waiting = Task.Run(() => server.TryReceive(TimeSpan.FromSeconds(8), out var line) ? line : null);
        await Task.Delay(300); // the reader of the server is now blocked in a read

        var sent = await Task.Run(() => server.Send("pong 1")).WaitAsync(Wait); // a TimeoutException here: a send waited for the read that was pending
        Assert.True(sent);
        Assert.Equal("pong 1", Receive(client));

        Assert.True(client.Send("reply"));
        Assert.Equal("reply", await waiting.WaitAsync(Wait));
    }

    [Fact]
    public async Task Sends_from_many_threads_are_never_mixed()
    {
        var (server, client) = Pair();
        var senders = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                Assert.True(server.Send($"t{t}-{i}-" + new string((char)('a' + t), 300)));
            }
        })).ToArray();
        await Task.WhenAll(senders).WaitAsync(Wait);

        var seen = new HashSet<string>();
        for (var i = 0; i < 8 * 50; i++)
        {
            var line = Receive(client);
            var parts = line.Split('-');
            Assert.Equal(3, parts.Length);
            Assert.Equal(new string((char)('a' + int.Parse(parts[0][1..])), 300), parts[2]);
            Assert.True(seen.Add(parts[0] + "-" + parts[1]));
        }
    }

    [Fact]
    public void A_closed_other_end_is_noticed_and_a_send_then_says_no()
    {
        var (server, client) = Pair();
        client.Dispose();
        WaitUntilClosed(server);
        Assert.False(server.TryReceive(TimeSpan.FromMilliseconds(50), out _));
        Assert.False(server.Send("anyone there?"));
    }

    [Fact]
    public void A_line_that_was_written_before_the_close_is_still_read()
    {
        var (server, client) = Pair();
        Assert.True(client.Send("last words"));
        client.Dispose();
        Assert.Equal("last words", Receive(server));
        WaitUntilClosed(server);
    }

    [Fact]
    public async Task Dispose_returns_while_a_read_is_waiting_and_is_idempotent()
    {
        var (server, _) = Pair();
        await Task.Delay(200);
        var disposing = Task.Run(() =>
        {
            server.Dispose();
            server.Dispose();
        });
        await disposing.WaitAsync(Wait); // a TimeoutException here: Dispose hung behind a read that was waiting
        Assert.True(server.IsClosed);
        Assert.False(server.Send("after dispose"));
    }

    [Fact]
    public void A_timeout_is_a_false_and_not_a_close()
    {
        var (server, _) = Pair();
        var before = Environment.TickCount64;
        Assert.False(server.TryReceive(TimeSpan.FromMilliseconds(300), out var line));
        Assert.Equal(string.Empty, line);
        Assert.InRange(Environment.TickCount64 - before, 250, 5000);
        Assert.False(server.IsClosed);
    }

    [Fact]
    public void A_line_that_is_too_long_is_not_sent_and_does_not_open_the_channel_to_it()
    {
        var (server, client) = Pair();
        Assert.True(server.Send(new string('a', PipeChannel.MaxLine)));
        Assert.False(server.Send(new string('a', PipeChannel.MaxLine + 1)));
        Assert.False(server.Send("two\nlines"));
        Assert.False(server.Send("carriage\rreturn"));
        Assert.Equal(PipeChannel.MaxLine, Receive(client).Length);
        Assert.False(client.TryReceive(TimeSpan.FromMilliseconds(200), out _));
    }

    [Fact]
    public void A_line_from_the_other_end_that_is_too_long_closes_the_channel()
    {
        Assert.True(LaunchNative.TryCreatePipe(out var server, out var rawClient));
        using (rawClient)
        {
            var channel = new PipeChannel(server);
            _open.Add(channel);
            Assert.True(LaunchNative.WriteAll(rawClient, Encoding.UTF8.GetBytes(new string('a', PipeChannel.MaxLine + 1) + "\n"), Wait));
            WaitUntilClosed(channel);
            Assert.False(channel.TryReceive(TimeSpan.FromMilliseconds(50), out _));
        }
    }

    [Fact]
    public void A_stream_with_no_end_of_line_closes_the_channel_instead_of_growing()
    {
        Assert.True(LaunchNative.TryCreatePipe(out var server, out var rawClient));
        using (rawClient)
        {
            var channel = new PipeChannel(server);
            _open.Add(channel);
            var chunk = Encoding.ASCII.GetBytes(new string('z', 8192));
            for (var i = 0; i < 4; i++)
            {
                if (!LaunchNative.WriteAll(rawClient, chunk, Wait))
                {
                    break; // the reader may already have given up and closed its end
                }
            }

            WaitUntilClosed(channel);
        }
    }

    [Fact]
    public void Bytes_that_are_not_text_close_the_channel()
    {
        Assert.True(LaunchNative.TryCreatePipe(out var server, out var rawClient));
        using (rawClient)
        {
            var channel = new PipeChannel(server);
            _open.Add(channel);
            Assert.True(LaunchNative.WriteAll(rawClient, [0xFF, 0xFE, 0xFD, (byte)'\n'], Wait));
            WaitUntilClosed(channel);
            Assert.False(channel.TryReceive(TimeSpan.FromMilliseconds(50), out _));
        }
    }

    [Fact]
    public void A_carriage_return_before_the_line_feed_is_dropped()
    {
        Assert.True(LaunchNative.TryCreatePipe(out var server, out var rawClient));
        using (rawClient)
        {
            var channel = new PipeChannel(server);
            _open.Add(channel);
            Assert.True(LaunchNative.WriteAll(rawClient, Encoding.UTF8.GetBytes("windows line\r\nunix line\n"), Wait));
            Assert.Equal("windows line", Receive(channel));
            Assert.Equal("unix line", Receive(channel));
        }
    }

    [Fact]
    public void A_handle_that_is_not_a_pipe_gives_no_channel_and_is_left_open()
    {
        var path = Path.Combine(Scratch.NewFolder("pipe-not-a-pipe"), "plain.bin");
        try
        {
            using var file = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            Assert.False(LaunchNative.IsPipe(file.DangerousGetHandle()));
            Assert.Null(PipeChannel.FromInheritedHandle(file.DangerousGetHandle()));
            Assert.Null(PipeChannel.FromInheritedHandle(0));
            Assert.Null(PipeChannel.FromInheritedHandle(-1));
            RandomAccess.Write(file, [1, 2, 3], 0); // still ours, still open
        }
        finally
        {
            Scratch.Delete(Path.GetDirectoryName(path)!);
        }
    }

    [Fact]
    public void A_real_pipe_handle_is_taken_over_by_FromInheritedHandle()
    {
        Assert.True(LaunchNative.TryCreatePipe(out var server, out var client));
        var value = client.DangerousGetHandle();
        Assert.True(LaunchNative.IsPipe(value));
        Assert.True(LaunchNative.IsInheritable(value), "the child's end must be the one that can be inherited");
        Assert.False(LaunchNative.IsInheritable(server.DangerousGetHandle()), "the parent's end must stay private");

        var serverChannel = new PipeChannel(server);
        _open.Add(serverChannel);
        client.SetHandleAsInvalid(); // the value is handed over: the channel below owns it, this object must not close it
        var clientChannel = PipeChannel.FromInheritedHandle(value);
        Assert.NotNull(clientChannel);
        _open.Add(clientChannel);
        Assert.True(clientChannel.Send("through the adopted handle"));
        Assert.Equal("through the adopted handle", Receive(serverChannel));
    }
}
