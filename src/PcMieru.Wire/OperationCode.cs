using System.Text.Json.Serialization;

namespace PcMieru.Wire;

/// <summary>
/// Outcome of a management operation. The numeric value is also the Helper's process exit code,
/// so values 0..6 line up with the CLI exit codes (SPEC section 9).
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter<OperationCode>))]
public enum OperationCode
{
    Ok = 0,
    Error = 1,
    InvalidArguments = 2,
    NotFound = 3,
    Denied = 4,
    Protected = 5,
    MultipleMatches = 6,
    PartialSuccess = 7,
    Gone = 8,
    Cancelled = 9,
    Unsupported = 10,
    NotImplemented = 64,
}
