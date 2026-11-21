using System.Text;
using PcMieru.Cli;

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

var stdin = new StreamReader(Console.OpenStandardInput(), utf8, detectEncodingFromByteOrderMarks: false);
using var cancel = new CancellationTokenSource();
var pressed = 0;
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();

    // A second Ctrl+C leaves at once, whatever is still running.
    if (Interlocked.Increment(ref pressed) > 1)
    {
        Environment.Exit(1);
    }

    // A read blocked on stdin (pcmi mcp in a terminal) cannot be interrupted; leave after a grace period. A check that is stopping is
    // given longer (CliShutdown): it joins its workers and saves the stopped run, which 2 seconds could cut off.
    _ = CliShutdown.WaitForLeaveAsync().ContinueWith(_ => Environment.Exit(1), TaskScheduler.Default);
};

var env = new CliEnvironment(stdin, stdout, stderr, CliEnvironment.DefaultFactory, width, ErrorIsTerminal: !Console.IsErrorRedirected);
try
{
    return await CliApp.RunAsync(args, env, cancel.Token).ConfigureAwait(false);
}
finally
{
    await stdout.FlushAsync().ConfigureAwait(false);
    await stderr.FlushAsync().ConfigureAwait(false);
}
