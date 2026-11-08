using PcMieru.Wire;

namespace PcMieru.Cli;

/// <summary>Process exit codes (SPEC section 9). Frozen: scripts depend on the numbers.</summary>
public enum ExitCode
{
    Success = 0,
    Failure = 1,
    BadArguments = 2,
    NotFound = 3,
    Denied = 4,
    Protected = 5,
    MultipleMatches = 6,
}

public static class ExitCodes
{
    public static ExitCode From(OperationCode code) => code switch
    {
        OperationCode.Ok => ExitCode.Success,
        OperationCode.PartialSuccess => ExitCode.Failure,
        OperationCode.InvalidArguments => ExitCode.BadArguments,
        OperationCode.NotFound or OperationCode.Gone => ExitCode.NotFound,
        OperationCode.Denied => ExitCode.Denied,
        OperationCode.Protected => ExitCode.Protected,
        OperationCode.MultipleMatches => ExitCode.MultipleMatches,
        _ => ExitCode.Failure,
    };
}
