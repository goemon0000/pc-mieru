using System.Text.Json.Nodes;
using PcMieru.Core.Reporting;

namespace PcMieru.Cli.Tests.Reporting;

/// <summary>
/// The report builder renders a built document either way without sampling again, and the "redacted" it writes is what
/// it really did: the check box on the GUI page and the CLI use the same two methods.
/// </summary>
public class ReportRenderTests
{
    private static ReportBuilder Builder(bool redactorEnabled, out PcMieruServices services)
    {
        services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
        return new ReportBuilder(
            services.Get<ISnapshotSource>(),
            services.Observations,
            services.Get<IHardwareProvider>(),
            services.Get<IStorageHealthProvider>(),
            new SimpleRedactor(new RedactionContext("alice", "ALICE-PC", @"C:\Users\alice"), redactorEnabled),
            static () => new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc));
    }

    private static JsonNode FirstVolume(JsonNode document) => document["hardware"]!["storage"]!["value"]![0]!["volumes"]![0]!;

    [Fact]
    public async Task Json_is_redacted_by_place_and_says_so_when_asked_and_allowed()
    {
        var builder = Builder(true, out var services);
        using (services)
        {
            var document = await builder.BuildAsync(new ReportOptions(0));

            var hidden = builder.RenderJson(document, redact: true);
            Assert.True(hidden["redacted"]!.GetValue<bool>());
            Assert.Equal("<redacted>", FirstVolume(hidden)["label"]!.GetValue<string>());
            Assert.Equal("<redacted>", hidden["hardware"]!["audio"]!["value"]![0]!["name"]!["value"]!.GetValue<string>());
            Assert.Equal("<redacted>", hidden["hardware"]!["usb"]!["value"]![0]!["name"]!["value"]!.GetValue<string>());

            // the same document, rendered again without redaction: nothing was changed in place
            var shown = builder.RenderJson(document, redact: false);
            Assert.False(shown["redacted"]!.GetValue<bool>());
            Assert.Equal("System", FirstVolume(shown)["label"]!.GetValue<string>());
            Assert.Equal("Speakers (Fake Audio)", shown["hardware"]!["audio"]!["value"]![0]!["name"]!["value"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task The_live_nic_friendly_name_is_blanked_in_both_renders_when_redacting()
    {
        // The NIC friendly name in the summary is a Windows InterfaceAlias a user can rename to their own name, and it is
        // the one live free-text name the report prints. It is summary-scoped (not the hardware model's network[].name),
        // so neither the path rules nor the content pass reach it: the builder must blank it at render time.
        var builder = Builder(true, out var services);
        using (services)
        {
            var document = await builder.BuildAsync(new ReportOptions(0));
            var realName = document.Summary.System.Nics.Single(n => n.IsUp).Name;
            Assert.False(string.IsNullOrWhiteSpace(realName));

            var hiddenJson = builder.RenderJson(document, redact: true);
            var nics = (JsonArray)hiddenJson["summary"]!["system"]!["nics"]!;
            Assert.NotEmpty(nics);
            foreach (var nic in nics)
            {
                Assert.Equal("<redacted>", nic!["name"]!.GetValue<string>());
            }

            var hiddenMd = builder.RenderMarkdown(document, Language.En, redact: true);
            Assert.DoesNotContain(realName, hiddenMd, StringComparison.Ordinal);
            Assert.Contains("<redacted>", hiddenMd, StringComparison.Ordinal);

            // the same document, rendered again without redaction: the real name is intact in place (blanking is not destructive)
            var shownJson = builder.RenderJson(document, redact: false);
            Assert.Equal(realName, ((JsonArray)shownJson["summary"]!["system"]!["nics"]!).Select(n => n!["name"]!.GetValue<string>()).First(name => name == realName));
            var shownMd = builder.RenderMarkdown(document, Language.En, redact: false);
            Assert.Contains(realName, shownMd, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_box_that_is_on_cannot_override_no_redact()
    {
        var builder = Builder(false, out var services);
        using (services)
        {
            var document = await builder.BuildAsync(new ReportOptions(0));
            Assert.False(document.Redacted);

            var json = builder.RenderJson(document, redact: true);
            Assert.False(json["redacted"]!.GetValue<bool>());
            Assert.Equal("System", FirstVolume(json)["label"]!.GetValue<string>());

            var yes = Strings.Get("report.header.redactedYes", Language.En);
            var no = Strings.Get("report.header.redactedNo", Language.En);
            var markdown = builder.RenderMarkdown(document, Language.En, redact: true);
            Assert.Contains(no, markdown, StringComparison.Ordinal);
            Assert.DoesNotContain(yes, markdown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Markdown_header_follows_what_was_done_and_the_version_is_passed_through()
    {
        var builder = Builder(true, out var services);
        using (services)
        {
            var document = await builder.BuildAsync(new ReportOptions(0));
            var yes = Strings.Get("report.header.redactedYes", Language.En);
            var no = Strings.Get("report.header.redactedNo", Language.En);

            var hidden = builder.RenderMarkdown(document, Language.En, redact: true, "9.9.9");
            Assert.Contains(yes, hidden, StringComparison.Ordinal);
            Assert.Contains("9.9.9", hidden, StringComparison.Ordinal);

            var shown = builder.RenderMarkdown(document, Language.En, redact: false);
            Assert.Contains(no, shown, StringComparison.Ordinal);
            Assert.DoesNotContain(yes, shown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_implementer_that_does_not_render_says_so_instead_of_leaking()
    {
        IReportBuilder bare = new BareBuilder();
        Assert.Throws<NotSupportedException>(() => bare.RenderJson(null!, redact: true));
        Assert.Throws<NotSupportedException>(() => bare.RenderMarkdown(null!, Language.En, redact: true));
    }

    private sealed class BareBuilder : IReportBuilder
    {
        public Task<ReportDocument> BuildAsync(ReportOptions options, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
