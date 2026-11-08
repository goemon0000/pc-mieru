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

    [Fact]
    public void Every_source_that_names_an_origin_is_listed_in_NOTICE()
    {
        var notice = File.ReadAllText(Path.Combine(Root, "NOTICE.txt"));
        var origin = new Regex("parts-life|pl-sensors|PartsLife", RegexOptions.IgnoreCase);
        var named = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Root, path).Replace('\\', '/');
            if ((!path.EndsWith(".cs", StringComparison.Ordinal) && !path.EndsWith(".csproj", StringComparison.Ordinal))
                || rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            if (origin.IsMatch(File.ReadAllText(path)))
            {
                named.Add(rel);
            }
        }

        Assert.NotEmpty(named);
        Assert.All(named, rel => Assert.True(notice.Contains(rel, StringComparison.Ordinal), $"{rel} names its origin but NOTICE.txt does not list it"));
    }

    [Theory]
    [InlineData("PcMieru.Gui")]
    [InlineData("PcMieru.Cli")]
    public void Apps_build_SensorHost_and_copy_it_next_to_themselves(string project)
    {
        var text = File.ReadAllText(Path.Combine(Root, "src", project, project + ".csproj"));
        Assert.Matches(@"<ProjectReference Include=""[^""]*PcMieru\.SensorHost\.csproj"" ReferenceOutputAssembly=""false"" />", text);
        Assert.Contains("SensorHostNextToApp.targets", text, StringComparison.Ordinal);
        var targets = File.ReadAllText(Path.Combine(Root, "build", "SensorHostNextToApp.targets"));
        Assert.Contains("PCMieru.SensorHost", targets, StringComparison.Ordinal);
    }
}
