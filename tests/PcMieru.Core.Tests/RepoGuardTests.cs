using System.Text.RegularExpressions;

namespace PcMieru.Core.Tests;

/// <summary>Rules about how the app may be built that no compiler checks: no UAC prompt at start, a tiny Helper.</summary>
public class RepoGuardTests
{
    private static string Root
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

    [Fact]
    public void Every_manifest_is_asInvoker_and_never_asks_for_administrator()
    {
        var manifests = Directory.GetFiles(Path.Combine(Root, "src"), "app.manifest", SearchOption.AllDirectories);
        Assert.NotEmpty(manifests);
        foreach (var path in manifests)
        {
            var text = File.ReadAllText(path);
            Assert.Contains("level=\"asInvoker\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("requireAdministrator", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("highestAvailable", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Helper_references_only_Wire()
    {
        var text = File.ReadAllText(Path.Combine(Root, "src", "PcMieru.Helper", "PcMieru.Helper.csproj"));
        var references = Regex.Matches(text, "<(?:Project|Package)Reference\\s+Include=\"([^\"]+)\"")
            .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', '/').Split('/')[^1]))
            .ToArray();
        Assert.Equal(["PcMieru.Wire"], references);
        Assert.Contains("<PublishAot>true</PublishAot>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Gui_is_a_per_monitor_dpi_windows_exe_named_PCMieru()
    {
        var project = File.ReadAllText(Path.Combine(Root, "src", "PcMieru.Gui", "PcMieru.Gui.csproj"));
        Assert.Contains("<AssemblyName>PCMieru</AssemblyName>", project, StringComparison.Ordinal);
        Assert.Contains("<OutputType>WinExe</OutputType>", project, StringComparison.Ordinal);
        var manifest = File.ReadAllText(Path.Combine(Root, "src", "PcMieru.Gui", "app.manifest"));
        Assert.Contains("PerMonitorV2", manifest, StringComparison.Ordinal);
        Assert.Contains("level=\"asInvoker\"", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_is_named_pcmi()
    {
        var project = File.ReadAllText(Path.Combine(Root, "src", "PcMieru.Cli", "PcMieru.Cli.csproj"));
        Assert.Contains("<AssemblyName>pcmi</AssemblyName>", project, StringComparison.Ordinal);
    }

    [Fact]
    public void Wire_references_nothing()
    {
        var text = File.ReadAllText(Path.Combine(Root, "src", "PcMieru.Wire", "PcMieru.Wire.csproj"));
        Assert.DoesNotContain("<ProjectReference", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<PackageReference", text, StringComparison.Ordinal);
    }
}
