using System.Text.Json.Serialization;
using PcMieru.Wire;

namespace PcMieru.Core;

[JsonConverter(typeof(CamelCaseEnumConverter<ObservationSeverity>))]
public enum ObservationSeverity
{
    Info,
    Notice,
    Warning,
}

/// <summary>Fact = a measured statement. Candidate = a possible explanation. Never state a candidate as a fact.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ObservationCertainty>))]
public enum ObservationCertainty
{
    Fact,
    Candidate,
}

/// <summary>
/// A "why is it slow" finding. Text is not stored: MessageKey + Args are rendered through the string table
/// in whichever language the reader wants (Strings.Format), so the same finding reads in ja and en.
/// </summary>
public sealed record Observation(
    string Id,
    ObservationSeverity Severity,
    ObservationCertainty Certainty,
    string MessageKey,
    IReadOnlyDictionary<string, string> Args,
    IReadOnlyList<Evidence> Evidence,
    IReadOnlyList<ProcessKey> Processes);

/// <summary>One measured number behind an observation. Value is always text, with its unit in Unit.</summary>
public sealed record Evidence(string Metric, string Value, string Unit);
