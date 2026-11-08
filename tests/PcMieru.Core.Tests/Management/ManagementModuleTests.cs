using PcMieru.Core.Management;

namespace PcMieru.Core.Tests.Management;

public class ManagementModuleTests
{
    private static ServiceRegistry Register(bool forceFakes)
    {
        var registry = new ServiceRegistry();
        ManagementModule.Register(registry, new PcMieruOptions { ForceFakes = forceFakes });
        return registry;
    }

    private static string[] AppFolderEntries()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMieru");
        return Directory.Exists(folder)
            ? Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
    }

    [Fact]
    public void Every_slot_is_filled_in_both_modes_and_the_modes_differ_in_the_type_behind_it()
    {
        var fake = Register(forceFakes: true);
        var real = Register(forceFakes: false);

        void Same<T>(Func<object, object, bool> differ)
            where T : class
        {
            var f = fake.Get<T>();
            var r = real.Get<T>();
            Assert.NotNull(f);
            Assert.NotNull(r);
            Assert.True(differ(f, r), typeof(T).Name + ": the fake and the real implementation must be different types");
        }

        Same<IProtectionPolicy>((f, r) => f.GetType() != r.GetType());
        Same<IProcessControl>((f, r) => f.GetType() != r.GetType());
        Same<IProcessAffinityControl>((f, r) => f.GetType() != r.GetType());
        Same<IElevatedProcessActions>((f, r) => f.GetType() != r.GetType());
        Same<IShellActions>((f, r) => f.GetType() != r.GetType());
        Same<IServiceManager>((f, r) => f.GetType() != r.GetType());
        Same<IStartupManager>((f, r) => f.GetType() != r.GetType());
        Same<IStartupUndo>((f, r) => f.GetType() != r.GetType());
        Same<ISessionProvider>((f, r) => f.GetType() != r.GetType());
        Same<IHelperClient>((f, r) => f.GetType() != r.GetType());
        Same<ITaskLauncher>((f, r) => f.GetType() != r.GetType());
    }

    [Fact]
    public void The_real_startup_manager_serves_both_the_manager_and_the_undo_interface_as_one_object()
    {
        var real = Register(forceFakes: false);
        Assert.Same(real.Get<IStartupManager>(), real.Get<IStartupUndo>());
        Assert.Same(real.Get<IProcessControl>(), real.Get<IProcessAffinityControl>());

        var fake = Register(forceFakes: true);
        Assert.Same(fake.Get<IStartupManager>(), fake.Get<IStartupUndo>());
    }

    [Fact]
    public void The_fakes_are_in_memory_only_and_never_start_the_helper()
    {
        var fake = Register(forceFakes: true);
        Assert.IsAssignableFrom<IHelperClient>(fake.Get<IHelperClient>());
        Assert.StartsWith("Fake", fake.Get<IHelperClient>().GetType().Name, StringComparison.Ordinal);
        Assert.StartsWith("Fake", fake.Get<IProcessControl>().GetType().Name, StringComparison.Ordinal);
        Assert.StartsWith("Fake", fake.Get<IServiceManager>().GetType().Name, StringComparison.Ordinal);
        Assert.StartsWith("Fake", fake.Get<IStartupManager>().GetType().Name, StringComparison.Ordinal);
    }

    [Fact]
    public void Creating_the_real_services_reads_writes_and_starts_nothing()
    {
        var before = AppFolderEntries();

        var registry = Register(forceFakes: false);
        _ = registry.Get<IProcessControl>();
        _ = registry.Get<IServiceManager>();
        _ = registry.Get<IStartupManager>();
        _ = registry.Get<IHelperClient>();

        Assert.Equal(before, AppFolderEntries());
    }

    [Fact]
    public void Management_text_is_registered_by_Register_so_a_front_end_needs_no_extra_step()
    {
        _ = Register(forceFakes: true);
        Assert.NotEqual("[" + ManagementStrings.HelperTimeout + "]", Strings.Get(ManagementStrings.HelperTimeout, Language.En));
        Assert.NotEqual("[" + ManagementStrings.HelperTimeout + "]", Strings.Get(ManagementStrings.HelperTimeout, Language.Ja));
    }

    [Fact]
    public void ProcessControl_and_the_protection_policy_share_the_one_policy_the_front_end_sees()
    {
        // The policy the UI shows ("protected") and the one the operation enforces must be the same object, or the screen
        // could offer an action the engine then refuses (or the reverse).
        var real = Register(forceFakes: false);
        var policy = real.Get<IProtectionPolicy>();
        var control = (ProcessControl)real.Get<IProcessControl>();
        Assert.Same(policy, control.Policy);
    }
}
