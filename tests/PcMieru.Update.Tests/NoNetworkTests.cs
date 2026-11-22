using System.Text.RegularExpressions;
using PcMieru.Update.Bundle;

namespace PcMieru.Update.Tests;

/// <summary>
/// PC Mieru reaches the network in exactly the places it says, and this class is the list (DESIGN-B 3.1 and 8). Step 1 had "no network in PcMieru.Update"; step 2 adds the release feed, so
/// the rule became an allow-list that is read from the source of every project:
/// <list type="bullet">
/// <item>The HTTP library (<c>System.Net.Http</c>) is named by one file, <c>src/PcMieru.Update/Feed/UpdateHttp.cs</c>, and by nothing else. The Cli (and so the MCP server) cannot reach it: it does
/// not reference the Update project.</item>
/// <item>A raw socket is named by one file, the smoke run's loopback load for the network meter, which binds 127.0.0.1 and nothing else.</item>
/// <item>No <c>WebRequest</c>, <c>WebClient</c>, <c>HttpWebRequest</c>, <c>WebSocket</c> or <c>HttpListener</c> anywhere.</item>
/// <item>Brotli is never enabled: <c>Directory.Build.props</c> suppresses an audit advisory on the Brotli decoder on exactly that promise, and the one handler keeps AutomaticDecompression at None
/// (a test of <c>FeedHttpTests</c> reads that from the real handler, and this one reads the line).</item>
/// </list>
/// A comment may use any of the words; code may not (outside the file that is allowed it).
/// </summary>
public partial class NoNetworkTests
{
    private const string HttpFile = "src/PcMieru.Update/Feed/UpdateHttp.cs";
    private const string LoopbackFile = "src/PcMieru.Gui/Smoke/SmokeRun.NetMeter.cs";
    private const string UpdateServiceFile = "src/PcMieru.Update/Feed/UpdateService.cs";

    [Fact]
    public void The_update_assembly_references_the_http_library_and_no_other_network_assembly()
    {
        var references = typeof(BundleZip).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();
        Assert.NotEmpty(references); // the list is really the assembly's, not an empty one

        // Anything called System.Net.* is a network assembly. The feed needs the HTTP client and the types it shares with the basics (status codes, decompression flags);
        // sockets, TLS options, name resolution, mail, ping, web sockets and listeners are not needed and not allowed.
        var network = references.Where(n => n.StartsWith("System.Net", StringComparison.OrdinalIgnoreCase)).ToArray();
        var unexpected = network.Where(n => !AllowedNetworkAssemblies.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray();
        Assert.True(unexpected.Length == 0, "PcMieru.Update references " + string.Join(", ", unexpected));
        Assert.Contains("System.Net.Http", network); // the feed is here; a check that cannot see it proves nothing
        Assert.Contains("System.IO.Compression", references);
    }

    private static readonly string[] AllowedNetworkAssemblies = ["System.Net.Http", "System.Net.Primitives"];

    [Fact]
    public void Only_the_feeds_http_file_names_the_http_library()
    {
        var hits = Scan(HttpWords());

        Assert.True(hits.Count > 0, "the scan did not see UpdateHttp.cs at all");
        var elsewhere = hits.Where(h => h.File != HttpFile).Select(h => h.ToString()).ToArray();
        Assert.True(elsewhere.Length == 0, "the HTTP library is named outside " + HttpFile + ":\n" + string.Join("\n", elsewhere));
    }

    [Fact]
    public void The_http_client_of_the_feed_is_made_in_exactly_one_place_the_update_service()
    {
        // M10: every client the app makes is counted and handed the service's clock (Retry-After). A second place that makes one would skip that. Tests make theirs through the
        // service's TransportFactory or in tests/, which this scan does not read.
        var hits = Scan(NewUpdateHttp());

        Assert.True(hits.Count > 0, "the scan did not see the one place that makes the client");
        var elsewhere = hits.Where(h => h.File != UpdateServiceFile).Select(h => h.ToString()).ToArray();
        Assert.True(elsewhere.Length == 0, "an UpdateHttp is made outside " + UpdateServiceFile + ":\n" + string.Join("\n", elsewhere));
        Assert.Matches(NewUpdateHttp(), WithoutComments("    using var t = new UpdateHttp(version);")); // the pattern does see a construction
        Assert.DoesNotMatch(NewUpdateHttp(), WithoutComments("    // new UpdateHttp(version)"));
    }

    [Fact]
    public void No_project_names_a_web_request_a_web_client_a_web_socket_or_a_listener()
    {
        var hits = Scan(NeverWords());

        Assert.True(hits.Count == 0, "a network type that nothing may use:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void Raw_sockets_are_named_only_by_the_smoke_runs_loopback_load_and_it_stays_on_127_0_0_1()
    {
        var hits = Scan(SocketWords());

        Assert.True(hits.Count > 0, "the scan did not see " + LoopbackFile + " at all");
        var elsewhere = hits.Where(h => h.File != LoopbackFile).Select(h => h.ToString()).ToArray();
        Assert.True(elsewhere.Length == 0, "a raw socket outside " + LoopbackFile + ":\n" + string.Join("\n", elsewhere));

        var code = string.Join('\n', File.ReadAllLines(Path.Combine(RepoRoot(), LoopbackFile)).Select(WithoutComments));
        Assert.Contains("IPAddress.Loopback", code, StringComparison.Ordinal);
        Assert.DoesNotMatch(NotLoopback(), code);
    }

    [Fact]
    public void Only_the_gui_and_the_update_project_itself_reference_the_update_project()
    {
        // The Cli (so the MCP server), Core, Checks, the Helper, the NetMeter, the SensorHost and the Wire cannot reach the feed: the reference does not exist.
        var src = Path.Combine(RepoRoot(), "src");
        var projects = Directory.GetFiles(src, "*.csproj", SearchOption.AllDirectories);
        Assert.True(projects.Length >= 8, $"only {projects.Length} projects found");

        var referencing = projects
            .Where(p => File.ReadAllText(p).Contains("PcMieru.Update", StringComparison.Ordinal))
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["PcMieru.Gui", "PcMieru.Update"], referencing);
    }

    [Fact]
    public void The_update_project_references_no_package_and_no_project_but_the_wire_project_and_the_wire_reaches_nothing()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PcMieru.Update", "PcMieru.Update.csproj"));
        Assert.DoesNotContain("<PackageReference", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<FrameworkReference", text, StringComparison.Ordinal);

        // The one project it may reference is the Wire: the gate file's fixed name and bytes live there (DESIGN-B 4), and the Wire is a leaf. What it means here: nothing that can reach the
        // network comes in through that reference (no package, no project of its own, no System.Net in its code).
        var references = System.Text.RegularExpressions.Regex.Matches(text, @"<ProjectReference\s+Include=""([^""]+)""").Select(m => m.Groups[1].Value.Replace('\\', '/')).ToArray();
        Assert.Equal(["../PcMieru.Wire/PcMieru.Wire.csproj"], references);

        var wire = Path.Combine(RepoRoot(), "src", "PcMieru.Wire");
        var wireProject = File.ReadAllText(Path.Combine(wire, "PcMieru.Wire.csproj"));
        Assert.DoesNotContain("<PackageReference", wireProject, StringComparison.Ordinal);
        Assert.DoesNotContain("<ProjectReference", wireProject, StringComparison.Ordinal);
        var sources = Directory.GetFiles(wire, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(sources.Length > 5, "the Wire sources were not found");
        foreach (var source in sources)
        {
            var code = File.ReadAllText(source);
            Assert.DoesNotContain("System.Net", code, StringComparison.Ordinal);
            Assert.DoesNotContain("HttpClient", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Brotli_is_never_named_in_code_and_no_automatic_decompression_is_enabled()
    {
        var hits = Scan(BrotliWords());
        Assert.True(hits.Count == 0, "Brotli or an automatic decompression in code:\n" + string.Join("\n", hits));

        // Every assignment of AutomaticDecompression, anywhere, says None. The scan above would not see a different spelling of the same thing, so the assignments are read too.
        var assignments = Scan(AutomaticDecompressionAssignment());
        Assert.True(assignments.Count > 0, "no AutomaticDecompression assignment found: the handler is not where this test looks");
        Assert.All(assignments, h => Assert.Matches(NoneAssignment(), h.Code));
        Assert.All(assignments, h => Assert.Equal(HttpFile, h.File));
    }

    [Fact]
    public void The_handler_of_the_feed_is_made_with_no_redirect_no_cookie_no_credential_and_no_decompression()
    {
        var code = string.Join('\n', File.ReadAllLines(Path.Combine(RepoRoot(), HttpFile)).Select(WithoutComments));

        Assert.Contains("AllowAutoRedirect = false", code, StringComparison.Ordinal);
        Assert.Contains("UseCookies = false", code, StringComparison.Ordinal);
        Assert.Contains("AutomaticDecompression = DecompressionMethods.None", code, StringComparison.Ordinal);
        Assert.Contains("PreAuthenticate = false", code, StringComparison.Ordinal);
        Assert.Contains("Credentials = null", code, StringComparison.Ordinal);
        Assert.DoesNotContain("UseDefaultCredentials", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CookieContainer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpMethod.Post", code, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpMethod.Put", code, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpMethod.Delete", code, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpMethod.Patch", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Cookie", code.Replace("UseCookies", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal); // the one word allowed is the switch that turns them off
    }

    [Fact]
    public void The_brotli_remark_in_the_build_props_says_what_is_true_now()
    {
        // The audit suppression stands on "PC Mieru never decompresses Brotli". The remark must say what reads archives (Deflate and Stored only), where the one HttpClient is and
        // what it keeps, and what must not be done while the suppression stands.
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        Assert.Contains("PC Mieru never decompresses Brotli.", text, StringComparison.Ordinal);
        Assert.Contains("Deflate and Stored only", text, StringComparison.Ordinal);
        Assert.Contains("src/PcMieru.Update/Feed/UpdateHttp.cs", text, StringComparison.Ordinal);
        Assert.Contains("AutomaticDecompression at None", text, StringComparison.Ordinal);
        Assert.Contains("Do not enable any automatic decompression while it stands.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("no HttpClient in step 1", text, StringComparison.Ordinal);   // false since step 2
        Assert.DoesNotContain("no archive reading", text, StringComparison.Ordinal);
        Assert.Contains("NuGetAuditSuppress Include=\"https://github.com/advisories/GHSA-p3jp-9v83-76w9\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_source_checks_do_see_what_they_are_looking_for()
    {
        // A check that cannot fail proves nothing: the patterns and the comment stripping, on lines that do and do not count.
        Assert.Matches(HttpWords(), WithoutComments("    using var client = new HttpClient();"));
        Assert.Matches(HttpWords(), WithoutComments("using System.Net.Http;"));
        Assert.Matches(HttpWords(), WithoutComments("    var h = new SocketsHttpHandler();"));
        Assert.Matches(HttpWords(), WithoutComments("    catch (HttpRequestException) { }"));
        Assert.DoesNotMatch(HttpWords(), WithoutComments("    throw new UpdateHttpException(reason);"));
        Assert.DoesNotMatch(HttpWords(), WithoutComments("    // step 2 adds an HttpClient"));
        Assert.DoesNotMatch(HttpWords(), WithoutComments("    /// the HttpClient of step 2"));

        Assert.Matches(NeverWords(), WithoutComments("    WebRequest.Create(url); // fetch"));
        Assert.Matches(NeverWords(), WithoutComments("    var c = new WebClient();"));
        Assert.Matches(NeverWords(), WithoutComments("    using var l = new HttpListener();"));
        Assert.DoesNotMatch(NeverWords(), WithoutComments("    WebRequested += OnRequested;"));   // an event of the web view, not a request

        Assert.Matches(SocketWords(), WithoutComments("using System.Net.Sockets;"));
        Assert.Matches(SocketWords(), WithoutComments("    using var l = new TcpListener(IPAddress.Loopback, 0);"));
        Assert.Matches(SocketWords(), WithoutComments("    var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);"));
        Assert.DoesNotMatch(SocketWords(), WithoutComments("    Socket: Probe.Text(source, value, reason),"));      // the CPU socket of the hardware page
        Assert.DoesNotMatch(SocketWords(), WithoutComments("    var x = 1; // a Socket"));

        Assert.Matches(BrotliWords(), WithoutComments("    using var b = new BrotliStream(s, CompressionMode.Decompress);"));
        Assert.Matches(BrotliWords(), WithoutComments("    AutomaticDecompression = DecompressionMethods.All,"));
        Assert.Matches(BrotliWords(), WithoutComments("    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli,"));
        Assert.DoesNotMatch(BrotliWords(), WithoutComments("    AutomaticDecompression = DecompressionMethods.None,"));
        Assert.DoesNotMatch(BrotliWords(), WithoutComments("    /// so Brotli is never asked for"));

        Assert.Matches(AutomaticDecompressionAssignment(), "    AutomaticDecompression = DecompressionMethods.None,");
        Assert.Matches(AutomaticDecompressionAssignment(), "    handler.AutomaticDecompression=DecompressionMethods.All;");
        Assert.Matches(NoneAssignment(), "    AutomaticDecompression = DecompressionMethods.None,");
        Assert.DoesNotMatch(NoneAssignment(), "    AutomaticDecompression = DecompressionMethods.All,");

        Assert.Matches(NotLoopback(), "var a = IPAddress.Any;");
        Assert.Matches(NotLoopback(), "var a = IPAddress.Parse(\"10.0.0.1\");");
        Assert.Matches(NotLoopback(), "await Dns.GetHostEntryAsync(name);");
        Assert.DoesNotMatch(NotLoopback(), "var a = IPAddress.Loopback;");

        // A "//" inside a string is not a comment: the rest of the line is still code.
        Assert.Matches(SocketWords(), WithoutComments("    var u = \"https://example.invalid\"; var c = new TcpClient();"));
        Assert.DoesNotMatch(SocketWords(), WithoutComments("    var u = \"https://example.invalid\"; // new TcpClient()"));
    }

    // ------------------------------------------------------------------------------------------------------------------------- helpers

    private readonly record struct Hit(string File, int Line, string Code)
    {
        public override string ToString() => $"{File}:{Line}: {Code.Trim()}";
    }

    // The HTTP library: its types and its namespace. UpdateHttp, UpdateHttpException and IFeedTransport have none of these as a whole word.
    [GeneratedRegex(@"\b(HttpClient|HttpClientHandler|SocketsHttpHandler|HttpMessageHandler|DelegatingHandler|HttpRequestMessage|HttpResponseMessage|HttpRequestException|HttpMethod|HttpContent|System\.Net\.Http)\b")]
    private static partial Regex HttpWords();

    [GeneratedRegex(@"\bnew\s+UpdateHttp\s*\(")]
    private static partial Regex NewUpdateHttp();

    // Types that nothing in this repo may use, wherever it is.
    [GeneratedRegex(@"\b(WebRequest|HttpWebRequest|FtpWebRequest|WebClient|ServicePointManager|WebSocket|ClientWebSocket|HttpListener|System\.Net\.WebSockets|System\.Net\.Mail|SmtpClient)\b")]
    private static partial Regex NeverWords();

    // Raw sockets. "Socket" alone is a word of the hardware page (the CPU socket), so it counts only as a type being made or as the namespace.
    [GeneratedRegex(@"\b(TcpClient|TcpListener|UdpClient|NetworkStream|SocketException|SocketType|ProtocolType|AddressFamily|System\.Net\.Sockets)\b|\bnew\s+Socket\s*\(")]
    private static partial Regex SocketWords();

    // Brotli and every automatic decompression that is not None (Brotli is in All).
    [GeneratedRegex(@"\b(BrotliStream|BrotliDecoder|BrotliEncoder|BrotliCompressionOptions)\b|DecompressionMethods\s*\.\s*(All|Brotli|GZip|Deflate)\b")]
    private static partial Regex BrotliWords();

    [GeneratedRegex(@"\bAutomaticDecompression\s*=")]
    private static partial Regex AutomaticDecompressionAssignment();

    [GeneratedRegex(@"\bAutomaticDecompression\s*=\s*DecompressionMethods\s*\.\s*None\b")]
    private static partial Regex NoneAssignment();

    // What a loopback-only file must not contain: any other address, a name lookup, or a way to listen on every interface.
    [GeneratedRegex(@"IPAddress\s*\.\s*(Any|IPv6Any|Parse|TryParse|Broadcast)|\bDns\s*\.|\bIPv6Loopback\b.*\bAny\b")]
    private static partial Regex NotLoopback();

    /// <summary>Every code line (comments removed) of every .cs file of src/ and tools/ that matches <paramref name="pattern"/>.</summary>
    private static List<Hit> Scan(Regex pattern)
    {
        var root = RepoRoot();
        var files = new[] { "src", "tools" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !IsBuildOutput(root, f))
            .ToArray();
        Assert.True(files.Length >= 100, $"only {files.Length} source files found below {root}");

        var hits = new List<Hit>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = WithoutComments(lines[i]);
                if (pattern.IsMatch(code))
                {
                    hits.Add(new Hit(relative, i + 1, code));
                }
            }
        }

        return hits;
    }

    /// <summary>The line without its "//" comment. A "//" inside a string literal is not a comment.</summary>
    private static string WithoutComments(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                return line[..i];
            }
        }

        return line;
    }

    private static bool IsBuildOutput(string root, string file)
    {
        var parts = Path.GetRelativePath(root, file).Replace('\\', '/').Split('/');
        return parts.Contains("obj", StringComparer.Ordinal) || parts.Contains("bin", StringComparer.Ordinal);
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
