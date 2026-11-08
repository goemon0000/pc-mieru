namespace PcMieru.Gui;

/// <summary>A navigation entry before its page exists. Pages are created the first time they are shown.</summary>
internal sealed record PageEntry(string Id, string TitleKey, Func<GuiContext, PageBase> Create);

/// <summary>The one place that lists the pages and their order. Adding a page = one file in Pages/ + one line here.</summary>
internal static class PageCatalog
{
    public static IReadOnlyList<PageEntry> All { get; } =
    [
        new("overview", "gui.nav.overview", c => new OverviewPage(c)),
        new("processes", "gui.nav.processes", c => new ProcessesPage(c)),
        new("performance", "gui.nav.performance", c => new PerformancePage(c)),
        new("startup", "gui.nav.startup", c => new StartupPage(c)),
        new("services", "gui.nav.services", c => new ServicesPage(c)),
        new("users", "gui.nav.users", c => new UsersPage(c)),
        new("hardware", "gui.nav.hardware", c => new HardwarePage(c)),
        new("diagnose", "gui.nav.diagnose", c => new DiagnosePage(c)),
        new("settings", "gui.nav.settings", c => new SettingsPage(c)),
    ];
}
