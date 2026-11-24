using System.Globalization;
using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Launch;

/// <summary>The exit codes of <c>PCMieru.exe --verify-update</c> when it does not enter verify mode (no window is made for any of them). 0 is what a verified run that was told "done" returns.</summary>
public static class VerifyExit
{
    public const int Done = 0;

    /// <summary>The arguments are not <c>--verify-update &lt;tx&gt; &lt;gate handle&gt; &lt;pipe handle&gt;</c>.</summary>
    public const int BadArguments = 64;

    /// <summary>The handle is not the exclusive gate of this very install (or nothing was inherited).</summary>
    public const int GateNotInherited = 65;

    /// <summary>The handle is not a pipe.</summary>
    public const int PipeInvalid = 66;

    /// <summary>The journal on disk is not a VerifyPending of the same transaction.</summary>
    public const int NotWaitingForVerification = 67;
}

/// <summary>What <see cref="VerifyAdmission.Admit"/> let in: the gate the swapper handed down (held for the whole run), the pipe, and the transaction id.</summary>
public sealed class VerifyEntry : IDisposable
{
    internal VerifyEntry(string tx, GateEntry gate, IMessageChannel channel)
    {
        Tx = tx;
        Gate = gate;
        Channel = channel;
    }

    public string Tx { get; }

    public GateEntry Gate { get; }

    public IMessageChannel Channel { get; }

    public void Dispose()
    {
        Channel.Dispose();
        Gate.Dispose();
    }
}

/// <summary>
/// The condition on which <c>PCMieru.exe</c> runs in verify mode (DESIGN-B 5, "the conditions of the run"). A flag on the command line is not enough and is never believed: the program is a verification child only when
/// the swapper that started it gave it, by limited handle inheritance, (1) a handle to the EXCLUSIVE start gate of its own install (<see cref="InstallGate.AdoptInherited"/> proves it by the final path, the file
/// identity and a second open that must be refused), (2) a handle that is a pipe, and (3) the journal on disk, read here and never written, says that this very transaction is waiting for its launch check. Anything
/// else ends with a code before any window, any setting, any file or any network is touched; a person who types the option by hand gets <see cref="VerifyExit.GateNotInherited"/> or one of its neighbours.
/// </summary>
public static class VerifyAdmission
{
    public const string Option = "--verify-update";

    /// <summary>True when the first argument is the option (the program is then either a verification child or ends: it never goes on as a normal start).</summary>
    public static bool IsVerifyCommand(string[] args) => args.Length > 0 && args[0] == Option;

    /// <summary>The argument list of a verification child (what the runner writes after the program's name).</summary>
    public static string ArgumentsFor(string tx, nint gateHandle, nint pipeHandle) =>
        string.Join(' ', Option, tx, ((ulong)gateHandle).ToString(CultureInfo.InvariantCulture), ((ulong)pipeHandle).ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The entry for these arguments, or null with <paramref name="exitCode"/> (a <see cref="VerifyExit"/> value) when the program must not verify anything.
    /// </summary>
    /// <param name="executablePath">The path of this program's own exe (the gate file is the one beside it).</param>
    public static VerifyEntry? Admit(string[] args, string executablePath, out int exitCode)
    {
        exitCode = VerifyExit.BadArguments;
        if (args.Length != 4 || args[0] != Option || !IsTx(args[1]) || !TryHandle(args[2], out var gateHandle) || !TryHandle(args[3], out var pipeHandle) || gateHandle == pipeHandle)
        {
            return null;
        }

        if (!LaunchNative.IsPipe(pipeHandle))
        {
            exitCode = VerifyExit.PipeInvalid;
            return null;
        }

        var gate = InstallGate.AdoptInherited(executablePath, gateHandle);
        if (!gate.IsOpen)
        {
            exitCode = VerifyExit.GateNotInherited;
            return null;
        }

        if (!JournalWaits(executablePath, args[1]))
        {
            gate.Dispose();
            exitCode = VerifyExit.NotWaitingForVerification;
            return null;
        }

        var channel = PipeChannel.FromInheritedHandle(pipeHandle);
        if (channel is null)
        {
            gate.Dispose();
            exitCode = VerifyExit.PipeInvalid;
            return null;
        }

        exitCode = VerifyExit.Done;
        return new VerifyEntry(args[1], gate, channel);
    }

    /// <summary>Whether <c>I\update\journal.json</c> is a journal that waits for its launch check and has this transaction id. Read only.</summary>
    internal static bool JournalWaits(string executablePath, string tx)
    {
        try
        {
            var install = InstallGate.InstallFolderOf(executablePath);
            if (install is null)
            {
                return false;
            }

            var bytes = TxEngine.ReadSmall(new TxPaths(install).Journal, TxJson.MaxBytes);
            return bytes is not null && TxJournal.TryParse(bytes, out var journal, out _) && journal.Phase == TxPhase.VerifyPending && journal.Tx == tx;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsTx(string text) => text.Length == 12 && text.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private static bool TryHandle(string text, out nint value)
    {
        value = 0;
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number == 0 || number > uint.MaxValue)
        {
            return false;
        }

        value = (nint)number;
        return true;
    }
}

/// <summary>
/// The verification child's side of the launch check: reads the runner's lines and answers them. <b>Every answer is written from the program's UI thread</b> (<c>postToUi</c>: a BeginInvoke), never from the thread
/// that reads the pipe, because a program whose UI thread is stuck must not look alive; the runner takes a late or missing answer as a failure. <c>done</c> closes the program through the same route.
/// </summary>
public sealed class VerifySession : IDisposable
{
    private readonly IMessageChannel _channel;
    private readonly Action<Action> _postToUi;
    private readonly Action _close;
    private readonly Thread _reader;
    private volatile bool _stop;

    /// <param name="channel">The pipe to the runner.</param>
    /// <param name="postToUi">Runs the action on the UI thread, later (Control.BeginInvoke). It must not run it on the calling thread.</param>
    /// <param name="close">Closes the program's window (runs on the UI thread).</param>
    public VerifySession(IMessageChannel channel, Action<Action> postToUi, Action close)
    {
        _channel = channel;
        _postToUi = postToUi;
        _close = close;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "pcm-verify-reader" };
    }

    /// <summary>Starts answering. Called once the window exists (a ping that comes earlier waits in the pipe).</summary>
    public void Start() => _reader.Start();

    /// <summary>Tells the runner the window is shown and painted. Called from the UI thread, after the first paint.</summary>
    public void ReportDrawn() => _channel.Send(VerifyProtocol.Drawn);

    public void Dispose() => _stop = true;

    private void ReadLoop()
    {
        while (!_stop)
        {
            if (!_channel.TryReceive(TimeSpan.FromMilliseconds(200), out var line))
            {
                if (_channel.IsClosed)
                {
                    _postToUi(_close); // the runner is gone: there is nobody to verify for, so close (the job of the runner ends this program anyway)
                    return;
                }

                continue;
            }

            if (VerifyProtocol.NumberOf(line, VerifyProtocol.PingPrefix) is { } n)
            {
                _postToUi(() => _channel.Send(VerifyProtocol.Pong(n)));
            }
            else if (line == VerifyProtocol.Done)
            {
                _postToUi(_close);
                return;
            }
        }
    }
}
