using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Win32;
using PcMieru.Wire;

namespace PcMieru.Core.Tests.Management;

public class EfficiencyRequestTests
{
    private const string Nonce = "0123456789abcdef0123";
    private static readonly string ResultPath = @"C:\Users\user\AppData\Local\Temp\PCMieru\" + Nonce + ".json";

    private static HelperRequest Off(uint? priority = 32, uint? control = 1, uint? state = 0) =>
        new(HelperOperation.ProcessEfficiency, Nonce, ResultPath)
        {
            Pid = 1234,
            CreateTime = 133_800_000_000_000_000,
            Enable = false,
            PreviousPriorityClass = priority,
            PreviousThrottleControl = priority is null ? null : control,
            PreviousThrottleState = priority is null ? null : state,
        };

    [Fact]
    public void The_restore_values_round_trip_through_argv()
    {
        var request = Off(0x4000, 1, 0);
        Assert.True(HelperRequest.TryParse(request.ToArgs(), out var parsed, out var error), error);
        Assert.Equal(request, parsed);
    }

    [Fact]
    public void Off_without_restore_values_is_still_valid_and_means_the_system_default()
    {
        var request = Off(priority: null);
        Assert.True(HelperRequest.TryParse(request.ToArgs(), out var parsed, out _));
        Assert.Null(parsed!.PreviousPriorityClass);
    }

    [Theory]
    [InlineData("0x100")]
    [InlineData("256")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("33")]
    [InlineData("-1")]
    public void Realtime_and_unknown_priority_classes_cannot_be_restored(string value)
    {
        var args = Off().ToArgs().ToList();
        args[args.IndexOf("--prev-priority") + 1] = value;
        Assert.False(HelperRequest.TryParse(args, out _, out _));
    }

    [Theory]
    [InlineData("--prev-control", "2")]
    [InlineData("--prev-state", "2")]
    [InlineData("--prev-control", "x")]
    public void Throttle_masks_are_zero_or_one(string option, string value)
    {
        var args = Off().ToArgs().ToList();
        args[args.IndexOf(option) + 1] = value;
        Assert.False(HelperRequest.TryParse(args, out _, out _));
    }

    [Fact]
    public void The_three_restore_values_come_together()
    {
        var args = Off().ToArgs().ToList();
        var at = args.IndexOf("--prev-state");
        args.RemoveRange(at, 2);
        Assert.False(HelperRequest.TryParse(args, out _, out _));
    }

    [Fact]
    public void Restore_values_are_refused_with_state_on()
    {
        var args = Off().ToArgs().ToList();
        args[args.IndexOf("--state") + 1] = "on";
        Assert.False(HelperRequest.TryParse(args, out _, out _));
    }

    [Fact]
    public void Restore_values_are_refused_on_other_operations()
    {
        var args = new HelperRequest(HelperOperation.ProcessTerminate, Nonce, ResultPath) { Pid = 1234, CreateTime = 133_800_000_000_000_000 }
            .ToArgs().ToList();
        args.InsertRange(1, ["--prev-priority", "32", "--prev-control", "1", "--prev-state", "0"]);
        Assert.False(HelperRequest.TryParse(args, out _, out _));
    }
}

public class StartupApprovedTests : IDisposable
{
    // Only ever a throw-away key under HKCU\Software\PcMieruTest, deleted afterwards. Never a real startup entry.
    private readonly string _approvedRoot = TestRegistryRoot.Key + "\\" + Guid.NewGuid().ToString("N");

    public StartupApprovedTests() => TestRegistryRoot.Take();

    public void Dispose() => TestRegistryRoot.Release(_approvedRoot);

    private static RegistryKey Root() => Registry.CurrentUser;

    private string PathFor(string sub) => _approvedRoot + "\\" + sub;

    [Theory]
    [InlineData(2u, ApprovalState.Enabled)]
    [InlineData(6u, ApprovalState.Enabled)]
    [InlineData(3u, ApprovalState.Disabled)]
    [InlineData(7u, ApprovalState.Disabled)]
    [InlineData(0u, ApprovalState.Unknown)]
    [InlineData(4u, ApprovalState.Unknown)]
    [InlineData(0xFFFFFFFFu, ApprovalState.Unknown)]
    public void The_first_dword_decides_the_state(uint flag, ApprovalState expected)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, flag);
        Assert.Equal(expected, StartupApprovedCodec.Decode(RegistryValueKind.Binary, bytes));
    }

    [Fact]
    public void A_missing_value_is_enabled_and_odd_shapes_are_unknown()
    {
        Assert.Equal(ApprovalState.Enabled, StartupApprovedCodec.Decode(RegistryValueKind.None, null));
        Assert.Equal(ApprovalState.Unknown, StartupApprovedCodec.Decode(RegistryValueKind.Binary, new byte[8]));
        Assert.Equal(ApprovalState.Unknown, StartupApprovedCodec.Decode(RegistryValueKind.Binary, new byte[13]));
        Assert.Equal(ApprovalState.Unknown, StartupApprovedCodec.Decode(RegistryValueKind.DWord, new byte[12]));
    }

    [Fact]
    public void Encoding_matches_what_task_manager_writes()
    {
        var disabled = StartupApprovedCodec.EncodeDisabled(133_800_000_000_000_000);
        Assert.Equal(12, disabled.Length);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(disabled));
        Assert.Equal(133_800_000_000_000_000, StartupApprovedCodec.DisabledAt(disabled));
        var enabled = StartupApprovedCodec.EncodeEnabled();
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(enabled));
        Assert.All(enabled.Skip(4), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(StartupProvider.RunHkcu, "Run")]
    [InlineData(StartupProvider.RunHklm, "Run")]
    [InlineData(StartupProvider.RunHklm32, "Run32")]
    [InlineData(StartupProvider.StartupFolderUser, "StartupFolder")]
    [InlineData(StartupProvider.StartupFolderCommon, "StartupFolder")]
    public void Each_registration_place_has_its_approval_sub_key(StartupProvider provider, string expected)
    {
        Assert.Equal(expected, StartupApprovedCodec.ApprovedSubKey(provider));
    }

    [Fact]
    public void Places_we_only_list_have_no_approval_sub_key_and_are_never_written()
    {
        foreach (var provider in Enum.GetValues<StartupProvider>().Where(p => StartupApprovedCodec.ApprovedSubKey(p) is null))
        {
            var result = StartupApprovedToggle.Apply(Root(), _approvedRoot, provider, "Demo", enable: false, nowFileTime: 1, out var original);
            Assert.Equal(OperationCode.Unsupported, result.Code);
            Assert.Null(original);
        }

        using var created = Root().OpenSubKey(_approvedRoot);
        Assert.Null(created);
    }

    [Fact]
    public void Disabling_an_entry_without_an_approval_value_writes_three_plus_now_and_remembers_it_did_not_exist()
    {
        var result = StartupApprovedToggle.Apply(Root(), _approvedRoot, StartupProvider.RunHklm, "Demo", enable: false, nowFileTime: 777, out var original);

        Assert.Equal(OperationCode.Ok, result.Code);
        Assert.Equal(MessageKeys.StartupDisabled, result.MessageKey);
        Assert.False(original!.Existed);
        var state = StartupApprovedToggle.Read(Root(), PathFor("Run"), "Demo", out var kind, out var bytes);
        Assert.Equal(ApprovalState.Disabled, state);
        Assert.Equal(RegistryValueKind.Binary, kind);
        Assert.Equal(777, StartupApprovedCodec.DisabledAt(bytes!));
        Assert.Equal("false", result.Data![StartupOriginal.KeyExisted]);
    }

    [Fact]
    public void Undo_of_a_value_that_did_not_exist_deletes_it()
    {
        StartupApprovedToggle.Apply(Root(), _approvedRoot, StartupProvider.RunHklm, "Demo", false, 5, out var original);

        var restored = StartupApprovedToggle.Restore(Root(), _approvedRoot, StartupProvider.RunHklm, "Demo", original!);

        Assert.Equal(OperationCode.Ok, restored.Code);
        using var key = Root().OpenSubKey(PathFor("Run"));
        Assert.Null(key!.GetValue("Demo"));
    }

    [Fact]
    public void An_existing_enabled_value_is_restored_byte_for_byte()
    {
        var originalBytes = new byte[] { 6, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        using (var key = Root().CreateSubKey(PathFor("Run32")))
        {
            key.SetValue("Demo", originalBytes, RegistryValueKind.Binary);
        }

        var result = StartupApprovedToggle.Apply(Root(), _approvedRoot, StartupProvider.RunHklm32, "Demo", enable: false, nowFileTime: 99, out var original);
        Assert.Equal(OperationCode.Ok, result.Code);
        Assert.True(original!.Existed);
        Assert.Equal(originalBytes, original.Bytes);
        Assert.Equal(ApprovalState.Disabled, StartupApprovedToggle.Read(Root(), PathFor("Run32"), "Demo", out _, out _));

        StartupApprovedToggle.Restore(Root(), _approvedRoot, StartupProvider.RunHklm32, "Demo", original);
        StartupApprovedToggle.Read(Root(), PathFor("Run32"), "Demo", out var kind, out var bytes);
        Assert.Equal(RegistryValueKind.Binary, kind);
        Assert.Equal(originalBytes, bytes);
    }

    [Fact]
    public void The_undo_record_survives_the_data_dictionary()
    {
        var original = new StartupOriginal(true, RegistryValueKind.Binary, [6, 0, 0, 0, 9, 9, 9, 9, 9, 9, 9, 9]);
        var back = StartupOriginal.FromData(original.ToData());
        Assert.Equal(original.Existed, back!.Existed);
        Assert.Equal(original.Kind, back.Kind);
        Assert.Equal(original.Bytes, back.Bytes);
        Assert.Null(StartupOriginal.FromData(null));
        Assert.Null(StartupOriginal.FromData(new Dictionary<string, string> { [StartupOriginal.KeyExisted] = "true" }));
    }

    [Fact]
    public void A_value_of_an_unknown_shape_is_left_alone()
    {
        using (var key = Root().CreateSubKey(PathFor("Run")))
        {
            key.SetValue("Odd", 5, RegistryValueKind.DWord);
        }

        var result = StartupApprovedToggle.Apply(Root(), _approvedRoot, StartupProvider.RunHklm, "Odd", enable: false, nowFileTime: 1, out var original);

        Assert.Equal(OperationCode.Unsupported, result.Code);
        Assert.Equal(MessageKeys.StartupUnknownState, result.MessageKey);
        Assert.Null(original);
        using var after = Root().OpenSubKey(PathFor("Run"));
        Assert.Equal(5, after!.GetValue("Odd"));
        Assert.Equal(RegistryValueKind.DWord, after.GetValueKind("Odd"));
    }

    [Fact]
    public void Disabling_twice_writes_nothing_the_second_time()
    {
        StartupApprovedToggle.Apply(Root(), _approvedRoot, StartupProvider.RunHklm, "Demo", false, 111, out _);
        var second = StartupApprovedToggle.Apply(Root(), _approvedRoot, StartupProvider.RunHklm, "Demo", false, 222, out var original);

        Assert.Equal(MessageKeys.StartupAlreadyDisabled, second.MessageKey);
        Assert.Null(original);
        StartupApprovedToggle.Read(Root(), PathFor("Run"), "Demo", out _, out var bytes);
        Assert.Equal(111, StartupApprovedCodec.DisabledAt(bytes!));
    }

    [Fact]
    public void Enabling_a_disabled_entry_writes_two()
    {
        StartupApprovedToggle.Apply(Root(), _approvedRoot, StartupProvider.StartupFolderCommon, "Demo.lnk", false, 111, out _);
        var result = StartupApprovedToggle.Apply(Root(), _approvedRoot, StartupProvider.StartupFolderCommon, "Demo.lnk", true, 222, out _);

        Assert.Equal(MessageKeys.StartupEnabled, result.MessageKey);
        StartupApprovedToggle.Read(Root(), PathFor("StartupFolder"), "Demo.lnk", out _, out var bytes);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes!));
    }

    [Fact]
    public void Enabling_an_entry_that_was_never_disabled_does_nothing()
    {
        var result = StartupApprovedToggle.Apply(Root(), _approvedRoot, StartupProvider.RunHklm, "Fresh", true, 5, out _);
        Assert.Equal(MessageKeys.StartupAlreadyEnabled, result.MessageKey);
        using var key = Root().OpenSubKey(_approvedRoot);
        Assert.Null(key);
    }

    private HelperEnvironment Environment(Func<StartupProvider, string, bool> exists) => new()
    {
        Self = new SelfIdentity(1, null),
        SystemDirectory = System.Environment.SystemDirectory,
        Services = new FakeServiceApi(new FakeClock()),
        Clock = new FakeClock(),
        OpenStartupRoot = Root,
        StartupApprovedRoot = _approvedRoot,
        EntryExists = exists,
        NowFileTime = () => 4242,
    };

    private static HelperRequest Startup(StartupProvider provider, string name, bool enable) =>
        new(HelperOperation.StartupHklmSet, "0123456789abcdef0123", @"C:\x\0123456789abcdef0123.json")
        {
            Provider = provider,
            ValueName = name,
            Enable = enable,
        };

    [Fact]
    public void The_executor_refuses_an_entry_that_does_not_exist_and_creates_nothing()
    {
        var result = HelperExecutor.Execute(Startup(StartupProvider.RunHklm, "Ghost", false), Environment((_, _) => false));

        Assert.Equal(OperationCode.NotFound, result.Code);
        Assert.Equal(MessageKeys.StartupNotFound, result.MessageKey);
        using var key = Root().OpenSubKey(_approvedRoot);
        Assert.Null(key);
    }

    [Theory]
    [InlineData(StartupProvider.RunHkcu)]
    [InlineData(StartupProvider.StartupFolderUser)]
    public void The_executor_only_toggles_machine_wide_places(StartupProvider provider)
    {
        var result = HelperExecutor.Execute(Startup(provider, "Demo", false), Environment((_, _) => true));
        Assert.Equal(OperationCode.Unsupported, result.Code);
        using var key = Root().OpenSubKey(_approvedRoot);
        Assert.Null(key);
    }

    [Fact]
    public void The_executor_toggles_an_existing_machine_wide_entry_in_the_test_root_only()
    {
        var result = HelperExecutor.Execute(Startup(StartupProvider.RunHklm, "Demo", false), Environment((_, _) => true));

        Assert.Equal(OperationCode.Ok, result.Code);
        Assert.Equal(ApprovalState.Disabled, StartupApprovedToggle.Read(Root(), PathFor("Run"), "Demo", out _, out var bytes));
        Assert.Equal(4242, StartupApprovedCodec.DisabledAt(bytes!));
    }
}

public class StorageProtocolTests
{
    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x03)]
    [InlineData((byte)0x04)]
    [InlineData((byte)0x80)]
    [InlineData((byte)0xC0)]
    [InlineData((byte)0xFF)]
    public void Only_the_error_and_health_log_pages_can_be_asked_for(byte page)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StorageProtocol.BuildNvmeLogQuery(page, 512));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4097)]
    public void The_log_length_is_bounded(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StorageProtocol.BuildNvmeLogQuery(StorageProtocol.NvmeLogHealthInfo, length));
    }

    [Fact]
    public void The_health_query_has_the_documented_layout()
    {
        var query = StorageProtocol.BuildNvmeLogQuery(StorageProtocol.NvmeLogHealthInfo, 512);

        Assert.Equal(8 + 40 + 512, query.Length);
        Assert.Equal(50, BinaryPrimitives.ReadInt32LittleEndian(query));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(query.AsSpan(8)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(query.AsSpan(12)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(query.AsSpan(16)));
        Assert.Equal(40u, BinaryPrimitives.ReadUInt32LittleEndian(query.AsSpan(24)));
        Assert.Equal(512u, BinaryPrimitives.ReadUInt32LittleEndian(query.AsSpan(28)));
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0xD2)]
    [InlineData((byte)0xD4)]
    [InlineData((byte)0xD6)]
    [InlineData((byte)0xD8)]
    [InlineData((byte)0xD9)]
    [InlineData((byte)0xDA)]
    [InlineData((byte)0xDB)]
    [InlineData((byte)0xB0)]
    public void Only_smart_read_data_and_thresholds_can_be_built_never_a_write_a_self_test_or_enable_disable(byte feature)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StorageProtocol.BuildSmartRead(feature, 0));
    }

    [Theory]
    [InlineData((byte)0xD0)]
    [InlineData((byte)0xD1)]
    public void The_two_smart_reads_ask_for_one_sector(byte feature)
    {
        var input = StorageProtocol.BuildSmartRead(feature, 2);
        Assert.Equal(feature, input[4]);
        Assert.Equal(512u, BinaryPrimitives.ReadUInt32LittleEndian(input));
        Assert.Equal(1, input[5]);
        Assert.Equal(0xB0, input[10]);
        Assert.Equal(2, input[12]);
    }

    private static byte[] Descriptor(int offset, int length, byte fill, int total = 48 + 512)
    {
        var output = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8 + 16), (uint)offset);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8 + 20), (uint)length);
        output.AsSpan(8 + offset).Fill(fill);
        return output;
    }

    [Fact]
    public void A_well_formed_descriptor_gives_the_log_bytes()
    {
        var data = StorageProtocol.ExtractProtocolData(Descriptor(40, 512, 0xAB, total: 8 + 40 + 512), 512);
        Assert.NotNull(data);
        Assert.Equal(512, data!.Length);
        Assert.All(data, b => Assert.Equal(0xAB, b));
    }

    [Fact]
    public void A_descriptor_of_the_wrong_shape_gives_null_instead_of_other_bytes()
    {
        Assert.Null(StorageProtocol.ExtractProtocolData(new byte[20], 512));
        Assert.Null(StorageProtocol.ExtractProtocolData(new byte[600], 512)); // version/size zero
        Assert.Null(StorageProtocol.ExtractProtocolData(Descriptor(40, 100, 1, total: 8 + 40 + 512), 512)); // shorter than asked
        Assert.Null(StorageProtocol.ExtractProtocolData(Descriptor(8, 512, 1, total: 8 + 40 + 512), 512)); // data inside the header
        Assert.Null(StorageProtocol.ExtractProtocolData(Descriptor(40, 512, 1, total: 8 + 40 + 100), 512)); // cut off
    }

    [Fact]
    public void Smart_extraction_reads_the_sector_after_the_sixteen_byte_header()
    {
        var output = new byte[StorageProtocol.SendCmdOutSize];
        output.AsSpan(16).Fill(0x5A);
        var data = StorageProtocol.ExtractSmart(output);
        Assert.Equal(512, data!.Length);
        Assert.Equal(0x5A, data[0]);
    }

    [Fact]
    public void A_driver_error_or_an_all_zero_table_is_not_data()
    {
        var withError = new byte[StorageProtocol.SendCmdOutSize];
        withError.AsSpan(16).Fill(1);
        withError[4] = 1;
        Assert.Null(StorageProtocol.ExtractSmart(withError));
        Assert.Null(StorageProtocol.ExtractSmart(new byte[StorageProtocol.SendCmdOutSize]));
        Assert.Null(StorageProtocol.ExtractSmart(new byte[100]));
    }

    [Fact]
    public void Predict_failure_reads_the_flag_and_the_vendor_block()
    {
        var output = new byte[516];
        output[0] = 1;
        output.AsSpan(4).Fill(7);
        var (predict, data) = StorageProtocol.ExtractPredictFailure(output);
        Assert.True(predict);
        Assert.Equal(512, data!.Length);
        Assert.False(StorageProtocol.ExtractPredictFailure(new byte[516]).Predict);
        Assert.Null(StorageProtocol.ExtractPredictFailure(new byte[10]).Data);
    }

    [Theory]
    [InlineData(3, "ata")]
    [InlineData(11, "ata")]
    [InlineData(17, "nvme")]
    [InlineData(7, "usb")]
    [InlineData(12345, "bus-12345")]
    public void Bus_names(int bus, string name) => Assert.Equal(name, StorageProtocol.BusName(bus));

    [Fact]
    public void The_bus_type_is_read_from_the_device_descriptor_offset()
    {
        var descriptor = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(descriptor.AsSpan(28), 17);
        Assert.Equal(17, StorageProtocol.ParseBusType(descriptor));
        Assert.Equal(-1, StorageProtocol.ParseBusType(new byte[20]));
    }
}

public class ActionResultTests
{
    [Fact]
    public void Args_and_data_round_trip_through_the_helper_result_file_shape()
    {
        var result = ActionResult.Of(OperationCode.PartialSuccess, MessageKeys.ServiceRestartStoppedOnly, ("reason", "mgmt.x"), ("name", "Spooler"))
            .WithData(new Dictionary<string, string> { ["prev-priority"] = "32" });

        var wire = result.ToHelperResult("0123456789abcdef0123", "service-restart");
        var json = JsonSerializer.Serialize(wire, WireJsonContext.Default.HelperResult);
        var back = ActionResult.FromHelperResult(JsonSerializer.Deserialize(json, WireJsonContext.Default.HelperResult)!);

        Assert.Equal(OperationCode.PartialSuccess, back.Code);
        Assert.Equal(MessageKeys.ServiceRestartStoppedOnly, back.MessageKey);
        Assert.Equal("mgmt.x", back.Args!["reason"]);
        Assert.Equal("Spooler", back.Args["name"]);
        Assert.Equal("32", back.Data!["prev-priority"]);
        Assert.False(back.Data.ContainsKey("arg.reason"));
    }

    [Fact]
    public void A_result_without_args_or_data_has_no_data_dictionary()
    {
        var wire = ActionResult.Of(OperationCode.Ok, MessageKeys.ProcessTerminated).ToHelperResult("0123456789abcdef0123", "process-terminate");
        Assert.Null(wire.Data);
        var back = ActionResult.FromHelperResult(wire);
        Assert.Null(back.Args);
        Assert.Null(back.Data);
    }

    [Fact]
    public void Every_message_key_is_unique_and_namespaced()
    {
        var keys = typeof(MessageKeys).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(keys);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, k => Assert.StartsWith("mgmt.", k));
    }
}
