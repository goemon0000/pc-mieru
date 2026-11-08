using System.Runtime.InteropServices;
using System.Text.Json;
using PcMieru.Core.Management;
using PcMieru.Wire;

namespace PcMieru.Core.Tests.Management;

/// <summary>Stands in for "runas": records what it was asked to start and answers as the test says. Nothing is started, nothing is prompted.</summary>
internal sealed class FakeLauncher(Func<FakeLauncher.Call, LaunchResult> script) : IElevatedLauncher
{
    public sealed record Call(string Exe, string Arguments, string[] Args, string ResultPath, string Nonce, bool ResultFileExisted, long ResultFileLength);

    public List<Call> Calls { get; } = [];

    public LaunchResult Run(string exe, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var args = HelperClientTests.SplitCommandLine(arguments);
        var resultPath = args[(Array.IndexOf(args, "--result") + 1)..][0];
        var nonce = args[(Array.IndexOf(args, "--nonce") + 1)..][0];
        var call = new Call(exe, arguments, args, resultPath, nonce, File.Exists(resultPath), File.Exists(resultPath) ? new FileInfo(resultPath).Length : -1);
        Calls.Add(call);
        return script(call);
    }
}

public class HelperClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pcmieru-helperclient-" + Guid.NewGuid().ToString("N"));
    private readonly string _helperExe;
    private readonly string _results;

    public HelperClientTests()
    {
        Directory.CreateDirectory(_dir);
        _helperExe = Path.Combine(_dir, "PCMieru.Helper.exe");
        File.WriteAllBytes(_helperExe, [0x4D, 0x5A]); // never started: only has to exist
        _results = Path.Combine(_dir, "results");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>The Windows reading of a command line (what the Helper's own runtime does with it), after a dummy program name.</summary>
    internal static string[] SplitCommandLine(string arguments)
    {
        var pointer = CommandLineToArgvW("prog " + arguments, out var count);
        Assert.NotEqual(IntPtr.Zero, pointer);
        try
        {
            var result = new string[count - 1];
            for (var i = 1; i < count; i++)
            {
                result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!;
            }

            return result;
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    private HelperClient Client(IElevatedLauncher launcher, string? helperPath = null, string? results = null) =>
        new(launcher, helperPath ?? _helperExe, results ?? _results, TimeSpan.FromSeconds(5));

    private static HelperRequest Priority(uint pid = 1234) =>
        new(HelperOperation.ProcessPriority, "placeholder-nonce", @"C:\placeholder.json") { Pid = pid, CreateTime = 133_000_000_000_000_000, Priority = SettablePriority.BelowNormal };

    private static void WriteResult(FakeLauncher.Call call, string op, OperationCode code, string? message = "mgmt.process.priority-set", Dictionary<string, string>? data = null, string? nonce = null) =>
        File.WriteAllText(
            call.ResultPath,
            JsonSerializer.Serialize(new HelperResult(nonce ?? call.Nonce, op, code, message, data), WireJsonContext.Default.HelperResult));

    // ---- QuoteArgument ----

    public static TheoryData<string> Awkward => new()
    {
        "plain", "a b", "", @"C:\x y\", @"C:\x y\z", "say \"hi\"", "back\\\\\"quote", "trailing\\\\", "tab\there",
        "\"", "\\\"", "\\\\\"", "a\\b", "ends with quote\"", "two  spaces", @"C:\Program Files\PC Mieru\",
    };

    [Theory]
    [MemberData(nameof(Awkward))]
    public void A_quoted_argument_is_read_back_as_the_same_text(string argument)
    {
        var quoted = HelperClient.QuoteArgument(argument);
        Assert.Equal([argument], SplitCommandLine(quoted));
        Assert.Equal(["before", argument, "after"], SplitCommandLine("before " + quoted + " after"));
    }

    // ---- fake launcher ----

    [Fact]
    public async Task A_good_run_passes_the_fixed_request_and_returns_the_helpers_own_words()
    {
        Strings.Register(ManagementStrings.All);
        var launcher = new FakeLauncher(call =>
        {
            WriteResult(call, "process-priority", OperationCode.Ok, "mgmt.process.priority-set");
            return new LaunchResult(LaunchStatus.Exited, 0);
        });

        var outcome = await Client(launcher).RunAsync(Priority());

        Assert.Equal(OperationCode.Ok, outcome.Code);
        Assert.False(outcome.UacDeclined);
        Assert.NotNull(outcome.Result);
        var call = Assert.Single(launcher.Calls);
        Assert.Equal(_helperExe, call.Exe);
        Assert.True(call.ResultFileExisted, "the empty file the Helper writes into is made BEFORE the launch");
        Assert.Equal(0, call.ResultFileLength);
        Assert.Equal(32, call.Nonce.Length);
        Assert.All(call.Nonce, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        Assert.Equal(Path.Combine(_results, call.Nonce + ".json"), call.ResultPath);
        Assert.False(File.Exists(call.ResultPath), "the file is removed afterwards");

        // What the Helper receives is exactly what its own strict parser accepts, and carries the caller's values, not the placeholders.
        Assert.True(HelperRequest.TryParse(call.Args, out var parsed, out var error), error);
        Assert.Equal(HelperOperation.ProcessPriority, parsed!.Op);
        Assert.Equal(1234u, parsed.Pid);
        Assert.Equal(133_000_000_000_000_000, parsed.CreateTime);
        Assert.Equal(SettablePriority.BelowNormal, parsed.Priority);
        Assert.Equal(call.Nonce, parsed.Nonce);
        Assert.Equal(call.ResultPath, parsed.ResultPath);

        Assert.Equal(OperationCode.Ok, OperationMapper.FromHelper(outcome, Language.En).Code);
    }

    [Fact]
    public async Task Two_runs_never_share_a_nonce_or_a_result_file()
    {
        var launcher = new FakeLauncher(_ => new LaunchResult(LaunchStatus.Exited, 0));
        var client = Client(launcher);
        await client.RunAsync(Priority());
        await client.RunAsync(Priority());
        Assert.NotEqual(launcher.Calls[0].Nonce, launcher.Calls[1].Nonce);
        Assert.NotEqual(launcher.Calls[0].ResultPath, launcher.Calls[1].ResultPath);
    }

    [Fact]
    public async Task The_exit_code_is_the_truth_even_when_the_file_says_otherwise()
    {
        Strings.Register(ManagementStrings.All);
        var launcher = new FakeLauncher(call =>
        {
            WriteResult(call, "process-priority", OperationCode.Ok);
            return new LaunchResult(LaunchStatus.Exited, (int)OperationCode.Denied);
        });

        var outcome = await Client(launcher).RunAsync(Priority());

        Assert.Equal(OperationCode.Denied, outcome.Code);
        var mapped = OperationMapper.FromHelper(outcome, Language.En);
        Assert.Equal(OperationCode.Denied, mapped.Code);
        Assert.False(string.IsNullOrWhiteSpace(mapped.Message));
        Assert.Null(OperationMapper.ActionFromHelper(outcome));
    }

    [Theory]
    [InlineData("wrong-nonce")]
    [InlineData("wrong-op")]
    public async Task A_file_that_is_not_ours_is_ignored_and_the_exit_code_still_answers(string kind)
    {
        Strings.Register(ManagementStrings.All);
        var launcher = new FakeLauncher(call =>
        {
            WriteResult(
                call,
                kind == "wrong-op" ? "service-start" : "process-priority",
                OperationCode.Ok,
                nonce: kind == "wrong-nonce" ? "someoneelsesnonce000000000000000" : null);
            return new LaunchResult(LaunchStatus.Exited, (int)OperationCode.Ok);
        });

        var outcome = await Client(launcher).RunAsync(Priority());

        Assert.Equal(OperationCode.Ok, outcome.Code);
        Assert.Null(outcome.Result);
        Assert.Equal(OperationCode.Ok, OperationMapper.FromHelper(outcome, Language.En).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"nonce\":1}")]
    public async Task An_empty_or_broken_file_is_no_result(string content)
    {
        var launcher = new FakeLauncher(call =>
        {
            File.WriteAllText(call.ResultPath, content);
            return new LaunchResult(LaunchStatus.Exited, (int)OperationCode.NotFound);
        });

        var outcome = await Client(launcher).RunAsync(Priority());

        Assert.Equal(OperationCode.NotFound, outcome.Code);
        Assert.Null(outcome.Result);
    }

    [Fact]
    public async Task A_huge_file_is_not_read()
    {
        var launcher = new FakeLauncher(call =>
        {
            using (var stream = File.Create(call.ResultPath))
            {
                stream.SetLength(300 * 1024);
            }

            return new LaunchResult(LaunchStatus.Exited, 0);
        });

        Assert.Null((await Client(launcher).RunAsync(Priority())).Result);
    }

    [Fact]
    public async Task The_data_the_helper_writes_comes_back_whole()
    {
        var launcher = new FakeLauncher(call =>
        {
            WriteResult(call, "process-priority", OperationCode.Ok, data: new Dictionary<string, string> { ["prev-priority"] = "32", ["arg.priority"] = "belowNormal" });
            return new LaunchResult(LaunchStatus.Exited, 0);
        });

        var outcome = await Client(launcher).RunAsync(Priority());

        var action = OperationMapper.ActionFromHelper(outcome);
        Assert.NotNull(action);
        Assert.Equal("32", action!.Data!["prev-priority"]);
        Assert.Equal("belowNormal", action.Args!["priority"]);
    }

    [Fact]
    public async Task A_declined_prompt_is_cancelled_and_says_so()
    {
        Strings.Register(ManagementStrings.All);
        var launcher = new FakeLauncher(_ => new LaunchResult(LaunchStatus.Declined, Error: 1223));

        var outcome = await Client(launcher).RunAsync(Priority());

        Assert.Equal(OperationCode.Cancelled, outcome.Code);
        Assert.True(outcome.UacDeclined);
        Assert.Null(outcome.Result);
        Assert.Equal(OperationCode.Cancelled, OperationMapper.FromHelper(outcome, Language.En).Code);
    }

    [Fact]
    public async Task A_helper_that_could_not_start_is_an_error_with_the_win32_code_in_the_words()
    {
        Strings.Register(ManagementStrings.All);
        var launcher = new FakeLauncher(_ => new LaunchResult(LaunchStatus.StartFailed, Error: 193));

        var outcome = await Client(launcher).RunAsync(Priority());

        Assert.Equal(OperationCode.Error, outcome.Code);
        var mapped = OperationMapper.FromHelper(outcome, Language.En);
        Assert.Equal(OperationCode.Error, mapped.Code);
        Assert.Contains("193", mapped.Message);
    }

    [Fact]
    public async Task A_helper_that_does_not_finish_in_time_is_an_error_that_tells_the_user_to_check_the_state()
    {
        Strings.Register(ManagementStrings.All);
        var launcher = new FakeLauncher(_ => new LaunchResult(LaunchStatus.TimedOut));

        var outcome = await Client(launcher).RunAsync(Priority());

        Assert.Equal(OperationCode.Error, outcome.Code);
        Assert.Equal(ManagementStrings.HelperTimeout, outcome.Result!.Message);
        Assert.Equal(Strings.Get(ManagementStrings.HelperTimeout, Language.En), OperationMapper.FromHelper(outcome, Language.En).Message);
    }

    [Fact]
    public async Task Giving_up_while_waiting_is_cancelled_without_the_declined_flag()
    {
        var launcher = new FakeLauncher(_ => new LaunchResult(LaunchStatus.Cancelled));
        var outcome = await Client(launcher).RunAsync(Priority());
        Assert.Equal(OperationCode.Cancelled, outcome.Code);
        Assert.False(outcome.UacDeclined);
    }

    [Fact]
    public async Task A_helper_that_cannot_be_observed_is_an_error()
    {
        var launcher = new FakeLauncher(_ => new LaunchResult(LaunchStatus.Unobserved));
        var outcome = await Client(launcher).RunAsync(Priority());
        Assert.Equal(OperationCode.Error, outcome.Code);
        Assert.False(outcome.UacDeclined);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(63)]
    [InlineData(200)]
    [InlineData(256)]
    [InlineData(-1073741819)]
    public async Task An_exit_code_that_is_not_an_operation_code_is_an_error_and_the_file_is_not_trusted(int exitCode)
    {
        Strings.Register(ManagementStrings.All);
        var launcher = new FakeLauncher(call =>
        {
            WriteResult(call, "process-priority", OperationCode.Ok);
            return new LaunchResult(LaunchStatus.Exited, exitCode);
        });

        var outcome = await Client(launcher).RunAsync(Priority());

        Assert.Equal(OperationCode.Error, outcome.Code);
        Assert.Contains(exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture), OperationMapper.FromHelper(outcome, Language.En).Message);
    }

    [Fact]
    public async Task A_missing_helper_is_unsupported_and_nothing_is_started_or_created()
    {
        Strings.Register(ManagementStrings.All);
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch"));

        var outcome = await Client(launcher, helperPath: Path.Combine(_dir, "nope.exe")).RunAsync(Priority());

        Assert.Equal(OperationCode.Unsupported, outcome.Code);
        Assert.Empty(launcher.Calls);
        Assert.False(Directory.Exists(_results));
        Assert.Equal(OperationCode.Unsupported, OperationMapper.FromHelper(outcome, Language.En).Code);
    }

    [Fact]
    public async Task A_request_the_helper_would_refuse_is_refused_before_any_prompt()
    {
        Strings.Register(ManagementStrings.All);
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch"));
        var client = Client(launcher);

        var cases = new[]
        {
            // The Helper only toggles machine-side startup entries.
            new HelperRequest(HelperOperation.StartupHklmSet, "p", @"C:\p.json") { Provider = StartupProvider.RunHkcu, ValueName = "x", Enable = false },
            // A service name with a path separator.
            new HelperRequest(HelperOperation.ServiceStart, "p", @"C:\p.json") { ServiceName = @"a\b" },
            // A control character.
            new HelperRequest(HelperOperation.StartupHklmSet, "p", @"C:\p.json") { Provider = StartupProvider.RunHklm, ValueName = "a\u0001b", Enable = true },
            // A missing required value.
            new HelperRequest(HelperOperation.ProcessTerminate, "p", @"C:\p.json") { Pid = 5 },
        };

        foreach (var request in cases)
        {
            var outcome = await client.RunAsync(request);
            Assert.Equal(OperationCode.InvalidArguments, outcome.Code);
            Assert.Equal(ManagementStrings.HelperBadRequest, outcome.Result!.Message);
        }

        Assert.Empty(launcher.Calls);
        Assert.False(Directory.Exists(_results), "no result file either: nothing was going to run");
    }

    [Fact]
    public async Task A_call_cancelled_before_the_prompt_starts_nothing_and_leaves_no_file()
    {
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch"));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        var outcome = await Client(launcher).RunAsync(Priority(), source.Token);

        Assert.Equal(OperationCode.Cancelled, outcome.Code);
        Assert.Empty(launcher.Calls);
        Assert.False(Directory.Exists(_results) && Directory.EnumerateFileSystemEntries(_results).Any());
    }

    [Fact]
    public async Task If_the_result_file_cannot_be_made_there_is_no_prompt()
    {
        Strings.Register(ManagementStrings.All);
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "a file where the folder should be");
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch"));

        var outcome = await Client(launcher, results: Path.Combine(blocker, "helper")).RunAsync(Priority());

        Assert.Equal(OperationCode.Error, outcome.Code);
        Assert.Empty(launcher.Calls);
        Assert.False(string.IsNullOrWhiteSpace(OperationMapper.FromHelper(outcome, Language.En).Message));
    }

    [Fact]
    public async Task Old_leftover_result_files_with_our_kind_of_name_are_swept_and_other_files_are_not()
    {
        Directory.CreateDirectory(_results);
        var old = Path.Combine(_results, new string('a', 32) + ".json");
        var recent = Path.Combine(_results, new string('b', 32) + ".json");
        var foreign = Path.Combine(_results, "notes.json");
        foreach (var file in new[] { old, recent, foreign })
        {
            File.WriteAllText(file, "{}");
        }

        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-3));
        File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddHours(-3));

        await Client(new FakeLauncher(_ => new LaunchResult(LaunchStatus.Exited, 0))).RunAsync(Priority());

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void ReadResult_wants_the_nonce_and_the_operation_to_match_exactly()
    {
        var path = Path.Combine(_dir, "r.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new HelperResult("n1", "service-start", OperationCode.Ok, "k"), WireJsonContext.Default.HelperResult));

        Assert.NotNull(HelperClient.ReadResult(path, "n1", "service-start"));
        Assert.Null(HelperClient.ReadResult(path, "N1", "service-start"));
        Assert.Null(HelperClient.ReadResult(path, "n1", "service-stop"));
        Assert.Null(HelperClient.ReadResult(Path.Combine(_dir, "missing.json"), "n1", "service-start"));
    }

    // ---- the real Helper, started as an ordinary child (no UAC) ----

    private static string RealHelperPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PcMieru.slnx")))
            {
                // The configuration this test assembly was built in first: a stale Helper of the other configuration
                // (e.g. an old Debug build) would otherwise be picked and test yesterday's code.
                var ownConfig = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    ? "Release" : "Debug";
                foreach (var config in new[] { ownConfig, ownConfig == "Release" ? "Debug" : "Release" })
                {
                    var exe = Path.Combine(dir.FullName, "src", "PcMieru.Helper", "bin", config, "net10.0-windows", "PCMieru.Helper.exe");
                    if (File.Exists(exe))
                    {
                        return exe;
                    }
                }
            }
        }

        throw new FileNotFoundException("PCMieru.Helper.exe was not found: build PcMieru.slnx (not only the test project) before running this test.");
    }

    [Fact]
    public async Task The_real_helper_changes_the_priority_of_and_then_ends_our_own_ping_and_nothing_else()
    {
        Strings.Register(ManagementStrings.All);
        // verb null: the Helper is "asInvoker", so this starts it as a plain child and no UAC prompt can appear.
        var client = new HelperClient(new SystemElevatedLauncher(verb: null), RealHelperPath(), _results, TimeSpan.FromSeconds(60));
        using var ping = new OwnPing();

        var priority = await client.RunAsync(new HelperRequest(HelperOperation.ProcessPriority, "x", @"C:\x.json")
        {
            Pid = ping.Pid,
            CreateTime = ping.CreateTime,
            Priority = SettablePriority.BelowNormal,
        });

        Assert.Equal(OperationCode.Ok, priority.Code);
        Assert.NotNull(priority.Result);
        Assert.Equal("process-priority", priority.Result!.Op);
        Assert.Equal(System.Diagnostics.ProcessPriorityClass.BelowNormal, ping.Priority);
        Assert.Equal(OperationCode.Ok, OperationMapper.FromHelper(priority, Language.En).Code);

        // The wrong creation time means "a different process now": the Helper must leave it alone.
        var wrong = await client.RunAsync(new HelperRequest(HelperOperation.ProcessTerminate, "x", @"C:\x.json")
        {
            Pid = ping.Pid,
            CreateTime = ping.CreateTime + 10_000_000,
        });
        Assert.NotEqual(OperationCode.Ok, wrong.Code);
        Assert.False(ping.HasExited);

        var right = await client.RunAsync(new HelperRequest(HelperOperation.ProcessTerminate, "x", @"C:\x.json")
        {
            Pid = ping.Pid,
            CreateTime = ping.CreateTime,
        });
        Assert.Equal(OperationCode.Ok, right.Code);
        Assert.True(ping.HasExited);

        Assert.False(Directory.Exists(_results) && Directory.EnumerateFiles(_results).Any(), "every result file is removed");
    }
}
