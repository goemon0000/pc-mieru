using System.Security.Cryptography;
using PcMieru.Core;
using PcMieru.Wire;

namespace PcMieru.Cli;

/// <summary>What the state-changing commands print for an <see cref="OperationCode"/> and how the exit code is chosen.</summary>
public static class OperationText
{
    public static string Describe(CliContext context, OperationCode code) => context.Text("cli.result." + code switch
    {
        OperationCode.Ok => "ok",
        OperationCode.InvalidArguments => "invalidArguments",
        OperationCode.NotFound => "notFound",
        OperationCode.Denied => "denied",
        OperationCode.Protected => "protected",
        OperationCode.MultipleMatches => "multipleMatches",
        OperationCode.PartialSuccess => "partial",
        OperationCode.Gone => "gone",
        OperationCode.Cancelled => "cancelled",
        OperationCode.Unsupported => "unsupported",
        OperationCode.NotImplemented => "notImplemented",
        _ => "error",
    });

    /// <summary>The one line shown for a result: the localized meaning, then the provider's own words when it gave any.</summary>
    public static string Line(CliContext context, OperationResult result)
    {
        var text = Describe(context, result.Code);
        if (result.Code != OperationCode.Ok && !string.IsNullOrWhiteSpace(result.Message))
        {
            text += " (" + CliText.Printable(result.Message.Trim()) + ")";
        }

        return text;
    }

    /// <summary>One result: its own code. Several: all fine is 0, all the same failure is that failure, a mix is 1.</summary>
    public static ExitCode Combine(IReadOnlyList<OperationCode> codes)
    {
        if (codes.Count == 0)
        {
            return ExitCode.Success;
        }

        if (codes.All(c => c == OperationCode.Ok))
        {
            return ExitCode.Success;
        }

        var distinct = codes.Distinct().ToArray();
        return distinct.Length == 1 ? ExitCodes.From(distinct[0]) : ExitCode.Failure;
    }
}

/// <summary>
/// The CLI side of "run it as administrator" (SPEC section 8). Only used after an explicit <c>--elevate</c>, and only for
/// the fixed requests the Helper knows. The request is fixed on the command line; one UAC prompt covers one request.
/// </summary>
public static class HelperElevation
{
    public static async Task<OperationResult> RunAsync(CliContext context, Func<string, string, HelperRequest> build)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var directory = context.HelperDirectory;
        var resultPath = Path.Combine(directory, nonce + ".json");
        try
        {
            // The caller makes the file first, so the result keeps the caller's permissions (the Helper only overwrites it).
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(resultPath, [], context.Ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult.Fail(OperationCode.Error, ex.Message);
        }

        try
        {
            context.Err.WriteLine("pcmi: " + context.Text("cli.elevate.prompt"));
            var outcome = await context.Services().Helper.RunAsync(build(nonce, resultPath), context.Ct).ConfigureAwait(false);
            if (outcome.UacDeclined)
            {
                return OperationResult.Fail(OperationCode.Denied, context.Text("cli.elevate.declined"));
            }

            var message = outcome.Result?.Message ?? outcome.Detail ?? string.Empty;
            var code = outcome.Result?.Code ?? outcome.Code;
            return code == OperationCode.Ok ? OperationResult.Success(message) : OperationResult.Fail(code, message);
        }
        finally
        {
            try
            {
                File.Delete(resultPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="direct"/> first. When it comes back Denied and the user asked for --elevate, the same change is
    /// requested through the Helper. Without --elevate a Denied result says how to ask for it.
    /// </summary>
    public static async Task<OperationResult> WithElevationAsync(
        CliContext context,
        ParsedArgs args,
        Func<Task<OperationResult>> direct,
        Func<string, string, HelperRequest> build)
    {
        var result = await direct().ConfigureAwait(false);
        if (result.Code != OperationCode.Denied)
        {
            return result;
        }

        if (!args.Has("elevate"))
        {
            context.Err.WriteLine("pcmi: " + context.Text("cli.hint.elevate"));
            return result;
        }

        return await RunAsync(context, build).ConfigureAwait(false);
    }
}
