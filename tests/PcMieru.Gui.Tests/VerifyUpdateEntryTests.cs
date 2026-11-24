using System.Diagnostics;
using PcMieru.Update.Launch;
using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;

namespace PcMieru.Gui.Tests;

public sealed class VerifyUpdateEntryTests
{
    private const string Tx = "0123456789ab";

    private static string BuildFolder => Path.Combine(Harness.GuiSource, "bin",
#if DEBUG
        "Debug",
#else
        "Release",
#endif
        "net10.0-windows");

    [Fact]
    public void Numeric_handles_without_inheritance_are_refused_before_a_window_opens()
    {
        var info = new ProcessStartInfo(Path.Combine(BuildFolder, "PCMieru.exe"), $"--verify-update {Tx} 4000000000 4000000004")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(info);
        Assert.NotNull(process);
        try
        {
            var timer = Stopwatch.StartNew();
            while (!process.WaitForExit(20) && timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                process.Refresh();
                Assert.Equal(0, process.MainWindowHandle);
            }

            Assert.True(process.HasExited, "PCMieru.exe did not refuse the invalid handles within 10 s.");
            Assert.Equal(VerifyExit.PipeInvalid, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                BoundedProcess.Kill(process);
            }
        }
    }

    [Fact]
    public void The_product_window_draws_answers_pings_and_exits_with_the_inherited_gate()
    {
        using var folder = new UpdateTestFolder();
        var paths = folder.Paths;
        foreach (var file in Directory.EnumerateFiles(BuildFolder))
        {
            File.Copy(file, Path.Combine(paths.Install, Path.GetFileName(file)));
        }

        var exe = Path.Combine(paths.Install, "PCMieru.exe");
        File.WriteAllBytes(paths.Gate, GateFile.Bytes());
        using var gate = InstallGate.EnterExclusive(exe);
        Assert.True(gate.IsOpen, gate.Outcome.ToString());
        var files = new[] { new InstalledFile(GateFile.FileName, GateFile.Bytes().Length, GateFile.Sha256Hex) };
        var before = new InstalledRecord("1.2.3", UpdateResultNoteTests.Before, files);
        var after = new InstalledRecord("1.2.4", UpdateResultNoteTests.After, files);
        var journal = new TxJournal(Tx, before, before.Sha256, after, after.Sha256, []) { Phase = TxPhase.VerifyPending, LaunchAttempts = 1 };
        var bytes = journal.ToUtf8Bytes();
        Assert.True(TxJournal.TryParse(bytes, out _, out var error), error);
        File.WriteAllBytes(paths.Journal, bytes);
        var verifier = new ProductionLaunchVerifier(gate, new VerifyTimings { DrawnBudget = TimeSpan.FromSeconds(12) });
        var verdict = verifier.LaunchAndVerify(new LaunchRequest(paths.Install, Tx, 1, 3, after.Version, after.BuildId,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20)));
        Assert.True(verdict == LaunchVerdict.Ok, $"Product verify window did not complete the draw/ping/done exchange ({verdict}).");
        Assert.Equal(bytes, File.ReadAllBytes(paths.Journal));
    }
}
