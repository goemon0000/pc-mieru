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

    /// <summary>
    /// The two elevated, NativeAOT helper exes: the Helper (one-shot, asked for per action) and the network meter (opt-in,
    /// long-lived). Both are small on purpose: Wire is all they know of the app, they take no NuGet package, they are AOT,
    /// and the linker is told to load DLLs from System32 only (tools/publish.ps1 then checks the flag in the built exe).
    /// </summary>
    [Theory]
    [InlineData("PcMieru.Helper")]
    [InlineData("PcMieru.NetMeter")]
    public void Elevated_helper_exes_reference_only_Wire_and_are_NativeAOT_with_a_System32_only_search(string project)
    {
        var text = File.ReadAllText(Path.Combine(Root, "src", project, project + ".csproj"));
        var references = Regex.Matches(text, "<(?:Project|Package)Reference\\s+Include=\"([^\"]+)\"")
            .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', '/').Split('/')[^1]))
            .ToArray();
        Assert.Equal(["PcMieru.Wire"], references);
        Assert.Contains("<PublishAot>true</PublishAot>", text, StringComparison.Ordinal);
        Assert.Contains("<LinkerArg Include=\"/DEPENDENTLOADFLAG:0x800\" />", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_release_script_publishes_and_checks_both_helper_exes_and_ships_neither_as_a_managed_dll()
    {
        var script = File.ReadAllText(Path.Combine(Root, "tools", "publish.ps1"));
        foreach (var exe in new[] { "PCMieru.Helper", "PCMieru.NetMeter" })
        {
            Assert.Contains($"'{exe}.exe'", script, StringComparison.Ordinal);
            Assert.Contains($"'{exe}.dll'", script, StringComparison.Ordinal);
        }

        Assert.Contains("Publish-AotExe 'Helper'", script, StringComparison.Ordinal);
        Assert.Contains("Publish-AotExe 'NetMeter'", script, StringComparison.Ordinal);

        // The dev-time reference to the managed meter is switched off for the app publishes, so only the NativeAOT exe ships.
        Assert.Contains("-p:NetMeterNextToApp=false", script, StringComparison.Ordinal);
        var targets = File.ReadAllText(Path.Combine(Root, "build", "NetMeterNextToApp.targets"));
        Assert.Contains("'$(NetMeterNextToApp)' != 'false'", targets, StringComparison.Ordinal);
    }

    [Fact]
    public void Dev_builds_of_the_apps_and_the_tests_put_the_network_meter_next_to_themselves()
    {
        var shared = File.ReadAllText(Path.Combine(Root, "build", "SensorHostNextToApp.targets"));
        Assert.Contains("NetMeterNextToApp.targets", shared, StringComparison.Ordinal);
        var meter = File.ReadAllText(Path.Combine(Root, "build", "NetMeterNextToApp.targets"));
        Assert.Matches(@"<ProjectReference Include=""[^""]*PcMieru\.NetMeter\.csproj"" ReferenceOutputAssembly=""false"" />", meter);
        var tests = File.ReadAllText(Path.Combine(Root, "tests", "PcMieru.Core.Tests", "PcMieru.Core.Tests.csproj"));
        Assert.Contains("NetMeterNextToApp.targets", tests, StringComparison.Ordinal);
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
