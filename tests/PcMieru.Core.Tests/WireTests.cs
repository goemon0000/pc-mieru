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
        HelperOperation.DiskHealthAll =>
            new HelperRequest(op, Nonce, ResultPath) { Drives = Drives(3, 0, 1) },
        _ => new HelperRequest(op, Nonce, ResultPath) { DriveNumber = 1 },
    };

    private static DriveList Drives(params int[] numbers)
    {
        Assert.True(DriveList.TryCreate(numbers, out var list));
        return list!;
    }

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
    public void Disk_health_all_goes_on_the_command_line_as_sorted_drive_numbers_and_nothing_else()
    {
        var request = Sample(HelperOperation.DiskHealthAll);

        Assert.Equal(["disk-health-all", "--drives", "0,1,3", "--result", ResultPath, "--nonce", Nonce], request.ToArgs());
        Assert.Equal("disk-health-all", HelperOperations.Get(HelperOperation.DiskHealthAll).Name);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0,1,2,3")]
    [InlineData("255")]
    [InlineData("0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15")]
    public void Good_drive_lists_are_accepted(string drives)
    {
        Assert.True(HelperRequest.TryParse(["disk-health-all", "--drives", drives, "--result", ResultPath, "--nonce", Nonce], out var request, out var error), error);
        Assert.Equal(drives, request!.Drives!.ToWire());
    }

    [Theory]
    [InlineData("")]
    [InlineData(",")]
    [InlineData("0,")]
    [InlineData(",0")]
    [InlineData("0,,1")]
    [InlineData("1,0")]
    [InlineData("1,1")]
    [InlineData("0, 1")]
    [InlineData(" 0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("256")]
    [InlineData("1000")]
    [InlineData("0000")]
    [InlineData("00")]
    [InlineData("01")]
    [InlineData("0,007")]
    [InlineData("1.5")]
    [InlineData("0x1")]
    [InlineData("abc")]
    [InlineData("\u0661")]
    [InlineData(@"..\x")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData("0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16")]
    public void Bad_drive_lists_are_refused(string drives)
    {
        Assert.False(HelperRequest.TryParse(["disk-health-all", "--drives", drives, "--result", ResultPath, "--nonce", Nonce], out _, out _));
        Assert.False(DriveList.TryParse(drives, out _));
    }

    [Fact]
    public void Disk_health_all_needs_its_list_and_takes_no_other_drive_option()
    {
        Assert.False(HelperRequest.TryParse(["disk-health-all", "--result", ResultPath, "--nonce", Nonce], out _, out _));
        Assert.False(HelperRequest.TryParse(["disk-health-all", "--drive", "1", "--result", ResultPath, "--nonce", Nonce], out _, out _));
        Assert.False(HelperRequest.TryParse(["disk-health-all", "--drives", "1", "--drives", "2", "--result", ResultPath, "--nonce", Nonce], out _, out _));
        Assert.False(HelperRequest.TryParse(["smart-read", "--drives", "1", "--result", ResultPath, "--nonce", Nonce], out _, out _));
        Assert.False(HelperRequest.TryParse(["nvme-health", "--drives", "1,2", "--result", ResultPath, "--nonce", Nonce], out _, out _));
    }

    [Fact]
    public void A_drive_list_is_made_sorted_and_refuses_what_the_wire_would()
    {
        Assert.True(DriveList.TryCreate([5, 1, 3], out var list));
        Assert.Equal([1, 3, 5], list!.Numbers);
        Assert.Equal(Drives(1, 3, 5), list);
        Assert.Equal(Drives(1, 3, 5).GetHashCode(), list.GetHashCode());
        Assert.NotEqual(Drives(1, 3), list);

        Assert.False(DriveList.TryCreate([], out _));
        Assert.False(DriveList.TryCreate([1, 1], out _));
        Assert.False(DriveList.TryCreate([-1], out _));
        Assert.False(DriveList.TryCreate([256], out _));
        Assert.False(DriveList.TryCreate(Enumerable.Range(0, DriveList.MaxCount + 1), out _));
        Assert.True(DriveList.TryCreate(Enumerable.Range(0, DriveList.MaxCount), out _));
    }

    [Fact]
    public void The_helper_client_refuses_nothing_it_makes_but_a_list_that_would_not_parse_cannot_be_built()
    {
        // The client re-parses what it is about to send (before any prompt). A DriveList can only be built valid, so
        // whatever the reader puts into a request is a request the Helper accepts.
        var request = new HelperRequest(HelperOperation.DiskHealthAll, Nonce, ResultPath) { Drives = Drives(0, 255) };

        Assert.True(HelperRequest.TryParse(request.ToArgs(), out var parsed, out var error), error);
        Assert.Equal(request, parsed);
    }

    [Fact]
    public void The_biggest_disk_health_answer_fits_the_result_file_limit_with_room_to_spare()
    {
        // 16 drives, each with every key of its biggest answer (NVMe: health page, 16 error entries; SATA: table and thresholds).
        var data = new Dictionary<string, string>();
        for (var drive = 0; drive < DriveList.MaxCount; drive++)
        {
            data[HelperDataKeys.ForDrive(drive, HelperDataKeys.DriveCode)] = "0";
            data[HelperDataKeys.ForDrive(drive, HelperDataKeys.Bus)] = "nvme";
            data[HelperDataKeys.ForDrive(drive, HelperDataKeys.NvmeHealth)] = Convert.ToBase64String(new byte[512]);
            data[HelperDataKeys.ForDrive(drive, HelperDataKeys.NvmeErrors)] = Convert.ToBase64String(new byte[1024]);
            data[HelperDataKeys.ForDrive(drive, HelperDataKeys.SmartData)] = Convert.ToBase64String(new byte[512]);
            data[HelperDataKeys.ForDrive(drive, HelperDataKeys.SmartThresholds)] = Convert.ToBase64String(new byte[512]);
        }

        var json = JsonSerializer.Serialize(
            ActionResult.Of(OperationCode.Ok, MessageKeys.DiskRead).WithData(data).ToHelperResult(Nonce, "disk-health-all"),
            WireJsonContext.Default.HelperResult);

        Assert.True(json.Length < PcMieru.Core.Management.HelperClient.MaxResultBytes / 2, $"{json.Length} bytes");
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
