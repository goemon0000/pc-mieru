using PcMieru.Update.Launch;

namespace PcMieru.Gui;

internal static class VerifyUpdate
{
    public static int Run(string[] args)
    {
        using var entry = VerifyAdmission.Admit(args, Environment.ProcessPath!, out var code);
        if (entry is null)
        {
            return code;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Strings.Register(GuiStrings.All);
        ApplicationConfiguration.Initialize();
        var settings = new GuiSettings();
        Strings.Current = settings.Language;
        using var theme = new ThemeService(settings.Theme);
        using var services = PcMieruServices.Create(new PcMieruOptions { NoStateFiles = true, InProcessOnly = true });
        using var pump = new SnapshotPump(services, settings.IntervalMs);
        var context = new GuiContext(services, pump, theme, settings, verifying: true);
        using var form = new MainForm(context);
        using var session = new VerifySession(entry.Channel, action =>
        {
            try
            {
                form.BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // The window has already closed.
            }
        }, form.Close);

        form.Shown += (_, _) =>
        {
            // Redraw the visible window and its child controls before reporting the first paint.
            form.Refresh();
            session.ReportDrawn();
            session.Start();
        };
        Application.Run(form);
        return session.Completed ? VerifyExit.Done : VerifyExit.Interrupted;
    }
}
