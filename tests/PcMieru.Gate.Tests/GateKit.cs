using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using PcMieru.Wire.Gate;

namespace PcMieru.Gate.Tests;

/// <summary>The programs that pass the gate: the five shipped exes and the test-only party.</summary>
public enum Exe
{
    Gui,
    Cli,
    SensorHost,
    Helper,
    NetMeter,
    Party,
}

/// <summary>A scratch folder under the temporary folder of the run (the tests are run with TMP pointing into a scratch area) and everything that has to be undone before it can go.</summary>
internal sealed class Scratch : IDisposable
{
    private readonly List<Action> _undo = [];

    public Scratch()
    {
        // The long spelling of the temporary folder (TEMP is often the 8.3 one: C:\Users\ADMINI~1\...), found with the system's own long-name call and not with the code under test: the gate
        // answers with the FINAL path of the exe (long names), and a test that spells its folders the same way can then ask "is it the folder I made" in one comparison.
        var temp = Path.GetTempPath();
        Root = Path.Combine(Probes.LongPathOf(temp) ?? temp, "gate-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Sub(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Registers something to undo when the scratch folder goes (the last one registered first). Returns the same undo as an action that does it at once and only once.</summary>
    public Action OnDispose(Action undo)
    {
        var done = false;
        void Once()
        {
            if (!done)
            {
                done = true;
                undo();
            }
        }

        _undo.Add(Once);
        return Once;
    }

    /// <summary>An install folder with an exe (a stand-in file) and, by default, the gate file. Returns the path of the exe.</summary>
    public string Install(string name = "app", bool gate = true)
    {
        var folder = Sub(name);
        var exe = Path.Combine(folder, "PCMieru.exe");
        File.WriteAllText(exe, "not a program");
        if (gate)
        {
            File.WriteAllBytes(Path.Combine(folder, GateFile.FileName), GateFile.Bytes());
        }

        return exe;
    }

    public static string GateOf(string exe) => Path.Combine(Path.GetDirectoryName(exe)!, GateFile.FileName);

    public static string UpdateOf(string exe) => Path.Combine(Path.GetDirectoryName(exe)!, GateFile.UpdateFolderName);

    /// <summary>mklink /J: a junction needs no privilege. The target is never removed with the link.</summary>
    public Action Junction(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        // One string on purpose: cmd.exe does not read the escaping that ArgumentList would put around a quoted pair of paths.
        info.Arguments = "/c mklink /J \"" + link + "\" \"" + target + "\"";
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "mklink /J failed: " + output);
        return OnDispose(() => Directory.Delete(link)); // a junction: removes the link only
    }

    /// <summary>
    /// mklink /H: a hard link (a second name for a file of the same volume) needs no privilege. The link is the new name, the target the existing file. Removing the link removes that one name only.
    /// Only ever made inside <see cref="Root"/>.
    /// </summary>
    public Action Hardlink(string link, string target)
    {
        Assert.StartsWith(Root, link, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Root, target, StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        // One string on purpose (see Junction).
        info.Arguments = "/c mklink /H \"" + link + "\" \"" + target + "\"";
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "mklink /H failed: " + output);
        return OnDispose(() => File.Delete(link));
    }

    /// <summary>
    /// A deny entry for the current user on a file or a folder of the scratch area, taken back when the scratch folder goes. (The owner can always change the rights back, which is what makes
    /// this possible without any elevation.) Never used outside <see cref="Root"/>.
    /// </summary>
    public Action Deny(string path, FileSystemRights rights)
    {
        Assert.StartsWith(Root, path, StringComparison.OrdinalIgnoreCase);
        var user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(user, rights, AccessControlType.Deny);
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl();
            security.AddAccessRule(rule);
            info.SetAccessControl(security);
            return OnDispose(() =>
            {
                var back = info.GetAccessControl();
                back.RemoveAccessRule(rule);
                info.SetAccessControl(back);
            });
        }
        else
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.AddAccessRule(rule);
            info.SetAccessControl(security);
            return OnDispose(() =>
            {
                var back = info.GetAccessControl();
                back.RemoveAccessRule(rule);
                info.SetAccessControl(back);
            });
        }
    }

    public void Dispose()
    {
        for (var i = _undo.Count - 1; i >= 0; i--)
        {
            try
            {
                _undo[i]();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
            }
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }

                    Directory.Delete(Root, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}

/// <summary>What a child process did.</summary>
internal sealed record Ran(int ExitCode, string Out, string Err);

/// <summary>A child process that may still be running (its gate is held while it does).</summary>
internal sealed class RunningChild : IDisposable
{
    private readonly Process _process;
    private readonly System.Collections.Concurrent.BlockingCollection<string> _lines = [];
    private readonly StringBuilder _out = new();
    private readonly StringBuilder _err = new();
    private readonly object _lock = new();

    public RunningChild(Process process)
    {
        _process = process;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (_lock)
                {
                    _out.Append(e.Data).Append('\n');
                }

                _lines.Add(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (_lock)
                {
                    _err.Append(e.Data).Append('\n');
                }
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public bool HasExited => _process.HasExited;

    /// <summary>Writes to the child's stdin (the child may have left: then this throws an IOException).</summary>
    public void Write(string text)
    {
        _process.StandardInput.Write(text);
        _process.StandardInput.Flush();
    }

    public void CloseInput()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The next line the child wrote to stdout that nobody has read yet; null when none came within the time.</summary>
    public string? ReadLine(TimeSpan timeout) => _lines.TryTake(out var line, timeout) ? line : null;

    /// <summary>Waits for the end of the child and gives everything it wrote (every line ends with a line feed).</summary>
    public Ran Wait(int timeoutMs = 120_000)
    {
        if (!_process.WaitForExit(timeoutMs))
        {
            _process.Kill(entireProcessTree: true);
            throw new TimeoutException("the child did not end within " + timeoutMs + " ms");
        }

        _process.WaitForExit(); // the one without a time: it also waits for the end of the redirected streams
        lock (_lock)
        {
            return new Ran(_process.ExitCode, _out.ToString(), _err.ToString());
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
        _lines.Dispose();
    }
}

internal static class Child
{
    /// <summary>Starts a program with its own APPDATA and LOCALAPPDATA (never the developer's), stdin open until <see cref="RunningChild.CloseInput"/>.</summary>
    public static RunningChild Start(string exe, IEnumerable<string> args, string appData, string? workingDirectory = null, IDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(exe)!,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        Directory.CreateDirectory(appData);
        info.Environment["APPDATA"] = appData;
        info.Environment["LOCALAPPDATA"] = appData;
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                info.Environment[pair.Key] = pair.Value;
            }
        }

        return new RunningChild(Process.Start(info)!);
    }

    /// <summary>Runs to the end with stdin closed at once.</summary>
    public static Ran Run(string exe, IEnumerable<string> args, string appData, string? workingDirectory = null, IDictionary<string, string>? environment = null, int timeoutMs = 120_000)
    {
        using var child = Start(exe, args, appData, workingDirectory, environment);
        child.CloseInput();
        return child.Wait(timeoutMs);
    }
}

/// <summary>Where the build outputs of the exes are, and one copy of each in a scratch folder with a gate file the tests wrote.</summary>
internal static class Repo
{
    public static string Root { get; } = FindRoot();

    /// <summary>"Release" or "Debug": the configuration this test project was built with, which is the one its references were built with too.</summary>
    public static string Configuration { get; } = FindConfiguration();

    public static string FileText(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "PcMieru.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("PcMieru.slnx was not found above " + AppContext.BaseDirectory);
    }

    private static string FindConfiguration()
    {
        var parts = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar);
        for (var i = parts.Length - 2; i >= 0; i--)
        {
            if (string.Equals(parts[i], "bin", StringComparison.OrdinalIgnoreCase))
            {
                return parts[i + 1];
            }
        }

        throw new InvalidOperationException("no bin\\<configuration> in " + AppContext.BaseDirectory);
    }

    public static string BuildFolderOf(Exe exe)
    {
        var project = exe switch
        {
            Exe.Gui => "src/PcMieru.Gui",
            Exe.Cli => "src/PcMieru.Cli",
            Exe.SensorHost => "src/PcMieru.SensorHost",
            Exe.Helper => "src/PcMieru.Helper",
            Exe.NetMeter => "src/PcMieru.NetMeter",
            _ => "tests/PcMieru.PartyStub",
        };
        return Path.Combine(Root, project.Replace('/', Path.DirectorySeparatorChar), "bin", Configuration, "net10.0-windows");
    }

    public static string ExeNameOf(Exe exe) => exe switch
    {
        Exe.Gui => "PCMieru.exe",
        Exe.Cli => "pcmi.exe",
        Exe.SensorHost => "PCMieru.SensorHost.exe",
        Exe.Helper => "PCMieru.Helper.exe",
        Exe.NetMeter => "PCMieru.NetMeter.exe",
        _ => "PCMieru.Party.exe",
    };

    public static void CopyBuild(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(from))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
            {
                continue; // a leftover of a publish into the build folder, never part of a build
            }

            CopyBuild(directory, Path.Combine(to, name));
        }
    }
}

/// <summary>An exe's build folder copied into the scratch area, with the gate file written by the test (never the build's own copy of it).</summary>
internal sealed record Box(Exe Exe, string Folder)
{
    public string ExePath => Path.Combine(Folder, Repo.ExeNameOf(Exe));

    public string GatePath => Path.Combine(Folder, GateFile.FileName);

    public string UpdatePath => Path.Combine(Folder, GateFile.UpdateFolderName);
}

/// <summary>The class fixture of the process tests: one scratch root, one copy per exe (made when first asked for).</summary>
public sealed class Boxes : IDisposable
{
    private readonly Scratch _scratch = new();
    private readonly Dictionary<Exe, Box> _boxes = [];
    private readonly object _lock = new();

    internal Scratch Scratch => _scratch;

    /// <summary>The APPDATA and LOCALAPPDATA of the children: a folder of the scratch area, so no child can read or write the developer's.</summary>
    internal string AppData => Path.Combine(_scratch.Root, "appdata");

    internal Box Get(Exe exe)
    {
        lock (_lock)
        {
            if (!_boxes.TryGetValue(exe, out var box))
            {
                var source = Repo.BuildFolderOf(exe);
                Assert.True(File.Exists(Path.Combine(source, Repo.ExeNameOf(exe))), "not built: " + Path.Combine(source, Repo.ExeNameOf(exe)));
                var folder = Path.Combine(_scratch.Root, "box-" + exe.ToString().ToLowerInvariant());
                Repo.CopyBuild(source, folder);
                File.WriteAllBytes(Path.Combine(folder, GateFile.FileName), GateFile.Bytes());
                box = new Box(exe, folder);
                _boxes[exe] = box;
            }

            return box;
        }
    }

    public void Dispose() => _scratch.Dispose();
}

/// <summary>Ways the gate can be made not to open, on a real folder, each undone by the returned object.</summary>
internal static class Situation
{
    public static IDisposable Apply(Scratch scratch, string exePath, GateOutcome outcome)
    {
        var undo = new List<Action>();
        var gate = Scratch.GateOf(exePath);
        var update = Scratch.UpdateOf(exePath);
        switch (outcome)
        {
            case GateOutcome.Open:
                break;

            case GateOutcome.Busy:
            {
                var holder = InstallGate.EnterExclusive(exePath);
                Assert.True(holder.IsOpen, holder.Detail);
                undo.Add(holder.Dispose);
                break;
            }

            case GateOutcome.RecoveryPending:
                Directory.CreateDirectory(update);
                File.WriteAllText(Path.Combine(update, GateFile.JournalName), "{}");
                undo.Add(() => Directory.Delete(update, recursive: true));
                break;

            case GateOutcome.RecoveryFailed:
                Directory.CreateDirectory(update);
                File.WriteAllText(Path.Combine(update, GateFile.JournalName), "{}");
                File.WriteAllText(Path.Combine(update, GateFile.RecoveryFailedName), string.Empty); // the sentinel: its presence is the message
                undo.Add(() => Directory.Delete(update, recursive: true));
                break;

            case GateOutcome.MissingGate:
                File.Delete(gate);
                undo.Add(() => File.WriteAllBytes(gate, GateFile.Bytes()));
                break;

            case GateOutcome.Denied:
                undo.Add(scratch.Deny(gate, FileSystemRights.ReadData));
                break;

            case GateOutcome.ReparseRefused:
            {
                var elsewhere = scratch.Sub("elsewhere-" + Guid.NewGuid().ToString("N")[..6]);
                undo.Add(scratch.Junction(update, elsewhere));
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        return new Undo(undo);
    }

    private sealed class Undo(List<Action> actions) : IDisposable
    {
        public void Dispose()
        {
            for (var i = actions.Count - 1; i >= 0; i--)
            {
                actions[i]();
            }
        }
    }
}

/// <summary>Probes that decide at discovery time whether a test can run on this machine; a test that cannot is reported as skipped with the reason, never as passed.</summary>
internal static class Probes
{
    public static readonly Lazy<string?> NoSymlink = new(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "gate-probe-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, "target.txt");
            File.WriteAllText(target, "x");
            File.CreateSymbolicLink(Path.Combine(folder, "link.txt"), target);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message.ReplaceLineEndings(" ");
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    });

    /// <summary>Null when the volume of the scratch area makes 8.3 names, else why not. Nothing is changed: a folder with a long name is made and its short name asked for.</summary>
    public static readonly Lazy<string?> No8dot3 = new(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "gate-probe-a-long-folder-name-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(folder);
            var shortPath = ShortPathOf(folder);
            if (shortPath is not null && !string.Equals(Path.GetFileName(shortPath), Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return "8.3 names are not made on the volume of " + Path.GetTempPath() + " (read-only 'fsutil 8dot3name query' says: " + Fsutil() + ")";
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    });

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint length);

    /// <summary>The 8.3 form of an existing path, or null when the system has none to give.</summary>
    public static string? ShortPathOf(string path)
    {
        var buffer = new StringBuilder(1024);
        var length = GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
        return length == 0 || length > buffer.Capacity ? null : buffer.ToString();
    }

    /// <summary>The long form of an existing path (8.3 names written out), with no trailing separator; null when the system has none to give. Independent of the code under test.</summary>
    public static string? LongPathOf(string path)
    {
        var buffer = new StringBuilder(1024);
        var length = GetLongPathNameW(path.TrimEnd(Path.DirectorySeparatorChar), buffer, (uint)buffer.Capacity);
        return length == 0 || length > buffer.Capacity ? null : buffer.ToString();
    }

    private static string Fsutil()
    {
        try
        {
            var info = new ProcessStartInfo("fsutil.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("8dot3name");
            info.ArgumentList.Add("query");
            info.ArgumentList.Add(Path.GetPathRoot(Path.GetTempPath())!.TrimEnd(Path.DirectorySeparatorChar));
            using var process = Process.Start(info)!;
            var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(15_000);
            return text.ReplaceLineEndings(" ").Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "fsutil could not be run: " + ex.Message;
        }
    }
}

/// <summary>A test that needs a file symbolic link: skipped (with the reason) where the account may not make one.</summary>
public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        if (Probes.NoSymlink.Value is { } why)
        {
            Skip = "a file symbolic link cannot be made here without a privilege: " + why;
        }
    }
}

/// <summary>A test that needs 8.3 short names on the volume of the scratch area: skipped (with the reason) where there are none. The setting is never changed.</summary>
public sealed class ShortNameFactAttribute : FactAttribute
{
    public ShortNameFactAttribute()
    {
        if (Probes.No8dot3.Value is { } why)
        {
            Skip = why;
        }
    }
}
