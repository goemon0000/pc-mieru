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
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();

    // A read blocked on stdin (pcmi mcp in a terminal) cannot be interrupted; leave after a grace period.
    _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => Environment.Exit(1), TaskScheduler.Default);
};

var env = new CliEnvironment(stdin, stdout, stderr, CliEnvironment.DefaultFactory, width);
try
{
    return await CliApp.RunAsync(args, env, cancel.Token).ConfigureAwait(false);
}
finally
{
    await stdout.FlushAsync().ConfigureAwait(false);
    await stderr.FlushAsync().ConfigureAwait(false);
}
