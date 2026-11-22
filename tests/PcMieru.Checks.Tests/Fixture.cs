namespace PcMieru.Checks.Tests;

/// <summary>
/// Golden files for the pcm-check/1 JSON. A shape change fails the test on purpose: review the diff (the format
/// string changes if it is not additive), then regenerate with PCMIERU_UPDATE_FIXTURES=1.
/// </summary>
internal static class Fixture
{
    public static string Dir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PcMieru.slnx")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "tests", "PcMieru.Checks.Tests", "Fixtures");
        }
    }

    public static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";

    public static string Read(string name)
    {
        var path = Path.Combine(Dir, name);
        Assert.True(File.Exists(path), $"fixture {name} is missing; run with PCMIERU_UPDATE_FIXTURES=1");
        return File.ReadAllText(path);
    }

    public static void AssertMatches(string name, string actual)
    {
        var path = Path.Combine(Dir, name);
        actual = Normalize(actual);
        if (Environment.GetEnvironmentVariable("PCMIERU_UPDATE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(path, actual, new System.Text.UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(path), $"fixture {name} is missing; run with PCMIERU_UPDATE_FIXTURES=1");
        var expected = Normalize(File.ReadAllText(path));
        Assert.True(
            expected == actual,
            $"{name} differs from the current output. If the change is intended (additive, or the format string bumped), regenerate with PCMIERU_UPDATE_FIXTURES=1 and review the diff.");
    }
}
