using System.Globalization;
using PcMieru.Core.Collection;

namespace PcMieru.Gui.Tests;

internal static class Harness
{
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PcMieru.slnx")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir.FullName;
        }
    }

    public static string GuiSource => Path.Combine(Root, "src", "PcMieru.Gui");

    /// <summary>Invariant culture and a chosen language for the length of a test; both are restored on dispose.</summary>
    public static IDisposable Locale(Language language = Language.En)
    {
        var culture = CultureInfo.CurrentCulture;
        var current = Strings.Current;
        Strings.Register(GuiStrings.All);
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Strings.Current = language;
        return new Restore(() =>
        {
            CultureInfo.CurrentCulture = culture;
            Strings.Current = current;
        });
    }

    public static async Task<Snapshot> FakeSnapshotAsync()
    {
        using var source = new FakeSnapshotSource();
        return await source.SampleAsync();
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
