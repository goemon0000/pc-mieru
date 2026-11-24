using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PcMieru.Update.Feed;
using PcMieru.Update.Launch;
using PcMieru.Update.Transaction;

// TEST ONLY (see the csproj). The doubles of the launch check and of the hand-off:
//   --verify-update <tx> <gate handle> <pipe handle> [<mode> [<report file> <parent pid>]]
//        what PCMieru.exe does in verify mode, with the product's own admission (VerifyAdmission) and session (VerifySession). The mode is the 5th argument or, when it is missing, the first line of txchild-mode.txt beside the
//        exe (a generation of the test's install says in its own files how its "PCMieru.exe" behaves). Modes: good, hang (the UI thread never serves its queue), nopong (draws, never answers), die (ends at once),
//        dielate (draws, answers, then ends), nodrawn (never says drawn), unadmitted (admission only: ends with its code, or 0 when it was admitted).
//   handoff <install> <stage folder> <manifest sha256> <version> <build id> <tag>
//        what the GUI does: the product's GuiHandOff towards I\update\recover.exe; exit 0 when the runner said accepted (like the GUI closing), 5 on a refusal, 6 on no answer.
//   (no arguments) the started program: appends one line to <install>\..\started.log (the first line of generation.txt in the install) and ends.
namespace PcMieru.TxChild;

internal static class ChildModes
{
    public const int Refused = 5;
    public const int NoAnswer = 6;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetHandleInformation(nint handle, out uint flags);

    [DllImport("kernel32.dll")]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int which);

    [DllImport("kernel32.dll")]
    private static extern bool DuplicateHandle(SafeProcessHandle source, nint handle, nint target, out SafeFileHandle duplicate, uint access, bool inheritHandle, uint options);

    [DllImport("kernelbase.dll")]
    private static extern bool CompareObjectHandles(nint first, SafeFileHandle second);

    private const uint HandleFlagInherit = 1;
    private const uint ProcessDuplicateHandle = 0x40;
    private const uint DuplicateSameAccess = 2;

    public static int Verify(string[] args)
    {
        var exe = Environment.ProcessPath!;
        var mode = args.Length > 4 ? args[4] : ModeFile(exe);
        var report = args.Length > 5 ? args[5] : null;

        Report(report, "pid " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        if (args.Length > 6)
        {
            var parentId = int.Parse(args[6], CultureInfo.InvariantCulture);
            var gate = nint.Parse(args[2], CultureInfo.InvariantCulture);
            var pipe = nint.Parse(args[3], CultureInfo.InvariantCulture);
            Report(report, "inherited " + string.Join(',', InheritedHandles(parentId, gate, pipe)));
        }

        using var entry = VerifyAdmission.Admit([.. args.Take(4)], exe, out var code);
        if (entry is null)
        {
            Report(report, $"refused {code}");
            return code;
        }

        if (mode == "unadmitted")
        {
            return 0;
        }

        var queue = new BlockingCollection<Action>();
        var session = new VerifySession(entry.Channel, queue.Add, queue.CompleteAdding);
        switch (mode)
        {
            case "die":
                return 9;
            case "nodrawn":
                Thread.Sleep(Timeout.Infinite);
                return 0;
            case "nopong":
                session.ReportDrawn();
                Thread.Sleep(Timeout.Infinite);
                return 0;
            case "hang":
                session.ReportDrawn();
                session.Start();
                Thread.Sleep(Timeout.Infinite); // the "UI thread" never serves its queue: the reader posts the pongs and nobody sends them
                return 0;
            case "dielate":
                session.ReportDrawn();
                session.Start();
                Pump(queue, TimeSpan.FromSeconds(1));
                return 9;
            default:
                session.ReportDrawn();
                session.Start();
                Pump(queue, Timeout.InfiniteTimeSpan);
                session.Dispose();
                return 0;
        }
    }

    /// <summary>Serves the queue on THIS thread (the "UI thread") until it is closed or the time is up.</summary>
    private static void Pump(BlockingCollection<Action> queue, TimeSpan limit)
    {
        var until = limit == Timeout.InfiniteTimeSpan ? long.MaxValue : Environment.TickCount64 + (long)limit.TotalMilliseconds;
        while (Environment.TickCount64 < until && !queue.IsCompleted)
        {
            if (queue.TryTake(out var action, 50))
            {
                action();
            }
        }
    }

    public static int HandOff(string[] args)
    {
        if (args.Length != 7)
        {
            Console.Error.WriteLine("bad command line");
            return 2;
        }

        var staged = new StagedUpdate { Folder = args[2], ManifestSha256 = args[3], Version = args[4], BuildId = args[5], Tag = args[6] };
        var options = new HandOffOptions { Prepare = _ => new HandOffResult(HandOffStatus.Ready, "the double does not place recover.exe"), AnswerWithin = TimeSpan.FromSeconds(30) };
        var answer = GuiHandOff.Run(args[1], staged, options);
        if (answer.Accepted)
        {
            Console.Out.WriteLine("handoff accepted");
            return 0;
        }

        Console.Out.WriteLine($"handoff refused {answer.Refusal} {answer.Detail}");
        return answer.Refusal == ApplyRefusal.NoAnswer ? NoAnswer : Refused;
    }

    /// <summary>The program that a swap's end starts (the new or the old "GUI"): says which generation it is.</summary>
    public static int Started()
    {
        var folder = Path.GetDirectoryName(Environment.ProcessPath)!;
        var generation = File.Exists(Path.Combine(folder, "generation.txt")) ? File.ReadAllLines(Path.Combine(folder, "generation.txt")).FirstOrDefault() : "unknown";
        var line = $"started {generation} pid={Environment.ProcessId}{Environment.NewLine}";
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetDirectoryName(folder)!, "started.log"), line);
                return 0;
            }
            catch (IOException)
            {
                Thread.Sleep(20);
            }
        }

        return 3;
    }

    private static string ModeFile(string exe)
    {
        var path = Path.Combine(Path.GetDirectoryName(exe)!, "txchild-mode.txt");
        return File.Exists(path) ? File.ReadAllLines(path).FirstOrDefault()?.Trim() ?? "good" : "good";
    }

    private static void Report(string? file, string line)
    {
        if (file is not null)
        {
            File.AppendAllText(file, line + Environment.NewLine);
        }
    }

    /// <summary>Inherited handles keep their numeric value and refer to the same kernel object. The CLR can also open inheritable handles before Main.</summary>
    private static IEnumerable<ulong> InheritedHandles(int parentId, nint gate, nint pipe)
    {
        using var parent = OpenProcess(ProcessDuplicateHandle, false, parentId);
        if (parent.IsInvalid)
        {
            throw new InvalidOperationException("Could not open the test parent process.");
        }

        var standard = new HashSet<nint> { GetStdHandle(-10), GetStdHandle(-11), GetStdHandle(-12) };
        for (var value = 4L; value < 0x40000; value += 4)
        {
            var handle = (nint)value;
            if (!GetHandleInformation(handle, out var flags) || (flags & HandleFlagInherit) == 0)
            {
                continue;
            }

            // The parent closes its launch copies right after CreateProcess. VerifyAdmission checks the two received objects below.
            if (handle == gate || handle == pipe)
            {
                yield return (ulong)value;
            }
            else if (!standard.Contains(handle) && DuplicateHandle(parent, handle, GetCurrentProcess(), out var copy, 0, false, DuplicateSameAccess))
            {
                using (copy)
                {
                    if (CompareObjectHandles(handle, copy))
                    {
                        yield return (ulong)value;
                    }
                }
            }
        }
    }
}
