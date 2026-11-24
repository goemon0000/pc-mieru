using System.Collections.Concurrent;
using PcMieru.Update.Launch;

namespace PcMieru.Update.Tests;

/// <summary>
/// The verification child's side of the launch check, with a scripted channel and a "UI thread" that is this test's own thread: every answer is written by the action that was posted to the UI thread and by nothing else.
/// A program whose UI thread is stuck must not look alive, so a pong written from the thread that reads the pipe is exactly the fault these tests are for (LaunchProcessTests sees it only after minutes, as a check that took too long).
/// </summary>
public sealed class VerifySessionTests
{
    private sealed class ScriptedChannel(params string[] lines) : IMessageChannel
    {
        private readonly ConcurrentQueue<string> _lines = new(lines);

        public ConcurrentQueue<(string Line, int Thread)> Sent { get; } = [];

        public bool IsClosed => false;

        public bool Send(string line)
        {
            Sent.Enqueue((line, Environment.CurrentManagedThreadId));
            return true;
        }

        public bool TryReceive(TimeSpan timeout, out string line)
        {
            if (_lines.TryDequeue(out line!))
            {
                return true;
            }

            Thread.Sleep(Math.Min(10, (int)timeout.TotalMilliseconds));
            line = string.Empty;
            return false;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Waits until <paramref name="done"/> holds (the reader thread has done what it does with the lines); false when it never did.</summary>
    private static bool Until(Func<bool> done)
    {
        var limit = Environment.TickCount64 + 10_000;
        while (!done())
        {
            if (Environment.TickCount64 > limit)
            {
                return false;
            }

            Thread.Sleep(5);
        }

        return true;
    }

    [Fact]
    public void A_ping_is_answered_by_the_action_posted_to_the_ui_thread_and_never_by_the_reading_thread()
    {
        var channel = new ScriptedChannel(VerifyProtocol.Ping(1), VerifyProtocol.Ping(2));
        var posted = new ConcurrentQueue<Action>();
        using var session = new VerifySession(channel, posted.Enqueue, () => { });

        session.Start();

        // The reader has done its part when both pings are posted; a reader that answers itself writes a pong instead.
        Assert.True(Until(() => posted.Count == 2 || !channel.Sent.IsEmpty), "the reader neither posted the pings nor answered them");
        Assert.True(channel.Sent.IsEmpty, "a pong was written before the UI thread ran anything: " + string.Join(", ", channel.Sent.Select(s => s.Line)));
        Assert.Equal(2, posted.Count);

        // The UI thread (this one) serves its queue: now, and only now, the answers go out, in the order of the pings.
        while (posted.TryDequeue(out var action))
        {
            action();
        }

        Assert.Equal([VerifyProtocol.Pong(1), VerifyProtocol.Pong(2)], channel.Sent.Select(s => s.Line));
        Assert.All(channel.Sent, sent => Assert.Equal(Environment.CurrentManagedThreadId, sent.Thread));
    }

    [Fact]
    public void Done_closes_the_window_through_the_ui_thread_and_only_then_the_run_counts_as_complete()
    {
        var channel = new ScriptedChannel(VerifyProtocol.Done);
        var posted = new ConcurrentQueue<Action>();
        var closed = 0;
        using var session = new VerifySession(channel, posted.Enqueue, () => closed++);

        session.Start();

        Assert.True(Until(() => !posted.IsEmpty), "done was not posted to the UI thread");
        Assert.False(session.Completed);
        Assert.Equal(0, closed);

        while (posted.TryDequeue(out var action))
        {
            action();
        }

        Assert.True(session.Completed);
        Assert.Equal(1, closed);
        Assert.True(channel.Sent.IsEmpty);
    }
}
