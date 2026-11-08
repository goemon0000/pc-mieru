using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using PcMieru.Core.Management;
using PcMieru.Wire;

namespace PcMieru.Core.Tests.Management;

/// <summary>
/// A throw-away stand-in for the machine: Run keys and StartupApproved stores live under
/// <c>HKCU\Software\PcMieruTest\&lt;guid&gt;</c>, the Startup folders in a temp directory. Even the "machine side"
/// providers point there, so no test can reach a real startup entry. Everything is deleted on dispose.
/// </summary>
internal sealed class TempStartupWorld : IDisposable
{
    public const string TestRoot = TestRegistryRoot.Key;
    public const long Now = 133_500_000_000_000_000L;

    private readonly string _id = Guid.NewGuid().ToString("N");

    public TempStartupWorld()
    {
        TestRegistryRoot.Take();
        Directory =Path.Combine(Path.GetTempPath(), "pcmieru-startup-" + _id);
        UserFolder = Path.Combine(Directory, "user-startup");
        CommonFolder = Path.Combine(Directory, "common-startup");
        System.IO.Directory.CreateDirectory(UserFolder);
        System.IO.Directory.CreateDirectory(CommonFolder);
        UndoFile = Path.Combine(Directory, "startup-undo.json");
        Locations = new StartupLocations
        {
            OpenRunRoot = _ => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
            RunPath = provider => KeyBase + "\\run-" + provider.ToString().ToLowerInvariant(),
            OpenApprovedRoot = _ => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
            ApprovedRoot = provider => KeyBase + (StartupManager.IsMachineSide(provider) ? "\\approved-machine" : "\\approved-user"),
            UserFolder = UserFolder,
            CommonFolder = CommonFolder,
        };
    }

    public string Directory { get; }

    public string UserFolder { get; }

    public string CommonFolder { get; }

    public string UndoFile { get; }

    public StartupLocations Locations { get; }

    public string KeyBase => TestRoot + "\\" + _id;

    public TestSource Source => new(Locations);

    public StartupManager Manager(bool elevated, IHelperClient? helper = null, TestSource? source = null) =>
        new(Locations, helper ?? new RecordingHelper(), () => elevated, new StartupUndoStore(UndoFile), () => Now, source ?? Source);

    public void AddRun(StartupProvider provider, string name, string value, RegistryValueKind kind = RegistryValueKind.String)
    {
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = hkcu.CreateSubKey(Locations.RunPath(provider), writable: true);
        key.SetValue(name, value, kind);
    }

    public void AddRunRaw(StartupProvider provider, string name, object value, RegistryValueKind kind)
    {
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = hkcu.CreateSubKey(Locations.RunPath(provider), writable: true);
        key.SetValue(name, value, kind);
    }

    public string ApprovedPath(StartupProvider provider) => Locations.ApprovedRoot(provider) + "\\" + StartupApprovedCodec.ApprovedSubKey(provider);

    public void SetApproval(StartupProvider provider, string name, byte[] bytes, RegistryValueKind kind = RegistryValueKind.Binary)
    {
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = hkcu.CreateSubKey(ApprovedPath(provider), writable: true);
        key.SetValue(name, bytes, kind);
    }

    public byte[]? GetApproval(StartupProvider provider, string name)
    {
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = hkcu.OpenSubKey(ApprovedPath(provider));
        return key?.GetValue(name) as byte[];
    }

    public string? GetRun(StartupProvider provider, string name)
    {
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = hkcu.OpenSubKey(Locations.RunPath(provider));
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public static byte[] Approval(uint flag)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, flag);
        return bytes;
    }

    public void Dispose()
    {
        TestRegistryRoot.Release(KeyBase);

        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}

/// <summary>The real registry and folder readers on the temp world; scheduled tasks and packaged apps are whatever the test says.</summary>
internal sealed class TestSource(StartupLocations locations) : IStartupSource
{
    private readonly SystemStartupSource _real = new(locations);

    public List<RawStartupEntry> Scheduled { get; } = [];

    public List<RawStartupEntry> Packaged { get; } = [];

    public IReadOnlyList<RawStartupEntry> Read(StartupProvider provider, CancellationToken ct) => provider switch
    {
        StartupProvider.ScheduledTask => Scheduled,
        StartupProvider.PackagedApp => Packaged,
        _ => _real.Read(provider, ct),
    };
}

public class StartupIdTests
{
    [Theory]
    [InlineData("runHkcu:Steam", StartupProvider.RunHkcu, "Steam")]
    [InlineData("RUNHKLM:Some Thing", StartupProvider.RunHklm, "Some Thing")]
    [InlineData("runHklm32:a", StartupProvider.RunHklm32, "a")]
    [InlineData("startupFolderUser:x.lnk", StartupProvider.StartupFolderUser, "x.lnk")]
    [InlineData("startupFolderCommon:x.lnk", StartupProvider.StartupFolderCommon, "x.lnk")]
    [InlineData("scheduledTask:\\Folder\\Task", StartupProvider.ScheduledTask, "\\Folder\\Task")]
    [InlineData("runHkcu:a:b:c", StartupProvider.RunHkcu, "a:b:c")]
    public void An_id_is_a_provider_then_the_name_split_at_the_first_colon(string id, StartupProvider provider, string name)
    {
        Assert.True(StartupManager.TryParseId(id, out var p, out var n));
        Assert.Equal(provider, p);
        Assert.Equal(name, n);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("runHkcu")]
    [InlineData("runHkcu:")]
    [InlineData(":name")]
    [InlineData("nowhere:name")]
    [InlineData("C:\\Windows")]
    public void Anything_else_is_not_an_id(string? id) => Assert.False(StartupManager.TryParseId(id, out _, out _));

    [Fact]
    public void An_id_made_from_a_provider_and_a_name_parses_back()
    {
        foreach (var provider in Enum.GetValues<StartupProvider>())
        {
            var id = StartupManager.IdOf(provider, "x:y");
            Assert.True(StartupManager.TryParseId(id, out var p, out var n), id);
            Assert.Equal((provider, "x:y"), (p, n));
        }
    }

    [Fact]
    public void Only_the_three_machine_places_need_the_helper_and_only_the_two_run_and_folder_kinds_toggle()
    {
        Assert.Equal(
            [StartupProvider.RunHklm, StartupProvider.RunHklm32, StartupProvider.StartupFolderCommon],
            Enum.GetValues<StartupProvider>().Where(StartupManager.IsMachineSide));
        Assert.Equal(
            [StartupProvider.RunHkcu, StartupProvider.RunHklm, StartupProvider.RunHklm32, StartupProvider.StartupFolderUser, StartupProvider.StartupFolderCommon],
            Enum.GetValues<StartupProvider>().Where(StartupManager.IsToggleable));
    }
}

public class StartupUndoStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pcmieru-undo-" + Guid.NewGuid().ToString("N"));

    private string File_ => Path.Combine(_dir, "undo.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void The_first_record_wins_names_are_case_blind_and_a_new_store_reads_the_file()
    {
        var store = new StartupUndoStore(File_);
        var first = new StartupUndoRecord("runHkcu", "Steam", "true", "Binary", "AgAAAAAAAAAAAAAA");
        store.SetIfAbsent("runHkcu:Steam", first);
        store.SetIfAbsent("RUNHKCU:STEAM", new StartupUndoRecord("runHkcu", "Steam", "false", "None", ""));
        Assert.Equal(first, store.Get("runHkcu:steam"));
        Assert.True(store.Contains("runhkcu:STEAM"));

        var again = new StartupUndoStore(File_);
        Assert.Equal(first, again.Get("runHkcu:Steam"));
        again.Remove("runHkcu:Steam");
        Assert.False(new StartupUndoStore(File_).Contains("runHkcu:Steam"));
    }

    [Fact]
    public void A_damaged_file_means_nothing_to_undo_and_a_blocked_folder_costs_only_the_file()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "{{{");
        var store = new StartupUndoStore(File_);
        Assert.False(store.Contains("runHkcu:x"));
        store.SetIfAbsent("runHkcu:x", new StartupUndoRecord("runHkcu", "x", "false", "None", ""));
        Assert.True(new StartupUndoStore(File_).Contains("runHkcu:x"));

        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        var blocked = new StartupUndoStore(Path.Combine(blocker, "undo.json"));
        blocked.SetIfAbsent("runHkcu:y", new StartupUndoRecord("runHkcu", "y", "false", "None", ""));
        Assert.True(blocked.Contains("runHkcu:y"));
    }
}

public class StartupManagerListTests
{
    [Fact]
    public async Task Run_values_are_listed_with_their_text_unexpanded_and_other_kinds_are_not_entries()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        world.AddRun(StartupProvider.RunHkcu, "Plain", @"C:\Tools\a.exe /x");
        world.AddRun(StartupProvider.RunHkcu, "Expand", @"%SystemRoot%\notepad.exe", RegistryValueKind.ExpandString);
        world.AddRunRaw(StartupProvider.RunHkcu, "Number", 5, RegistryValueKind.DWord);
        world.AddRun(StartupProvider.RunHkcu, "", "default value is not an entry");

        var list = await world.Manager(elevated: false).ListAsync();

        Assert.Equal(["Expand", "Plain"], list.Select(i => i.Name));
        Assert.Equal(@"%SystemRoot%\notepad.exe", list[0].Command);
        Assert.Equal("runHkcu:Plain", list[1].Id);
        Assert.All(list, i => Assert.Equal(StartupProvider.RunHkcu, i.Provider));
    }

    [Fact]
    public async Task The_approval_decides_the_state_and_an_unknown_one_cannot_be_toggled()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        foreach (var name in new[] { "NoApproval", "Two", "Six", "Three", "Seven", "Weird", "Short" })
        {
            world.AddRun(StartupProvider.RunHkcu, name, "x.exe");
        }

        world.SetApproval(StartupProvider.RunHkcu, "Two", TempStartupWorld.Approval(2));
        world.SetApproval(StartupProvider.RunHkcu, "Six", TempStartupWorld.Approval(6));
        world.SetApproval(StartupProvider.RunHkcu, "Three", TempStartupWorld.Approval(3));
        world.SetApproval(StartupProvider.RunHkcu, "Seven", TempStartupWorld.Approval(7));
        world.SetApproval(StartupProvider.RunHkcu, "Weird", TempStartupWorld.Approval(9));
        world.SetApproval(StartupProvider.RunHkcu, "Short", [3, 0, 0, 0]);

        var items = (await world.Manager(elevated: false).ListAsync()).ToDictionary(i => i.Name);

        Assert.Equal(StartupState.Enabled, items["NoApproval"].State);
        Assert.Equal(StartupState.Enabled, items["Two"].State);
        Assert.Equal(StartupState.Enabled, items["Six"].State);
        Assert.Equal(StartupState.Disabled, items["Three"].State);
        Assert.Equal(StartupState.Disabled, items["Seven"].State);
        foreach (var name in new[] { "Weird", "Short" })
        {
            Assert.Equal(StartupState.Unknown, items[name].State);
            Assert.False(items[name].CanToggle);
            Assert.False(string.IsNullOrEmpty(items[name].Detail));
        }

        Assert.True(items["Two"].CanToggle);
        Assert.False(items["Two"].NeedsHelper);
    }

    [Fact]
    public async Task Machine_side_entries_need_the_helper_unless_we_are_already_elevated()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        world.AddRun(StartupProvider.RunHkcu, "Mine", "a.exe");
        world.AddRun(StartupProvider.RunHklm, "Machine", "b.exe");
        world.AddRun(StartupProvider.RunHklm32, "Machine32", "c.exe");
        File.WriteAllText(Path.Combine(world.CommonFolder, "common.txt"), "x");
        File.WriteAllText(Path.Combine(world.UserFolder, "user.txt"), "x");

        var plain = (await world.Manager(elevated: false).ListAsync()).ToDictionary(i => i.Name);
        Assert.False(plain["Mine"].NeedsHelper);
        Assert.False(plain["user.txt"].NeedsHelper);
        Assert.True(plain["Machine"].NeedsHelper);
        Assert.True(plain["Machine32"].NeedsHelper);
        Assert.True(plain["common.txt"].NeedsHelper);

        var admin = (await world.Manager(elevated: true).ListAsync()).ToDictionary(i => i.Name);
        Assert.All(admin.Values, i => Assert.False(i.NeedsHelper));
        Assert.All(admin.Values, i => Assert.True(i.CanToggle));
    }

    [Fact]
    public async Task Providers_come_in_a_fixed_order_and_names_sort_within_each()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        world.AddRun(StartupProvider.RunHklm32, "z32", "x");
        world.AddRun(StartupProvider.RunHkcu, "b", "x");
        world.AddRun(StartupProvider.RunHkcu, "A", "x");
        world.AddRun(StartupProvider.RunHklm, "m", "x");
        File.WriteAllText(Path.Combine(world.UserFolder, "f.txt"), "x");

        var list = await world.Manager(elevated: false).ListAsync();

        Assert.Equal(["runHkcu:A", "runHkcu:b", "runHklm:m", "runHklm32:z32", "startupFolderUser:f.txt"], list.Select(i => i.Id));
    }

    [Fact]
    public async Task Folder_entries_skip_desktop_ini_and_a_shortcut_shows_its_target_and_arguments()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        File.WriteAllText(Path.Combine(world.UserFolder, "desktop.ini"), "[.ShellClassInfo]");
        File.WriteAllText(Path.Combine(world.UserFolder, "plain.bat"), "echo");

        // A real shortcut, made through the same late-bound COM the reader uses. Only the .lnk file is made; nothing is run.
        var link = Path.Combine(world.UserFolder, "tool.lnk");
        var target = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var type = Type.GetTypeFromProgID("WScript.Shell");
        Assert.NotNull(type);
        dynamic shell = Activator.CreateInstance(type!)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = target;
            shortcut.Arguments = "-test";
            shortcut.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }

        var items = (await world.Manager(elevated: false).ListAsync()).ToDictionary(i => i.Name);

        Assert.Equal(["plain.bat", "tool.lnk"], items.Keys.Order());
        Assert.Equal(Path.Combine(world.UserFolder, "plain.bat"), items["plain.bat"].Command);
        Assert.Equal(target + " -test", items["tool.lnk"].Command, ignoreCase: true);
    }

    [Fact]
    public async Task Scheduled_tasks_and_packaged_apps_are_listed_and_never_toggled()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        var source = world.Source;
        source.Scheduled.Add(new RawStartupEntry(StartupProvider.ScheduledTask, "Backup", "backup.exe /s", @"\Mine\Backup", StartupState.Disabled));
        source.Packaged.Add(new RawStartupEntry(StartupProvider.PackagedApp, "StartupId", "Family_abc", "Family_abc/StartupId", StartupState.Enabled));
        var manager = world.Manager(elevated: true, source: source);

        var items = await manager.ListAsync();

        Assert.Equal([@"scheduledTask:\Mine\Backup", "packagedApp:Family_abc/StartupId"], items.Select(i => i.Id));
        Assert.All(items, i =>
        {
            Assert.False(i.CanToggle);
            Assert.False(i.NeedsHelper);
        });
        Assert.Equal([StartupState.Disabled, StartupState.Enabled], items.Select(i => i.State));

        foreach (var item in items)
        {
            var result = await manager.SetEnabledAsync(item.Id, item.State != StartupState.Enabled);
            Assert.Equal(OperationCode.Unsupported, result.Code);
        }
    }

    [Fact]
    public void Packaged_app_tasks_are_read_from_the_registry_layout_Task_Manager_uses()
    {
        using var world = new TempStartupWorld();
        var basePath = world.KeyBase + "\\packaged";
        using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
        {
            void Task(string family, string task, object? state, RegistryValueKind kind = RegistryValueKind.DWord)
            {
                using var key = hkcu.CreateSubKey(basePath + "\\" + family + "\\" + task, writable: true);
                if (state is not null)
                {
                    key.SetValue("State", state, kind);
                }
            }

            Task("Fam_1", "TaskA", 2);
            Task("Fam_1", "TaskB", 1);
            Task("Fam_2", "Odd", 7);
            Task("Fam_2", "NoState", null);
            Task("Fam_2", "TextState", "2", RegistryValueKind.String);
            Task("Fam_2", "HAM", 2);
            Task("Fam_2", "Schemas", 2);

            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            var entries = StartupReaders.ReadPackaged(root, basePath).ToDictionary(e => e.IdName!);

            Assert.Equal(["Fam_1/TaskA", "Fam_1/TaskB", "Fam_2/Odd"], entries.Keys.Order());
            Assert.Equal(StartupState.Enabled, entries["Fam_1/TaskA"].ListedState);
            Assert.Equal(StartupState.Disabled, entries["Fam_1/TaskB"].ListedState);
            Assert.Equal(StartupState.Unknown, entries["Fam_2/Odd"].ListedState);
            Assert.Equal("Fam_1", entries["Fam_1/TaskA"].Command);
            Assert.Empty(StartupReaders.ReadPackaged(root, basePath + "\\missing"));
        }
    }

    [Fact]
    public void Packaged_state_numbers_follow_Task_Manager()
    {
        Assert.Equal(StartupState.Disabled, StartupReaders.MapPackagedState(0));
        Assert.Equal(StartupState.Disabled, StartupReaders.MapPackagedState(1));
        Assert.Equal(StartupState.Enabled, StartupReaders.MapPackagedState(2));
        Assert.Equal(StartupState.Disabled, StartupReaders.MapPackagedState(3));
        Assert.Equal(StartupState.Enabled, StartupReaders.MapPackagedState(4));
        Assert.Equal(StartupState.Unknown, StartupReaders.MapPackagedState(5));
    }

    [Fact]
    public async Task A_missing_place_is_an_empty_place()
    {
        using var world = new TempStartupWorld();
        var list = await world.Manager(elevated: false).ListAsync();
        Assert.Empty(list);
    }
}

public class StartupManagerToggleTests
{
    [Fact]
    public async Task Switching_a_user_entry_off_and_on_changes_only_its_approval_and_undo_puts_it_back_as_it_was()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        world.AddRun(StartupProvider.RunHkcu, "App", @"C:\x\app.exe");
        var manager = world.Manager(elevated: false);
        const string Id = "runHkcu:App";

        Assert.False(manager.HasUndo(Id));
        var off = await manager.SetEnabledAsync(Id, false);
        Assert.Equal(OperationCode.Ok, off.Code);
        var disabled = world.GetApproval(StartupProvider.RunHkcu, "App")!;
        Assert.Equal(12, disabled.Length);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(disabled));
        Assert.Equal(TempStartupWorld.Now, StartupApprovedCodec.DisabledAt(disabled));
        Assert.True(manager.HasUndo(Id));
        Assert.Equal(@"C:\x\app.exe", world.GetRun(StartupProvider.RunHkcu, "App"));

        var on = await manager.SetEnabledAsync(Id, true);
        Assert.Equal(OperationCode.Ok, on.Code);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(world.GetApproval(StartupProvider.RunHkcu, "App")!));
        Assert.True(manager.HasUndo(Id), "the FIRST original is kept, so undo is still the way back to 'no value'");

        var undone = await manager.UndoAsync(Id);
        Assert.Equal(OperationCode.Ok, undone.Code);
        Assert.Null(world.GetApproval(StartupProvider.RunHkcu, "App"));
        Assert.False(manager.HasUndo(Id));
        Assert.Equal(@"C:\x\app.exe", world.GetRun(StartupProvider.RunHkcu, "App"));
        Assert.Equal(StartupState.Enabled, (await manager.ListAsync()).Single().State);
    }

    [Fact]
    public async Task Undo_restores_an_original_value_that_existed_exactly()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        world.AddRun(StartupProvider.RunHkcu, "App", "a.exe");
        var original = new byte[] { 6, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        world.SetApproval(StartupProvider.RunHkcu, "App", original);
        var manager = world.Manager(elevated: false);

        Assert.Equal(OperationCode.Ok, (await manager.SetEnabledAsync("runHkcu:App", false)).Code);
        Assert.NotEqual(original, world.GetApproval(StartupProvider.RunHkcu, "App"));
        Assert.Equal(OperationCode.Ok, (await manager.UndoAsync("runHkcu:App")).Code);
        Assert.Equal(original, world.GetApproval(StartupProvider.RunHkcu, "App"));
    }

    [Fact]
    public async Task Asking_for_the_state_it_already_has_writes_nothing()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        world.AddRun(StartupProvider.RunHkcu, "On", "a.exe");
        world.AddRun(StartupProvider.RunHkcu, "Off", "b.exe");
        world.SetApproval(StartupProvider.RunHkcu, "Off", TempStartupWorld.Approval(3));
        var manager = world.Manager(elevated: false);

        Assert.Equal(OperationCode.Ok, (await manager.SetEnabledAsync("runHkcu:On", true)).Code);
        Assert.Equal(OperationCode.Ok, (await manager.SetEnabledAsync("runHkcu:Off", false)).Code);

        Assert.Null(world.GetApproval(StartupProvider.RunHkcu, "On"));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(world.GetApproval(StartupProvider.RunHkcu, "Off")!));
        Assert.False(manager.HasUndo("runHkcu:On"));
        Assert.False(manager.HasUndo("runHkcu:Off"));
    }

    [Fact]
    public async Task An_unknown_approval_is_left_alone()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        world.AddRun(StartupProvider.RunHkcu, "Odd", "a.exe");
        var odd = TempStartupWorld.Approval(9);
        world.SetApproval(StartupProvider.RunHkcu, "Odd", odd);

        var result = await world.Manager(elevated: false).SetEnabledAsync("runHkcu:Odd", false);

        Assert.Equal(OperationCode.Unsupported, result.Code);
        Assert.Equal(odd, world.GetApproval(StartupProvider.RunHkcu, "Odd"));
    }

    [Fact]
    public async Task An_entry_that_does_not_exist_is_not_found_and_no_approval_is_made_up_for_it()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        var manager = world.Manager(elevated: true);
        foreach (var id in new[] { "runHkcu:Ghost", "runHklm:Ghost", "startupFolderUser:ghost.lnk", "garbage", "" })
        {
            Assert.Equal(OperationCode.NotFound, (await manager.SetEnabledAsync(id, false)).Code);
        }

        Assert.Null(world.GetApproval(StartupProvider.RunHkcu, "Ghost"));
        Assert.Null(world.GetApproval(StartupProvider.RunHklm, "Ghost"));
    }

    [Fact]
    public async Task The_name_in_an_id_matches_without_regard_to_case_and_the_real_spelling_is_used()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        world.AddRun(StartupProvider.RunHkcu, "MixedCase", "a.exe");

        var result = await world.Manager(elevated: false).SetEnabledAsync("runHkcu:MIXEDCASE", false);

        Assert.Equal(OperationCode.Ok, result.Code);
        Assert.NotNull(world.GetApproval(StartupProvider.RunHkcu, "MixedCase"));
    }

    [Fact]
    public async Task A_folder_entry_is_switched_through_the_StartupFolder_store()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        File.WriteAllText(Path.Combine(world.UserFolder, "item.txt"), "x");
        var manager = world.Manager(elevated: false);

        Assert.Equal(OperationCode.Ok, (await manager.SetEnabledAsync("startupFolderUser:item.txt", false)).Code);

        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(world.GetApproval(StartupProvider.StartupFolderUser, "item.txt")!));
        Assert.True(File.Exists(Path.Combine(world.UserFolder, "item.txt")), "the file itself is never touched");
        Assert.Equal(StartupState.Disabled, (await manager.ListAsync()).Single().State);
    }

    [Fact]
    public async Task Elevated_a_machine_side_entry_is_switched_in_process_and_the_helper_is_not_asked()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        world.AddRun(StartupProvider.RunHklm, "Svc", "s.exe");
        var helper = new RecordingHelper();
        var manager = world.Manager(elevated: true, helper);

        Assert.Equal(OperationCode.Ok, (await manager.SetEnabledAsync("runHklm:Svc", false)).Code);

        Assert.Empty(helper.Requests);
        Assert.NotNull(world.GetApproval(StartupProvider.RunHklm, "Svc"));
        Assert.True(manager.HasUndo("runHklm:Svc"));
        Assert.Equal(OperationCode.Ok, (await manager.UndoAsync("runHklm:Svc")).Code);
        Assert.Null(world.GetApproval(StartupProvider.RunHklm, "Svc"));
    }

    private static HelperOutcome HelperSaid(HelperRequest request, OperationCode code, string key, StartupOriginal? original = null)
    {
        var action = ActionResult.Of(code, key, ("name", request.ValueName ?? ""));
        if (original is not null)
        {
            action = action.WithData(original.ToData());
        }

        return new HelperOutcome(code, false, action.ToHelperResult("nonce", HelperOperations.Get(request.Op).Name));
    }

    [Fact]
    public async Task Not_elevated_a_machine_side_entry_goes_to_the_helper_as_one_request_and_its_original_is_kept_for_undo()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        world.AddRun(StartupProvider.RunHklm, "Svc", "s.exe");
        var original = new StartupOriginal(false, RegistryValueKind.None, null);
        var helper = new RecordingHelper(r => HelperSaid(r, OperationCode.Ok, MessageKeys.StartupDisabled, original));
        var manager = world.Manager(elevated: false, helper);

        var result = await manager.SetEnabledAsync("runHklm:Svc", false);

        Assert.Equal(OperationCode.Ok, result.Code);
        var request = Assert.Single(helper.Requests);
        Assert.Equal(HelperOperation.StartupHklmSet, request.Op);
        Assert.Equal(StartupProvider.RunHklm, request.Provider);
        Assert.Equal("Svc", request.ValueName);
        Assert.False(request.Enable);
        Assert.Null(world.GetApproval(StartupProvider.RunHklm, "Svc"));
        Assert.True(manager.HasUndo("runHklm:Svc"));
    }

    [Fact]
    public async Task A_declined_prompt_changes_nothing_and_keeps_no_undo()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        world.AddRun(StartupProvider.RunHklm32, "Svc", "s.exe");
        var helper = new RecordingHelper(_ => new HelperOutcome(OperationCode.Cancelled, true, null));
        var manager = world.Manager(elevated: false, helper);

        var result = await manager.SetEnabledAsync("runHklm32:Svc", false);

        Assert.Equal(OperationCode.Cancelled, result.Code);
        Assert.False(manager.HasUndo("runHklm32:Svc"));
        Assert.Null(world.GetApproval(StartupProvider.RunHklm32, "Svc"));
    }

    [Fact]
    public async Task A_name_the_helper_would_refuse_is_refused_here_before_any_prompt()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        world.AddRun(StartupProvider.RunHklm, "bad\u0001name", "s.exe");
        var helper = new RecordingHelper();
        var manager = world.Manager(elevated: false, helper);

        var result = await manager.SetEnabledAsync("runHklm:bad\u0001name", false);

        Assert.Equal(OperationCode.InvalidArguments, result.Code);
        Assert.Empty(helper.Requests);
    }

    [Fact]
    public async Task Undo_through_the_helper_goes_back_to_the_original_state_and_skips_the_prompt_when_it_is_already_there()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        world.AddRun(StartupProvider.RunHklm, "Svc", "s.exe");
        // The entry was enabled (value 2) before; now it is disabled (3), and the record says so.
        world.SetApproval(StartupProvider.RunHklm, "Svc", TempStartupWorld.Approval(3));
        var store = new StartupUndoStore(world.UndoFile);
        var enabledOriginal = new StartupOriginal(true, RegistryValueKind.Binary, TempStartupWorld.Approval(2)).ToData();
        store.SetIfAbsent(
            "runHklm:Svc",
            new StartupUndoRecord("runHklm", "Svc", enabledOriginal[StartupOriginal.KeyExisted], enabledOriginal[StartupOriginal.KeyKind], enabledOriginal[StartupOriginal.KeyBytes]));

        var helper = new RecordingHelper(r => HelperSaid(r, OperationCode.Ok, MessageKeys.StartupEnabled));
        var manager = world.Manager(elevated: false, helper);
        var result = await manager.UndoAsync("runHklm:Svc");

        Assert.Equal(OperationCode.Ok, result.Code);
        var request = Assert.Single(helper.Requests);
        Assert.True(request.Enable);
        Assert.False(manager.HasUndo("runHklm:Svc"));

        // Already at the original state: no helper, record dropped.
        world.SetApproval(StartupProvider.RunHklm, "Svc", TempStartupWorld.Approval(2));
        new StartupUndoStore(world.UndoFile).SetIfAbsent(
            "runHklm:Svc",
            new StartupUndoRecord("runHklm", "Svc", enabledOriginal[StartupOriginal.KeyExisted], enabledOriginal[StartupOriginal.KeyKind], enabledOriginal[StartupOriginal.KeyBytes]));
        var second = world.Manager(elevated: false, helper);
        Assert.Equal(OperationCode.Ok, (await second.UndoAsync("runHklm:Svc")).Code);
        Assert.Single(helper.Requests);
        Assert.False(second.HasUndo("runHklm:Svc"));
    }

    [Fact]
    public async Task Undo_without_a_record_is_not_found_and_a_record_that_cannot_be_applied_is_dropped()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        var manager = world.Manager(elevated: true);
        Assert.Equal(OperationCode.NotFound, (await manager.UndoAsync("runHkcu:Nothing")).Code);
        Assert.Equal(OperationCode.NotFound, (await manager.UndoAsync("not an id")).Code);

        new StartupUndoStore(world.UndoFile).SetIfAbsent("runHkcu:Bad", new StartupUndoRecord("runHkcu", "Bad", "true", "NoSuchKind", "%%%"));
        var bad = world.Manager(elevated: true);
        Assert.True(bad.HasUndo("runHkcu:Bad"));
        Assert.Equal(OperationCode.Error, (await bad.UndoAsync("runHkcu:Bad")).Code);
        Assert.False(bad.HasUndo("runHkcu:Bad"));
    }

    [Fact]
    public async Task A_call_cancelled_before_it_began_changes_nothing()
    {
        Strings.Register(ManagementStrings.All);
        using var world = new TempStartupWorld();
        world.AddRun(StartupProvider.RunHkcu, "App", "a.exe");
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        var result = await world.Manager(elevated: false).SetEnabledAsync("runHkcu:App", false, source.Token);
        Assert.Equal(OperationCode.Cancelled, result.Code);
        Assert.Null(world.GetApproval(StartupProvider.RunHkcu, "App"));
    }
}

/// <summary>The real PC, read only. The registry and the shell are only looked at, never written.</summary>
public class StartupManagerRealReadTests
{
    private static string[] RegistryNames(RegistryHive hive, RegistryView view)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.OpenSubKey(StartupApprovedCodec.RunKeyPath);
        if (key is null)
        {
            return [];
        }

        return key.GetValueNames()
            .Where(n => n.Length > 0 && key.GetValueKind(n) is RegistryValueKind.String or RegistryValueKind.ExpandString)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? RunPowerShell(string script)
    {
        try
        {
            var info = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding = [Text.Encoding]::UTF8\n" + script)))
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            var text = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(60_000) && process.ExitCode == 0 ? text : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    [Fact]
    public async Task The_real_run_key_lists_equal_what_the_registry_holds()
    {
        var items = await new StartupManager(new RecordingHelper()).ListAsync();

        string[] Names(StartupProvider p) => items.Where(i => i.Provider == p).Select(i => i.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(RegistryNames(RegistryHive.CurrentUser, RegistryView.Registry64), Names(StartupProvider.RunHkcu));
        Assert.Equal(RegistryNames(RegistryHive.LocalMachine, RegistryView.Registry64), Names(StartupProvider.RunHklm));
        Assert.Equal(RegistryNames(RegistryHive.LocalMachine, RegistryView.Registry32), Names(StartupProvider.RunHklm32));
        Assert.All(items, i => Assert.False(string.IsNullOrEmpty(i.Id)));
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task The_real_list_agrees_with_Win32_StartupCommand_for_the_run_keys()
    {
        var text = RunPowerShell("Get-CimInstance Win32_StartupCommand | ForEach-Object { $_.Name + '|' + $_.Location }");
        Assert.NotNull(text); // Windows PowerShell 5.1 must answer: a silent skip would hide a broken comparison

        var items = await new StartupManager(new RecordingHelper()).ListAsync();
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var cimUser = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cimMachine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cimFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bar = line.LastIndexOf('|');
            if (bar <= 0)
            {
                continue;
            }

            var name = line[..bar];
            var location = line[(bar + 1)..];
            if (location.StartsWith(@"HKU\" + sid + @"\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", StringComparison.OrdinalIgnoreCase))
            {
                cimUser.Add(name);
            }
            else if (location.StartsWith(@"HKLM\SOFTWARE\", StringComparison.OrdinalIgnoreCase))
            {
                cimMachine.Add(name);
            }
            else if (location.Contains("Startup", StringComparison.OrdinalIgnoreCase))
            {
                cimFolders.Add(name);
            }
        }

        var ours = (StartupProvider p) => items.Where(i => i.Provider == p).Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(cimUser.Count > 0, "this PC has user Run entries; the comparison would be empty otherwise");
        Assert.True(!cimUser.Any(n => !ours(StartupProvider.RunHkcu).Contains(n)), "user Run names CIM lists but we do not");
        var machine = ours(StartupProvider.RunHklm).Concat(ours(StartupProvider.RunHklm32)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(!cimMachine.Any(n => !machine.Contains(n)), "machine Run names CIM lists but we do not");

        // CIM drops the extension of folder entries and mangles non-ASCII names, so only the ASCII ones are held against us.
        var folderNames = ours(StartupProvider.StartupFolderUser).Concat(ours(StartupProvider.StartupFolderCommon))
            .Select(n => Path.GetFileNameWithoutExtension(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(!cimFolders.Any(n => n.All(c => c < 128) && !folderNames.Contains(n)), "folder entries CIM lists but we do not");
    }

    [Fact]
    public async Task The_real_task_scheduler_list_agrees_with_Get_ScheduledTask()
    {
        const string Script = """
            Get-ScheduledTask | Where-Object { $_.TaskPath -notlike '\Microsoft\Windows*' } | ForEach-Object {
              $t = $_
              $hit = $false
              foreach ($tr in $t.Triggers) { if ($tr.Enabled -and ($tr.CimClass.CimClassName -in 'MSFT_TaskLogonTrigger','MSFT_TaskBootTrigger')) { $hit = $true } }
              if ($hit) { $t.TaskPath + $t.TaskName + '|' + $t.State }
            }
            """;
        var text = RunPowerShell(Script);
        Assert.NotNull(text); // Windows PowerShell 5.1 must answer: a silent skip would hide a broken comparison

        var expected = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|'))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);

        var ours = StartupReaders.ReadScheduledTasks(CancellationToken.None).ToDictionary(e => e.IdName!, StringComparer.OrdinalIgnoreCase);

        Assert.True(expected.Count > 0, "this PC has logon/boot tasks; the comparison would be empty otherwise");
        var missing = expected.Keys.Where(k => !ours.ContainsKey(k)).ToArray();
        var extra = ours.Keys.Where(k => !expected.ContainsKey(k)).ToArray();
        Assert.True(missing.Length <= 2 && extra.Length <= 2, $"missing: [{string.Join(", ", missing)}] extra: [{string.Join(", ", extra)}]");

        foreach (var (path, state) in expected.Where(e => ours.ContainsKey(e.Key)))
        {
            var disabled = state.Equals("Disabled", StringComparison.OrdinalIgnoreCase);
            Assert.Equal(disabled ? StartupState.Disabled : StartupState.Enabled, ours[path].ListedState);
        }

        Assert.All(ours.Values, e => Assert.False(e.IdName!.StartsWith(@"\Microsoft\Windows", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task The_real_packaged_app_list_reads_without_failing()
    {
        var items = await new StartupManager(new RecordingHelper()).ListAsync();
        Assert.All(items.Where(i => i.Provider == StartupProvider.PackagedApp), i =>
        {
            Assert.False(i.CanToggle);
            Assert.Contains("/", i.Id);
        });
    }
}
