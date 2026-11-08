using System.Text.Json;
using PcMieru.Wire;

namespace PcMieru.Core.Tests;

public class WireTests
{
    private const string Nonce = "0123456789abcdef0123";
    private static readonly string ResultPath = @"C:\Users\user\AppData\Local\Temp\PCMieru\" + Nonce + ".json";

    private static HelperRequest Sample(HelperOperation op) => op switch
    {
        HelperOperation.ServiceStart or HelperOperation.ServiceStop or HelperOperation.ServiceRestart =>
            new HelperRequest(op, Nonce, ResultPath) { ServiceName = "Spooler" },
        HelperOperation.StartupHklmSet =>
            new HelperRequest(op, Nonce, ResultPath) { Provider = StartupProvider.RunHklm, ValueName = "SecurityHealth", Enable = false },
        HelperOperation.ProcessTerminate =>
            new HelperRequest(op, Nonce, ResultPath) { Pid = 1234, CreateTime = 133_800_000_000_000_000 },
        HelperOperation.ProcessPriority =>
            new HelperRequest(op, Nonce, ResultPath) { Pid = 1234, CreateTime = 133_800_000_000_000_000, Priority = SettablePriority.BelowNormal },
        HelperOperation.ProcessEfficiency =>
            new HelperRequest(op, Nonce, ResultPath) { Pid = 1234, CreateTime = 133_800_000_000_000_000, Enable = true },
        _ => new HelperRequest(op, Nonce, ResultPath) { DriveNumber = 1 },
    };

    public static TheoryData<HelperOperation> Operations() => new(Enum.GetValues<HelperOperation>());

    [Theory]
    [MemberData(nameof(Operations))]
    public void Every_operation_round_trips_through_argv(HelperOperation op)
    {
        var request = Sample(op);
        var args = request.ToArgs();
        Assert.True(HelperRequest.TryParse(args, out var parsed, out var error), error);
        Assert.Equal(request, parsed);
    }

    [Fact]
    public void The_operation_table_covers_the_enum_exactly_once_with_unique_names()
    {
        Assert.Equal(Enum.GetValues<HelperOperation>().Order(), HelperOperations.All.Select(s => s.Op).Order());
        Assert.Equal(HelperOperations.All.Count, HelperOperations.All.Select(s => s.Name).Distinct().Count());
    }

    [Fact]
    public void Unknown_operations_and_options_are_refused()
    {
        Assert.False(HelperRequest.TryParse([], out _, out _));
        Assert.False(HelperRequest.TryParse(["cmd", "--result", ResultPath, "--nonce", Nonce], out _, out _));

        var good = Sample(HelperOperation.ServiceStart).ToArgs().ToList();
        Assert.False(HelperRequest.TryParse([.. good, "--extra", "x"], out _, out _));
        Assert.False(HelperRequest.TryParse([.. good, "--name", "Other"], out _, out _));
        Assert.False(HelperRequest.TryParse(good.Take(good.Count - 2).ToArray(), out _, out _));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("0123456789abcdef!!!!")]
    [InlineData("0123456789abcdef01230123456789abcdef01230123456789abcdef01230123456789")]
    public void Bad_nonces_are_refused(string nonce)
    {
        Assert.False(HelperRequest.IsValidNonce(nonce));
    }

    [Theory]
    [InlineData(@"\\server\share\0123456789abcdef0123.json")]
    [InlineData(@"C:\Temp\..\0123456789abcdef0123.json")]
    [InlineData(@"C:\Temp\other.json")]
    [InlineData(@"C:/Temp/0123456789abcdef0123.json")]
    [InlineData(@"\\?\C:\Temp\0123456789abcdef0123.json")]
    [InlineData(@"Temp\0123456789abcdef0123.json")]
    public void Result_paths_must_be_local_plain_and_named_after_the_nonce(string path)
    {
        Assert.False(HelperRequest.IsValidResultPath(path, Nonce));
    }

    [Theory]
    [InlineData("--pid", "0")]
    [InlineData("--pid", "abc")]
    [InlineData("--created", "0")]
    [InlineData("--created", "-5")]
    public void Process_operations_need_a_real_pid_and_creation_time(string key, string value)
    {
        var args = Sample(HelperOperation.ProcessTerminate).ToArgs().ToList();
        args[args.IndexOf(key) + 1] = value;
        Assert.False(HelperRequest.TryParse(args, out _, out _));
    }

    [Fact]
    public void Service_names_cannot_smuggle_path_separators()
    {
        var args = Sample(HelperOperation.ServiceStop).ToArgs().ToList();
        args[args.IndexOf("--name") + 1] = @"..\evil";
        Assert.False(HelperRequest.TryParse(args, out _, out _));
    }

    [Fact]
    public void Only_hklm_scope_startup_providers_exist_on_the_wire()
    {
        var request = new HelperRequest(HelperOperation.StartupHklmSet, Nonce, ResultPath)
        {
            Provider = StartupProvider.RunHkcu,
            ValueName = "x",
            Enable = true,
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => request.ToArgs());
    }

    [Fact]
    public void Helper_result_is_camel_case_json_with_a_string_code()
    {
        var json = JsonSerializer.Serialize(
            new HelperResult(Nonce, "service-start", OperationCode.Denied, "no"),
            WireJsonContext.Default.HelperResult);
        Assert.Contains("\"code\":\"denied\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("data", json, StringComparison.Ordinal);
    }
}
