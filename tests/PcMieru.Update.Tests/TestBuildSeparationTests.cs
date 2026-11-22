using System.Text;
using System.Text.RegularExpressions;
using PcMieru.Update.Transaction;

namespace PcMieru.Update.Tests;

/// <summary>
/// The test-only entry of the runner stays out of the product (review of step 4, S3). A swap with a scripted verdict and a stop at a named step are what a trial needs and what no shipped program may have: they are in
/// tests/PcMieru.Swap.TestBuild (the TEST BUILD of PCMieru.Swap.exe) and nowhere in src/. These tests read the source of the product and the built product assembly; tools/publish.ps1 then looks at the built exe itself
/// (the option name and the variable name, as ASCII and as UTF-16, with a control that proves the scan sees them), and the compile guard in the product's csproj stops a build that defines the test symbol.
/// </summary>
public sealed class TestBuildSeparationTests
{
    private const string Option = "--test-swap";
    private const string Variable = "PCMIERU_SWAP_TEST";
    private const string Symbol = "PCM_SWAP_TESTBUILD";

    private static readonly string Root = RepoFiles.Root();

    private static IEnumerable<string> ProductSources() => Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    // ---------------------------------------------------------------------------------------------------------------------------- the source

    [Theory]
    [InlineData(Option)]
    [InlineData(Variable)]
    [InlineData("StopHere")]
    [InlineData("WriteTestMarker")]
    [InlineData("TestSwitch")]
    [InlineData("TestVariable")]
    [InlineData("SwitchMissing")]
    public void No_source_of_the_product_names_the_test_entry_its_variable_or_its_helpers(string word)
    {
        var hits = ProductSources().Where(f => File.ReadAllText(f).Contains(word, StringComparison.Ordinal)).Select(Relative).ToList();

        Assert.True(hits.Count == 0, $"'{word}' is in the product source: {string.Join(", ", hits)}");
    }

    [Fact]
    public void The_swap_and_the_transaction_never_sleep_for_ever()
    {
        // A "stop here and wait to be killed" is exactly what the test build's entry does and what no shipped program may do.
        var files = ProductSources().Where(f => Relative(f).StartsWith("src/PcMieru.Swap/", StringComparison.Ordinal) || Relative(f).StartsWith("src/PcMieru.Update/Transaction/", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var code = Regex.Replace(File.ReadAllText(file), @"//.*|/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            Assert.DoesNotMatch(@"Timeout\.Infinite|Sleep\(\s*-1\s*\)|int\.MaxValue\)|WaitHandle\.WaitAny|new\s+ManualResetEvent\([^)]*\)\.WaitOne\(\)", code);
        }
    }

    [Fact]
    public void The_test_symbol_is_named_by_the_swap_entry_and_by_nothing_else_in_the_product_source()
    {
        var hits = ProductSources().Where(f => File.ReadAllText(f).Contains(Symbol, StringComparison.Ordinal)).Select(Relative).ToList();

        Assert.Equal(["src/PcMieru.Swap/Program.cs"], hits);
        var program = File.ReadAllText(Path.Combine(Root, "src", "PcMieru.Swap", "Program.cs"));
        Assert.Matches(@"(?m)^#if " + Symbol + @"\r?\n(?:\s*//[^\n]*\r?\n)*\s*return PcMieru\.Swap\.TestBuild\.TestEntry\.Run\(", program);
        Assert.Matches(@"(?m)^#else\r?\n\s*return SwapCli\.Run\(", program);
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the projects

    [Fact]
    public void The_product_project_stops_a_build_that_defines_the_test_symbol_and_defines_nothing_itself()
    {
        var product = File.ReadAllText(Path.Combine(Root, "src", "PcMieru.Swap", "PcMieru.Swap.csproj"));

        Assert.Contains("<Target Name=\"NoTestEntryInTheProduct\" BeforeTargets=\"CoreCompile\" Condition=\"$(DefineConstants.Contains('" + Symbol + "'))\">", product, StringComparison.Ordinal);
        Assert.Contains("<Error Text=\"" + Symbol + " is defined in the product's PCMieru.Swap project.", product, StringComparison.Ordinal);
        Assert.DoesNotContain("<DefineConstants>", product, StringComparison.Ordinal);
        foreach (var project in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories).Where(f => !f.EndsWith("PcMieru.Swap.csproj", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.DoesNotContain(Symbol, File.ReadAllText(project), StringComparison.Ordinal);
        }

        foreach (var shared in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
        {
            var path = Path.Combine(Root, shared);
            Assert.False(File.Exists(path) && File.ReadAllText(path).Contains(Symbol, StringComparison.Ordinal), shared + " defines the test symbol for every project");
        }
    }

    [Fact]
    public void The_test_build_project_defines_the_symbol_links_the_products_program_and_is_not_part_of_the_product()
    {
        var testBuild = File.ReadAllText(Path.Combine(Root, "tests", "PcMieru.Swap.TestBuild", "PcMieru.Swap.TestBuild.csproj"));

        Assert.Contains("<DefineConstants>$(DefineConstants);" + Symbol + "</DefineConstants>", testBuild, StringComparison.Ordinal);
        Assert.Contains(@"<Compile Include=""..\..\src\PcMieru.Swap\Program.cs"" Link=""Program.cs"" />", testBuild, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Root, "src", "PcMieru.Swap.TestBuild")), "the test build belongs in tests/");
        var entry = File.ReadAllText(Path.Combine(Root, "tests", "PcMieru.Swap.TestBuild", "TestEntry.cs"));
        Assert.Contains("\"" + Option + "\"", entry, StringComparison.Ordinal);
        Assert.Contains("\"" + Variable + "\"", entry, StringComparison.Ordinal);

        // The publish script does not know the test build, and the release floor does not list it.
        var publish = File.ReadAllText(Path.Combine(Root, "tools", "publish.ps1"));
        Assert.DoesNotContain("TestBuild", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("PCMieru.Swap.TestBuild", File.ReadAllText(Path.Combine(Root, "src", "PcMieru.Update", "Bundle", "BundleFloor.cs")), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the built product

    private static bool Contains(byte[] bytes, string text) =>
        Contains(bytes, Encoding.ASCII.GetBytes(text)) || Contains(bytes, Encoding.Unicode.GetBytes(text));

    private static bool Contains(byte[] bytes, byte[] needle) => bytes.AsSpan().IndexOf(needle) >= 0;

    [Fact]
    public void The_built_update_assembly_of_the_product_has_neither_name_in_any_encoding_and_the_scan_does_see_them_in_an_assembly_that_has_the_entry()
    {
        var product = File.ReadAllBytes(typeof(SwapCli).Assembly.Location);
        var withEntry = File.ReadAllBytes(typeof(TestBuildSeparationTests).Assembly.Location); // this project compiles TestEntry.cs in: the control

        Assert.True(product.Length > 100_000, "the product assembly was not read");
        Assert.False(Contains(product, Option), "PcMieru.Update.dll holds the test option");
        Assert.False(Contains(product, Variable), "PcMieru.Update.dll holds the test variable");
        Assert.True(Contains(withEntry, Option), "the control: the scan must find the option where the entry is");
        Assert.True(Contains(withEntry, Variable), "the control: the scan must find the variable where the entry is");
        Assert.Null(typeof(SwapCli).Assembly.GetType("PcMieru.Swap.TestBuild.TestEntry"));
        Assert.NotNull(typeof(TestBuildSeparationTests).Assembly.GetType("PcMieru.Swap.TestBuild.TestEntry"));
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the product's program

    [Theory]
    [InlineData("--test-swap")]
    [InlineData("--test-swap|C:\\staged|-|Before|1|C:\\marker.txt|ok")]
    [InlineData("--then-start|--test-swap|C:\\staged|-|Before|1|C:\\marker.txt|ok")]
    [InlineData("--after-pid|1234|--test-swap|C:\\staged|rollback.journal|After|2|C:\\marker.txt|fail")]
    public void The_products_program_does_not_know_the_entry_whatever_the_environment_says(string line)
    {
        using var rig = Rig.Create("sep-product", recorded: true);
        var before = Rig.Snapshot(rig.Install, includeUpdate: true);
        var err = new StringWriter();
        var starter = new FakeStarter();
        var host = new SwapHost
        {
            SelfPath = rig.Paths.Recover,
            Out = new StringWriter(),
            Error = err,
            IsElevated = static () => false,
            Seams = Rig.Seams(),
            Starter = starter,
        };
        Environment.SetEnvironmentVariable(Variable, rig.Install);
        try
        {
            Assert.Equal(SwapExit.BadCommandLine, SwapCli.Run(line.Split('|'), host));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, null);
        }

        Assert.StartsWith("swap: ", err.ToString(), StringComparison.Ordinal);
        Assert.Empty(starter.Starts);
        rig.AssertUnchanged(before, "the product's program with a test entry's command line", includeUpdate: true);
    }
}
