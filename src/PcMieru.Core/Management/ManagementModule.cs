namespace PcMieru.Core.Management;

/// <summary>
/// Fills the Management slots of the <see cref="ServiceRegistry"/>: the real implementations, or the Fakes for
/// <see cref="PcMieruOptions.ForceFakes"/>. Every constructor here is cheap and has no side effect (nothing is read,
/// started or written until a method is called), so creating the services never touches the system.
/// </summary>
public static class ManagementModule
{
    public static void Register(ServiceRegistry registry, PcMieruOptions options)
    {
        Strings.Register(ManagementStrings.All);
        if (options.ForceFakes)
        {
            RegisterFakes(registry);
        }
        else
        {
            RegisterReal(registry);
        }
    }

    private static void RegisterReal(ServiceRegistry registry)
    {
        var policy = new ProtectionPolicy();
        var eco = new EcoStateStore();
        var helper = new HelperClient();
        var control = new ProcessControl(policy, eco);
        var startup = new StartupManager(helper);

        registry.Set<IProtectionPolicy>(policy);
        registry.Set<IProcessControl>(control);
        registry.Set<IProcessAffinityControl>(control);
        registry.Set<IElevatedProcessActions>(new ElevatedProcessActions(helper, eco));
        registry.Set<IShellActions>(new ShellActions());
        registry.Set<IServiceManager>(new ServiceManager(helper));
        registry.Set<IStartupManager>(startup);
        registry.Set<IStartupUndo>(startup);
        registry.Set<ISessionProvider>(new SessionProvider());
        registry.Set<IHelperClient>(helper);
        registry.Set<ITaskLauncher>(new TaskLauncher());
    }

    private static void RegisterFakes(ServiceRegistry registry)
    {
        var policy = new FakeProtectionPolicy();
        var startup = new FakeStartupManager();
        registry.Set<IProtectionPolicy>(policy);
        registry.Set<IProcessControl>(new FakeProcessControl(policy));
        registry.Set<IProcessAffinityControl>(new FakeProcessAffinityControl());
        registry.Set<IElevatedProcessActions>(new FakeElevatedProcessActions());
        registry.Set<IShellActions>(new FakeShellActions());
        registry.Set<IServiceManager>(new FakeServiceManager());
        registry.Set<IStartupManager>(startup);
        registry.Set<IStartupUndo>(startup);
        registry.Set<ISessionProvider>(new FakeSessionProvider());
        registry.Set<IHelperClient>(new FakeHelperClient());
        registry.Set<ITaskLauncher>(new FakeTaskLauncher());
    }
}
