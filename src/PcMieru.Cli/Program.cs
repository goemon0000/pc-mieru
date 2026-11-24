using System.Text;
using PcMieru.Cli;
using PcMieru.Wire.Gate;

var utf8 = new UTF8Encoding(false);
TextWriter stdout;
TextWriter stderr;
var width = 0;
if (Console.IsOutputRedirected)
{
    stdout = new StreamWriter(Console.OpenStandardOutput(), utf8, 8192) { AutoFlush = false, NewLine = "\n" };
}
else
{
    try
    {
        Console.OutputEncoding = utf8;
        width = Console.WindowWidth;
    }
    catch (IOException)
    {
    }

    stdout = Console.Out;
}

if (Console.IsErrorRedirected)
{
    stderr = new StreamWriter(Console.OpenStandardError(), utf8, 1024) { AutoFlush = true, NewLine = "\n" };
}
else
{
    stderr = Console.Error;
}

// The start gate (DESIGN-B 4): the one place before every command and before the stdio RPC loop of the MCP server. A refusal is written to stderr only; stdout is the MCP channel and
// gets nothing. Access denied is Denied (4); a busy gate (an update being installed), a recovery that is pending or failed, a missing gate and a link are Failure (1).
// Held until Main returns: for the MCP server that is as long as the AI app keeps it running.
using var gate = InstallGate.EnterStartup();
if (!gate.IsOpen)
{
    stderr.WriteLine("pcmi: " + gate.Reason + (gate.RecoveryNotePath is { } note ? " (see " + note + ")" : string.Empty));
    return (int)(gate.Outcome == GateOutcome.Denied ? ExitCode.Denied : ExitCode.Failure);
}

var stdin = new StreamReader(Console.OpenStandardInput(), utf8, detectEncodingFromByteOrderMarks: false);
using var cancel = new CancellationTokenSource();

// Ctrl+C only cancels the token (the coordinator): Main owns the exit code. The process is forced out only when Main has not returned within
// the grace (a read blocked on stdin cannot be cancelled), and a second Ctrl+C waits while a check is joining its workers and saving the stopped run.
var shutdown = new ShutdownCoordinator(cancel);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.CtrlC();
};

var env = new CliEnvironment(stdin, stdout, stderr, CliEnvironment.DefaultFactory, width, ErrorIsTerminal: !Console.IsErrorRedirected);
return await shutdown.RunAsync(
    ct => CliApp.RunAsync(args, env, ct),
    async () =>
    {
        await stdout.FlushAsync().ConfigureAwait(false);
        await stderr.FlushAsync().ConfigureAwait(false);
    }).ConfigureAwait(false);
