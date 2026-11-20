using System.Diagnostics;
using System.Globalization;
using System.Text;
using PcMieru.Checks.Build;
using Xunit.Abstractions;

namespace PcMieru.Checks.Tests.Build;

/// <summary>
/// The configuration code of THIS machine, from the new readers against the OLD code (the harness of tools/pcb1-golden, built from the old
/// revision: <c>powershell -File tools/pcb1-golden/build-harness.ps1 -OldRepo &lt;pl-cloud&gt;</c>). It reads the real machine, so it is a
/// RealMachine test (CI leaves it out; run it by hand when no game is running: no CPU or GPU load, but WMI and DXGI answer slowly then).
/// <list type="number">
/// <item>The new readers collect the material ONCE. The new code and the old code (harness `replay`) are made from that same material and
/// must be byte-equal: the rules and the format are the same.</item>
/// <item>On the same machine state the old Windows readers (harness `windows`) and the new ones make a code each; they must be equal: the
/// readers see the same machine.</item>
/// </list>
/// **It prints only: equal yes/no, the number of items per category, the length of the code.** Never a name, a serial, the code or the JSON:
/// the material and the codes live in a temporary folder that is removed at the end, and a failure message says only which check failed.
/// </summary>
[Trait("Category", "RealMachine")]
public sealed class Pcb1RealMachineTests(ITestOutputHelper output)
{
    private const string HarnessVariable = "PCMIERU_PCB1_HARNESS";

    private static string HarnessPath() =>
        Environment.GetEnvironmentVariable(HarnessVariable) is { Length: > 0 } fromEnv
            ? fromEnv
            : Path.Combine(Path.GetTempPath(), "pcmieru-pcb1-harness", "out", "Pcb1Golden.exe");

    private static int Run(string exe, params string[] arguments)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in arguments)
        {
            start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("the harness did not start");

        // What the harness prints is not shown (its stderr names only an exception type, but this test shows nothing of it).
        var drain = Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
        if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            process.Kill(entireProcessTree: true);
            return -1;
        }

        drain.GetAwaiter().GetResult();
        return process.ExitCode;
    }

    private static bool SameBytes(string a, string b) => Encoding.UTF8.GetBytes(a).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(b));

    [Fact]
    public async Task The_new_code_equals_the_old_code_on_the_same_material_and_on_the_same_machine()
    {
        var exe = HarnessPath();
        Assert.True(
            File.Exists(exe),
            $"The harness of the old version is not built. Run: powershell -File tools/pcb1-golden/build-harness.ps1 -OldRepo <pl-cloud>  (or set {HarnessVariable} to Pcb1Golden.exe).");

        var dir = Path.Combine(Path.GetTempPath(), "pcmieru-pcb1-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 1. One reading of this machine through the new readers: that material, replayed to the old code and to the new one.
            var material = await Pcb1Material.ReadAsync(new WindowsBuildReaders());
            var materialFile = Path.Combine(dir, "material.json");
            material.Save(materialFile);

            var replayed = BuildCode.Make(await BuildCollector.CollectAsync(material.ToSources()));
            var oldReplayFile = Path.Combine(dir, "old-replay.txt");
            int replayExit = Run(exe, "replay", materialFile, oldReplayFile);
            Assert.True(replayExit == 0, $"the old code (replay) failed, exit {replayExit}");
            var oldReplay = File.ReadAllText(oldReplayFile, Encoding.UTF8);
            bool replayEqual = SameBytes(replayed.Code, oldReplay);

            // 2. On the same machine state: the old Windows readers, then the new ones, each end to end.
            var oldWindowsFile = Path.Combine(dir, "old-windows.txt");
            int windowsExit = Run(exe, "windows", oldWindowsFile);
            Assert.True(windowsExit == 0, $"the old Windows readers failed, exit {windowsExit}");
            var oldWindows = File.ReadAllText(oldWindowsFile, Encoding.UTF8);
            var newWindows = BuildCode.Make(await BuildCollector.CollectAsync());
            bool windowsEqual = SameBytes(newWindows.Code, oldWindows);

            // Only counts. Never a name, the code or the JSON.
            output.WriteLine("same material, old code vs new code, byte-equal: " + (replayEqual ? "yes" : "no"));
            output.WriteLine("same machine, old readers vs new readers, equal: " + (windowsEqual ? "yes" : "no"));
            foreach (var category in BuildConfig.Categories)
            {
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {category}: {newWindows.Config[category].Count} kinds, {newWindows.Config.Count(category)} items"));
            }

            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"code length: {newWindows.Code.Length}"));

            // A machine that gives nothing at all would make two empty codes "equal": the check must see something.
            Assert.False(newWindows.Config.IsEmpty, "this machine gave no part at all; the comparison proves nothing");
            Assert.True(replayEqual, "same material: the new code differs from the old code");
            Assert.True(windowsEqual, "same machine: the new readers' code differs from the old readers' code");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Assert.False(Directory.Exists(dir), "the temporary folder was not removed");
    }
}
