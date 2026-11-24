using System.Globalization;
using Microsoft.Win32.SafeHandles;
using PcMieru.Update.Feed;
using PcMieru.Update.Transaction;

namespace PcMieru.Update.Launch;

/// <summary>What the runner said to a hand-off: it took the update (the GUI then saves its settings, stops its children and closes) or refused it for a reason (the GUI stays open and shows the reason).</summary>
public sealed record ApplyHandOffResult(bool Accepted, ApplyRefusal Refusal, string Detail)
{
    public static ApplyHandOffResult Yes { get; } = new(true, ApplyRefusal.BadMessage, "accepted");

    public static ApplyHandOffResult No(ApplyRefusal refusal, string detail) => new(false, refusal, detail);
}

/// <summary>What the GUI's hand-off takes from outside; a test replaces what it needs.</summary>
public sealed class HandOffOptions
{
    /// <summary>Makes <c>I\update\recover.exe</c> a verified copy of the installed Swap.exe (or says it cannot).</summary>
    public Func<string, HandOffResult> Prepare { get; init; } = static folder => RunnerHandOff.Prepare(folder);

    /// <summary>How long the runner may take to answer.</summary>
    public TimeSpan AnswerWithin { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Starts the runner. Null: the real start (<see cref="LaunchNative.TryStart"/>).</summary>
    internal Func<LaunchNative.StartRequest, (LaunchNative.Child? Child, int Error)>? Start { get; init; }
}

/// <summary>
/// The GUI's side of the hand-off to the runner (DESIGN-B 3.3 and 5): it makes <c>I\update\recover.exe</c> ready, starts it as <c>recover.exe --apply &lt;pipe handle&gt; &lt;process handle&gt;</c> with exactly two
/// inherited handles (the end of a new pipe and an inheritable copy of this process's own handle, which the runner waits on, so that it waits for THIS process and not for a number that could have been reused),
/// and sends ONE line (<see cref="ApplyMessage"/>) over the pipe. Nothing else is on the command line. The runner answers <c>accepted</c> or a refusal; only after <c>accepted</c> may the caller save settings,
/// stop its children and exit. On a refusal or no answer within <see cref="HandOffOptions.AnswerWithin"/> the caller stays open and kills nothing (this class kills nothing).
/// </summary>
public static class GuiHandOff
{
    public static ApplyHandOffResult Run(string installFolder, StagedUpdate staged, HandOffOptions? options = null)
    {
        options ??= new HandOffOptions();
        var ready = options.Prepare(installFolder);
        if (!ready.IsReady)
        {
            return ApplyHandOffResult.No(ApplyRefusal.RunnerNotReady, $"{ready.Status}: {ready.Detail}");
        }

        var paths = new TxPaths(installFolder);
        if (!LaunchNative.TryCreatePipe(out var server, out var client))
        {
            return ApplyHandOffResult.No(ApplyRefusal.RunnerNotReady, "the pipe could not be made");
        }

        var processValue = LaunchNative.DuplicateSelfInheritable();
        PipeChannel? channel = null;
        try
        {
            if (processValue == 0)
            {
                return ApplyHandOffResult.No(ApplyRefusal.RunnerNotReady, "this process's handle could not be copied");
            }

            var clientValue = client.DangerousGetHandle();
            var commandLine = LaunchNative.Quote(paths.Recover) + " --apply " + ((ulong)clientValue).ToString(CultureInfo.InvariantCulture) + " " + ((ulong)processValue).ToString(CultureInfo.InvariantCulture);
            var request = new LaunchNative.StartRequest(paths.Recover, commandLine, installFolder, [clientValue, processValue], NoWindow: true, InJob: false);
            var (child, error) = options.Start is { } start ? start(request) : StartReal(request);
            LaunchNative.CloseHandle(processValue); // the runner has its own copy
            processValue = 0;
            client.Dispose();
            if (child is null)
            {
                return ApplyHandOffResult.No(ApplyRefusal.RunnerNotReady, "recover.exe could not be started (error " + error.ToString(CultureInfo.InvariantCulture) + ")");
            }

            child.Dispose(); // not a job: the runner outlives this program on purpose; only our handle to it is let go
            channel = new PipeChannel(server);
            server = new SafeFileHandle();
            if (!channel.Send(ApplyMessage.Of(staged).ToLine()))
            {
                return ApplyHandOffResult.No(ApplyRefusal.NoAnswer, "the runner closed the pipe");
            }

            if (!channel.TryReceive(options.AnswerWithin, out var answer))
            {
                return ApplyHandOffResult.No(ApplyRefusal.NoAnswer, channel.IsClosed ? "the runner ended without an answer" : "no answer in time");
            }

            return ApplyAnswer.TryParse(answer, out var refusal) ? ApplyHandOffResult.Yes : ApplyHandOffResult.No(refusal, answer);
        }
        finally
        {
            if (processValue != 0)
            {
                LaunchNative.CloseHandle(processValue);
            }

            channel?.Dispose(); // an abandoned hand-off closes the pipe: the runner's answer then fails to send, and it swaps nothing
            if (!server.IsInvalid)
            {
                server.Dispose();
            }

            client.Dispose();
        }
    }

    private static (LaunchNative.Child? Child, int Error) StartReal(LaunchNative.StartRequest request)
    {
        var child = LaunchNative.TryStart(request, out var error);
        return (child, error);
    }
}
