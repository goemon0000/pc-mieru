using System.Globalization;

namespace PcMieru.Wire;

/// <summary>
/// The whole command line of <c>PCMieru.NetMeter.exe</c>:
/// <c>--parent &lt;pid&gt; --parent-created &lt;filetime&gt; --pipe &lt;name&gt; --user-sid &lt;sid&gt;</c>, each exactly once.
/// The meter runs elevated and the exe is easy to start by hand, so the parser is strict: it accepts only values of the
/// shapes the front end produces and refuses everything else. In particular the SID is inserted into an SDDL string,
/// so it must be a plain SID text and nothing that could carry extra ACEs.
/// </summary>
public sealed record NetMeterArguments(uint ParentPid, long ParentCreatedFileTime, string PipeName, string UserSid)
{
    public const string PipePrefix = "PCMieru.NetMeter.";
    public const string SessionPrefix = "PCMieru.NetMeter.";
    public const int MinTokenLength = 16;
    public const int MaxTokenLength = 64;

    /// <summary>The ETW session is named after the user so two users' meters never collide, and an orphan is found again.</summary>
    public static string SessionNameFor(string userSid) => SessionPrefix + userSid;

    public string SessionName => SessionNameFor(UserSid);

    public static string BuildCommandLine(uint parentPid, long parentCreated, string pipeToken, string userSid) =>
        string.Create(CultureInfo.InvariantCulture, $"--parent {parentPid} --parent-created {parentCreated} --pipe {PipePrefix}{pipeToken} --user-sid {userSid}");

    public static bool TryParse(string[] args, out NetMeterArguments? result, out string error)
    {
        result = null;
        if (args.Length != 8)
        {
            error = "expected: --parent <pid> --parent-created <filetime> --pipe <name> --user-sid <sid>";
            return false;
        }

        string? parent = null, created = null, pipe = null, sid = null;
        for (var i = 0; i < args.Length; i += 2)
        {
            ref var slot = ref Slot(args[i], ref parent, ref created, ref pipe, ref sid, out var known);
            if (!known)
            {
                error = "unknown option " + Printable(args[i]);
                return false;
            }

            if (slot is not null)
            {
                error = "option given twice: " + args[i];
                return false;
            }

            slot = args[i + 1];
        }

        if (parent is null || created is null || pipe is null || sid is null)
        {
            error = "a required option is missing";
            return false;
        }

        if (!TryParseDecimal(parent, out var parentValue) || parentValue is 0 or > uint.MaxValue)
        {
            error = "--parent is not a process id";
            return false;
        }

        if (!TryParseDecimal(created, out var createdValue) || createdValue is 0 or > long.MaxValue)
        {
            error = "--parent-created is not a FILETIME";
            return false;
        }

        if (!IsPipeName(pipe))
        {
            error = "--pipe is not " + PipePrefix + "<hex token>";
            return false;
        }

        if (!IsSidText(sid))
        {
            error = "--user-sid is not a SID";
            return false;
        }

        result = new NetMeterArguments((uint)parentValue, (long)createdValue, pipe, sid);
        error = string.Empty;
        return true;
    }

    /// <summary>PCMieru.NetMeter. followed by 16 to 64 hex digits, nothing else.</summary>
    public static bool IsPipeName(string name)
    {
        if (!name.StartsWith(PipePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var token = name.AsSpan(PipePrefix.Length);
        if (token.Length is < MinTokenLength or > MaxTokenLength)
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The canonical SID text <c>S-1-&lt;authority&gt;-&lt;sub&gt;(-&lt;sub&gt;){1,15}</c>: decimal numbers only, a 48-bit
    /// authority, 32-bit sub-authorities, no sign, no space, no leading zeros. Anything else is refused.
    /// </summary>
    public static bool IsSidText(string text)
    {
        if (!text.StartsWith("S-1-", StringComparison.Ordinal) || text.Length > 184)
        {
            return false;
        }

        var parts = text.Split('-');
        if (parts.Length is < 4 or > 3 + 15)
        {
            return false;
        }

        if (!TryParseDecimal(parts[2], out var authority) || authority > 0xFFFF_FFFF_FFFFUL)
        {
            return false;
        }

        for (var i = 3; i < parts.Length; i++)
        {
            if (!TryParseDecimal(parts[i], out var sub) || sub > uint.MaxValue)
            {
                return false;
            }
        }

        return true;
    }

    private static ref string? Slot(string option, ref string? parent, ref string? created, ref string? pipe, ref string? sid, out bool known)
    {
        known = true;
        switch (option)
        {
            case "--parent":
                return ref parent;
            case "--parent-created":
                return ref created;
            case "--pipe":
                return ref pipe;
            case "--user-sid":
                return ref sid;
            default:
                known = false;
                return ref parent;
        }
    }

    /// <summary>Plain decimal digits, no sign, no leading zero (a lone 0 is fine), at most 20 digits and no overflow.</summary>
    private static bool TryParseDecimal(string text, out ulong value)
    {
        value = 0;
        if (text.Length is 0 or > 20 || (text.Length > 1 && text[0] == '0'))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static string Printable(string text) =>
        text.Length > 32 ? text[..32] + "..." : text;
}
