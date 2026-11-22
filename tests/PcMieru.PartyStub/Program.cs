using PcMieru.Wire.Gate;

// TEST ONLY: the smallest program that passes the start gate (DESIGN-B 4), with the one call every exe makes.
//   free gate  : "party ok" on stdout, exit 0 (with "--hold": waits until stdin is closed first, holding the gate the whole time, then exit 0)
//   not open   : the fixed reason on stderr, nothing on stdout, exit 1
using var gate = InstallGate.EnterStartup();
if (!gate.IsOpen)
{
    Console.Error.WriteLine("party " + gate.Reason);
    return 1;
}

if (args.Contains("--hold", StringComparer.Ordinal))
{
    Console.Out.WriteLine("party holding");
    Console.Out.Flush();
    while (Console.In.ReadLine() is not null)
    {
    }
}

Console.Out.WriteLine("party ok");
return 0;
