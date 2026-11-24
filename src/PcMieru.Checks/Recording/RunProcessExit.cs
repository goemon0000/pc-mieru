namespace PcMieru.Checks;

/// <summary>
/// The process-exit hook of the recorder, as a seam: the product hooks <see cref="AppDomain.ProcessExit"/>, a test "exits" without ending the test host.
/// The recorder registers one only while a run is active and disposes the registration when the run has ended, so a process that is not recording has
/// no handler of this kind at all.
/// </summary>
internal interface IProcessExit
{
    /// <summary>Calls <paramref name="onExit"/> when the process is exiting, until the returned registration is disposed (more than once is harmless).</summary>
    IDisposable Register(Action onExit);
}

/// <summary>The real thing: <see cref="AppDomain.ProcessExit"/>.</summary>
internal sealed class AppDomainProcessExit : IProcessExit
{
    public static AppDomainProcessExit Instance { get; } = new();

    public IDisposable Register(Action onExit)
    {
        ArgumentNullException.ThrowIfNull(onExit);
        EventHandler handler = (_, _) => onExit();
        AppDomain.CurrentDomain.ProcessExit += handler;
        return new Registration(handler);
    }

    private sealed class Registration(EventHandler handler) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                AppDomain.CurrentDomain.ProcessExit -= handler;
            }
        }
    }
}
