using System.Text.Json.Nodes;
using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Reporting;

/// <summary>The data side of the report: summary, hardware, storage health. Redaction happens at the output edge.</summary>
public sealed class ReportBuilder : IReportBuilder
{
    /// <summary>Longest observation a report may ask for (SPEC section 10 limits the MCP side the same way).</summary>
    public const int MaxDurationSec = 30;

    private readonly ISnapshotSource _snapshots;
    private readonly IObservationEngine _observations;
    private readonly IHardwareProvider _hardware;
    private readonly IStorageHealthProvider _storage;
    private readonly IRedactor _redactor;
    private readonly Func<DateTime> _clock;
    private readonly SamplerLoop? _sampler;

    /// <param name="sampler">When given and running, the observation window is read from its history instead of sampling again (two samplers on one source shorten each other's deltas).</param>
    public ReportBuilder(
        ISnapshotSource snapshots,
        IObservationEngine observations,
        IHardwareProvider hardware,
        IStorageHealthProvider storage,
        IRedactor redactor,
        Func<DateTime> clock,
        SamplerLoop? sampler = null)
    {
        _snapshots = snapshots;
        _observations = observations;
        _hardware = hardware;
        _storage = storage;
        _redactor = redactor;
        _clock = clock;
        _sampler = sampler;
    }

    public async Task<ReportDocument> BuildAsync(ReportOptions options, CancellationToken ct = default)
    {
        var health = options.IncludeStorageHealth ? await _storage.ReadAsync(false, ct).ConfigureAwait(false) : null;
        // The report says how long it really observed, so the number written is the clamped one.
        var seconds = Math.Clamp(options.DurationSec, 0, MaxDurationSec);
        var (_, summary) = await ObservationRun.RunAsync(_snapshots, _observations, TimeSpan.FromSeconds(seconds), health, ct, _sampler).ConfigureAwait(false);
        var hardware = options.IncludeHardware ? await _hardware.GetAsync(HardwareCategory.All, ct).ConfigureAwait(false) : null;
        return new ReportDocument(
            ReportDocument.CurrentSchemaVersion,
            _clock(),
            _redactor.Enabled,
            seconds,
            summary,
            hardware,
            health is null ? null : new StorageHealthReport(StorageHealthReport.CurrentSchemaVersion, health));
    }

    /// <summary>Hidden only when asked AND the process allows it; what is written in the header is what was done.</summary>
    public string RenderMarkdown(ReportDocument document, Language language, bool redact, string? appVersion = null)
    {
        var effective = redact && _redactor.Enabled;
        // The model has no serial, MAC, IP, SSID, command line, volume label, audio or USB name in the Markdown at all
        // (ReportMarkdown never prints them). The one live free-text name it DOES print is the NIC friendly name
        // (an InterfaceAlias a user can rename to their own name), so blank it in the graph; the final pass hides the rest.
        var prepared = Prepare(document, effective);
        var markdown = ReportMarkdown.Render(prepared, language, appVersion);
        return _redactor.RedactText(markdown, effective);
    }

    public JsonNode RenderJson(ReportDocument document, bool redact)
    {
        var effective = redact && _redactor.Enabled;
        var prepared = Prepare(document, effective);
        var node = PcMieruJson.ToNode(prepared) ?? new JsonObject();
        return _redactor.RedactJson(node, effective) ?? node;
    }

    /// <summary>
    /// Stamps the chosen redaction flag and, when redacting, blanks the live NIC friendly name in the summary.
    /// The hardware model's adapter names go through <c>BlankedPaths</c> (<c>network[].name</c>); the summary's live
    /// <c>SystemInfo.Nics[].Name</c> is a renameable InterfaceAlias that reaches both the JSON and the Markdown, and
    /// neither the path rules (summary-scoped, not hardware) nor the content pass can catch an arbitrary rename, so it
    /// is replaced here with the same placeholder. The GUI live view reads raw snapshots, not this document, so it is unaffected.
    /// </summary>
    private static ReportDocument Prepare(ReportDocument document, bool effective)
    {
        if (!effective)
        {
            return document with { Redacted = false };
        }

        var system = document.Summary.System;
        var nics = system.Nics;
        if (nics.Count > 0)
        {
            var blanked = new NicInfo[nics.Count];
            for (var i = 0; i < nics.Count; i++)
            {
                blanked[i] = nics[i] with { Name = SimpleRedactor.Placeholder };
            }

            system = system with { Nics = blanked };
        }

        return document with
        {
            Redacted = true,
            Summary = document.Summary with { System = system },
        };
    }
}
