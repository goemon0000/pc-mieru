using System.Text.RegularExpressions;
using PcMieru.Core.Reporting;

namespace PcMieru.Cli.Tests.Reporting;

public partial class KnownProcessCatalogTests
{
    private static readonly Dictionary<string, string> Env = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemRoot"] = @"C:\Windows",
        ["ProgramFiles"] = @"C:\Program Files",
        ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
        ["ProgramData"] = @"C:\ProgramData",
        ["LocalAppData"] = @"C:\Users\alice\AppData\Local",
        ["AppData"] = @"C:\Users\alice\AppData\Roaming",
    };

    private static readonly KnownProcessCatalog Catalog = new(KnownProcessData.Json, name => Env.GetValueOrDefault(name));

    private static readonly SignatureInfo Windows = new(SignatureState.SignedCatalog, "Microsoft Windows");

    // ---- the catalog data itself ------------------------------------------------------------------------------

    [Fact]
    public void The_catalog_has_about_sixty_entries_with_unique_ids()
    {
        Assert.InRange(Catalog.Count, 55, 90);
        Assert.Equal(Catalog.Count, Catalog.Ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_entry_has_plain_text_in_both_languages()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(KnownProcessData.Json)!;
        foreach (var entry in root["entries"]!.AsArray())
        {
            var id = entry!["id"]!.GetValue<string>();
            var ja = entry["ja"]!.GetValue<string>();
            var en = entry["en"]!.GetValue<string>();
            Assert.True(ja.Length is >= 5 and <= 120, $"{id}: ja text length {ja.Length}");
            Assert.True(en.Length is >= 8 and <= 160, $"{id}: en text length {en.Length}");
            Assert.True(JapaneseRegex().IsMatch(ja), $"{id}: ja text has no Japanese");
            Assert.False(JapaneseRegex().IsMatch(en), $"{id}: en text has Japanese");
            Assert.EndsWith("。", ja, StringComparison.Ordinal);
            Assert.False(Regex.IsMatch(ja + en, @"安全です|危険|ウイルスではありません|safe\b|harmless|malware", RegexOptions.IgnoreCase), $"{id}: says more than we can measure");
        }
    }

    [Fact]
    public void An_entry_with_a_file_names_its_signer_and_an_entry_without_one_does_not()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(KnownProcessData.Json)!;
        var groups = root["signerGroups"]!.AsObject();
        foreach (var entry in root["entries"]!.AsArray())
        {
            var id = entry!["id"]!.GetValue<string>();
            var paths = entry["paths"]!.AsArray();
            var signers = entry["signers"];
            if (paths.Count == 0)
            {
                Assert.True(signers is System.Text.Json.Nodes.JsonArray { Count: 0 }, $"{id}: no file but a signer is required");
            }
            else
            {
                var group = signers as System.Text.Json.Nodes.JsonValue;
                Assert.True(group is not null && groups.ContainsKey(group.GetValue<string>()), $"{id}: a file must name a known signer group");
            }
        }
    }

    [Fact]
    public void Every_listed_path_matches_itself_so_no_pattern_is_dead()
    {
        foreach (var id in Catalog.Ids)
        {
            var patterns = Catalog.PathPatterns(id);
            var name = NameOf(id);
            foreach (var pattern in patterns)
            {
                var concrete = Regex.Replace(pattern, "%([^%]+)%", m => Env[m.Groups[1].Value]).Replace("**", "x", StringComparison.Ordinal).Replace("*", "x", StringComparison.Ordinal);
                var match = Catalog.Match(name, concrete, null);
                Assert.True(match is { PathMatches: true }, $"{id}: '{pattern}' does not match its own concrete path {concrete}");
            }
        }
    }

    private static string NameOf(string id)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(KnownProcessData.Json)!;
        var entry = root["entries"]!.AsArray().First(e => e!["id"]!.GetValue<string>() == id)!;
        return entry["names"]![0]!.GetValue<string>();
    }

    // ---- matching ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_real_svchost_matches_on_name_path_and_signer()
    {
        var known = Catalog.Match("svchost.exe", @"C:\Windows\System32\svchost.exe", Windows);
        Assert.NotNull(known);
        Assert.Equal("svchost", known.Id);
        Assert.True(known.PathMatches);
        Assert.True(known.SignerMatches);
        Assert.Contains("サービス", known.DescriptionJa, StringComparison.Ordinal);
        Assert.Contains("services", known.DescriptionEn, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SVCHOST.EXE", @"c:\windows\SYSTEM32\SvcHost.exe")]
    [InlineData("svchost.exe", @"C:\Windows\System32\svchost.exe")]
    [InlineData("svchost.exe", @"C:/Windows/System32/svchost.exe")]
    [InlineData("svchost.exe", @"\\?\C:\Windows\System32\svchost.exe")]
    [InlineData("svchost.exe", @"C:\Windows\SysWOW64\svchost.exe")]
    [InlineData(@"C:\Windows\System32\svchost.exe", @"C:\Windows\System32\svchost.exe")]
    public void Case_slashes_the_device_prefix_and_the_32_bit_folder_do_not_matter(string name, string path)
    {
        var known = Catalog.Match(name, path, Windows);
        Assert.NotNull(known);
        Assert.True(known.PathMatches);
    }

    [Theory]
    [InlineData(@"C:\Users\alice\AppData\Local\Temp\svchost.exe")]
    [InlineData(@"C:\Windows\svchost.exe")]
    [InlineData(@"C:\Windows\System32\drivers\svchost.exe")]
    [InlineData(@"C:\Windows\System32\svchost.exe.bak\svchost.exe")]
    [InlineData(@"D:\Windows\System32\svchost.exe")]
    [InlineData(@"C:\Windows\System32x\svchost.exe")]
    public void A_svchost_in_the_wrong_place_says_the_place_is_not_normal(string path)
    {
        var known = Catalog.Match("svchost.exe", path, Windows);
        Assert.NotNull(known);
        Assert.False(known.PathMatches);
        Assert.StartsWith("場所が普通ではありません", known.DescriptionJa, StringComparison.Ordinal);
        Assert.StartsWith("Unusual location", known.DescriptionEn, StringComparison.Ordinal);
        Assert.Contains(@"%SystemRoot%\System32\svchost.exe", known.DescriptionJa, StringComparison.Ordinal);
        Assert.Contains("候補", known.DescriptionJa, StringComparison.Ordinal);
        Assert.DoesNotContain("{", known.DescriptionJa + known.DescriptionEn, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_in_the_catalog_with_no_known_path_is_not_matched_on_the_name_alone()
    {
        Assert.Null(Catalog.Match("svchost.exe", null, Windows));
        Assert.Null(Catalog.Match("svchost.exe", "  ", Windows));
        Assert.Null(Catalog.Match("chrome.exe", null, null));
    }

    [Theory]
    [InlineData("totally-unknown.exe")]
    [InlineData("svchost")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_name_that_is_not_listed_gets_no_answer(string name)
    {
        Assert.Null(Catalog.Match(name, @"C:\Windows\System32\svchost.exe", Windows));
    }

    [Fact]
    public void A_matching_place_with_the_wrong_signature_says_so_only_when_a_signature_was_measured()
    {
        const string path = @"C:\Windows\System32\svchost.exe";

        foreach (var wrong in new SignatureInfo[]
        {
            new(SignatureState.Unsigned, null),
            new(SignatureState.Invalid, "Microsoft Windows"),
            new(SignatureState.SignedEmbedded, "Shady Software Ltd."),
        })
        {
            var known = Catalog.Match("svchost.exe", path, wrong);
            Assert.NotNull(known);
            Assert.True(known.PathMatches);
            Assert.False(known.SignerMatches);
            Assert.StartsWith("署名が普通と違います", known.DescriptionJa, StringComparison.Ordinal);
            Assert.StartsWith("The signature is not the usual one", known.DescriptionEn, StringComparison.Ordinal);
            Assert.Contains("Microsoft", known.DescriptionJa, StringComparison.Ordinal);
        }

        var unknown = Catalog.Match("svchost.exe", path, null);
        Assert.NotNull(unknown);
        Assert.True(unknown.PathMatches);
        Assert.False(unknown.SignerMatches);
        Assert.DoesNotContain("署名が普通と違います", unknown.DescriptionJa, StringComparison.Ordinal);
        Assert.Contains("サービス", unknown.DescriptionJa, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Microsoft Windows")]
    [InlineData("Microsoft Corporation")]
    [InlineData("microsoft corporation")]
    [InlineData("CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US")]
    [InlineData("CN=\"Microsoft Corporation\", OU=MOPR, O=Microsoft Corporation, L=Redmond, S=Washington, C=US")]
    public void The_signer_is_read_from_a_bare_name_or_a_full_subject(string signer)
    {
        var known = Catalog.Match("svchost.exe", @"C:\Windows\System32\svchost.exe", new SignatureInfo(SignatureState.SignedEmbedded, signer));
        Assert.NotNull(known);
        Assert.True(known.SignerMatches);
    }

    [Fact]
    public void A_look_alike_publisher_is_not_the_publisher()
    {
        var known = Catalog.Match("svchost.exe", @"C:\Windows\System32\svchost.exe", new SignatureInfo(SignatureState.SignedEmbedded, "Microsoft Corporation Fake Ltd"));
        Assert.NotNull(known);
        Assert.False(known.SignerMatches);
    }

    [Fact]
    public void Windows_items_without_a_file_match_only_when_there_really_is_no_file()
    {
        foreach (var name in new[] { "System", "Registry", "Memory Compression", "System Idle Process", "vmmem", "Secure System" })
        {
            var known = Catalog.Match(name, null, null);
            Assert.NotNull(known);
            Assert.True(known.PathMatches);
            Assert.True(known.SignerMatches);
        }

        var fake = Catalog.Match("Registry", @"C:\Users\alice\Downloads\Registry", null);
        Assert.NotNull(fake);
        Assert.False(fake.PathMatches);
        Assert.StartsWith("場所が普通ではありません", fake.DescriptionJa, StringComparison.Ordinal);
    }

    [Fact]
    public void Steam_and_its_helper_match_in_the_real_install_folder()
    {
        var valve = new SignatureInfo(SignatureState.SignedEmbedded, "Valve Corp.");
        Assert.True(Catalog.Match("steam.exe", @"C:\Program Files (x86)\Steam\steam.exe", valve)!.SignerMatches);
        Assert.True(Catalog.Match("steamwebhelper.exe", @"C:\Program Files (x86)\Steam\bin\cef\cef.win64\steamwebhelper.exe", valve)!.PathMatches);
        Assert.False(Catalog.Match("steam.exe", @"C:\Users\alice\Downloads\steam.exe", valve)!.PathMatches);
    }

    [Fact]
    public void A_per_user_install_resolves_the_users_folder_from_the_environment()
    {
        var discord = new SignatureInfo(SignatureState.SignedEmbedded, "Discord Inc.");
        Assert.True(Catalog.Match("Discord.exe", @"C:\Users\alice\AppData\Local\Discord\app-1.0.9190\Discord.exe", discord)!.PathMatches);
        Assert.False(Catalog.Match("Discord.exe", @"C:\Users\bob\AppData\Local\Discord\app-1.0.9190\Discord.exe", discord)!.PathMatches);
    }

    [Fact]
    public void A_variable_that_is_not_set_drops_the_pattern_instead_of_matching_everything()
    {
        var bare = new KnownProcessCatalog(KnownProcessData.Json, _ => null);
        var known = bare.Match("svchost.exe", @"C:\anywhere\svchost.exe", Windows);
        Assert.NotNull(known);
        Assert.False(known.PathMatches);
    }

    [Fact]
    public void A_broken_catalog_is_refused_loudly()
    {
        Assert.Throws<FormatException>(() => new KnownProcessCatalog("""{ "entries": [ { "id": "a", "names": ["x.exe"], "paths": [], "signers": [], "ja": "あ", "en": "a" }, { "id": "b", "names": ["X.EXE"], "paths": [], "signers": [], "ja": "あ", "en": "a" } ] }""", _ => null));
        Assert.Throws<FormatException>(() => new KnownProcessCatalog("""{ "entries": [ { "id": "a", "names": ["x.exe"], "paths": ["%X%"], "signers": "nobody", "ja": "あ", "en": "a" } ] }""", _ => null));
        Assert.Throws<FormatException>(() => new KnownProcessCatalog("""{ "entries": [ { "id": "a", "names": ["x.exe"], "paths": [], "signers": [], "en": "a" } ] }""", _ => null));
    }

    [GeneratedRegex(@"[\u3040-\u30FF\u4E00-\u9FFF]")]
    private static partial Regex JapaneseRegex();
}
