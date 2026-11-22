using System.Reflection;
using System.Text.RegularExpressions;
using PcMieru.Update.Bundle;

namespace PcMieru.Update.Tests;

/// <summary>
/// Step 1 of the auto update reads zips and folders and nothing else: PcMieru.Update must not reach for the network. Two things keep it so, and both
/// are looked at here: the assembly's own metadata (what it references), and its source (what it says). Step 2 will add an HttpClient and will have
/// to change this test on purpose, in the commit that adds it, together with the Brotli remark in Directory.Build.props.
/// </summary>
public partial class NoNetworkTests
{
    /// <summary>The framework assemblies a network call would pull in.</summary>
    private static readonly string[] NetworkAssemblies =
    [
        "System.Net.Http",
        "System.Net.Sockets",
        "System.Net.Requests",
        "System.Net.WebClient",
        "System.Net.WebSockets",
        "System.Net.WebSockets.Client",
        "System.Net.Security",
        "System.Net.NameResolution",
        "System.Net.HttpListener",
        "System.Net.Mail",
        "System.Net.Ping",
        "System.Net.ServicePoint",
        "System.Net.WebProxy",
        "System.Net.WebHeaderCollection",
    ];

    [Fact]
    public void The_update_assembly_references_no_network_assembly()
    {
        var references = typeof(BundleZip).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();
        Assert.NotEmpty(references); // the list is really the assembly's, not an empty one

        var network = references.Where(n => NetworkAssemblies.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray();
        Assert.True(network.Length == 0, "PcMieru.Update references " + string.Join(", ", network));

        // What it does reference is the zip library and the basics, which is the point of the first test: a future reference shows up in this list.
        Assert.Contains("System.IO.Compression", references);
    }

    [Fact]
    public void The_update_assembly_never_names_a_network_type_in_its_source()
    {
        var folder = Path.Combine(RepoRoot(), "src", "PcMieru.Update");
        var files = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(folder, f))
            .ToArray();
        Assert.True(files.Length >= 10, $"only {files.Length} source files found below {folder}");

        var hits = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = WithoutComments(lines[i]);
                if (NetworkWord().IsMatch(code))
                {
                    hits.Add($"{Path.GetRelativePath(folder, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(hits.Count == 0, "network code in PcMieru.Update:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void The_update_project_references_no_package_and_no_other_project()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PcMieru.Update", "PcMieru.Update.csproj"));
        Assert.DoesNotContain("<PackageReference", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<ProjectReference", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<FrameworkReference", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_brotli_remark_in_the_build_props_says_what_is_true_of_step_one_and_what_step_two_must_do()
    {
        // The audit suppression stands on "PC Mieru never decompresses Brotli". The remark must say what reads archives now (Deflate and Stored only) and
        // what the HttpClient of step 2 must not do; the old one claimed "no archive reading" and "no HttpClient" as plain facts.
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        Assert.Contains("PC Mieru never decompresses Brotli.", text, StringComparison.Ordinal);
        Assert.Contains("Deflate and Stored only", text, StringComparison.Ordinal);
        Assert.Contains("no HttpClient in step 1", text, StringComparison.Ordinal);
        Assert.Contains("must not enable Brotli in AutomaticDecompression", text, StringComparison.Ordinal);
        Assert.DoesNotContain("no archive reading", text, StringComparison.Ordinal);
        Assert.Contains("NuGetAuditSuppress Include=\"https://github.com/advisories/GHSA-p3jp-9v83-76w9\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_source_check_does_see_a_network_word_it_is_looking_for()
    {
        // A check that cannot fail proves nothing: the pattern and the comment stripping, on lines that do and do not count.
        Assert.Matches(NetworkWord(), WithoutComments("    using var client = new HttpClient();"));
        Assert.Matches(NetworkWord(), WithoutComments("using System.Net.Sockets;"));
        Assert.Matches(NetworkWord(), WithoutComments("    var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);"));
        Assert.Matches(NetworkWord(), WithoutComments("    WebRequest.Create(url); // fetch"));
        Assert.DoesNotMatch(NetworkWord(), WithoutComments("    // step 2 adds an HttpClient"));
        Assert.DoesNotMatch(NetworkWord(), WithoutComments("    /// the HttpClient of step 2"));
        Assert.DoesNotMatch(NetworkWord(), WithoutComments("    var x = 1; // a Socket"));
    }

    // ------------------------------------------------------------------------------------------------------------------------- helpers

    // The words that mean a connection: the types themselves and the namespaces that hold them. A comment may use them, code may not.
    [GeneratedRegex(@"\b(HttpClient|HttpClientHandler|SocketsHttpHandler|HttpMessageHandler|HttpRequestMessage|HttpWebRequest|WebRequest|WebClient|TcpClient|UdpClient|TcpListener|Socket|NetworkStream|ClientWebSocket|System\.Net\.Http|System\.Net\.Sockets|System\.Net\.WebSockets|System\.Net\.Security)\b")]
    private static partial Regex NetworkWord();

    private static string WithoutComments(string line)
    {
        var at = line.IndexOf("//", StringComparison.Ordinal);
        return at < 0 ? line : line[..at];
    }

    private static bool IsBuildOutput(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        return relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PcMieru.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("PcMieru.slnx not found above " + AppContext.BaseDirectory);
    }
}
