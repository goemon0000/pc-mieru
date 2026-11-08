using PcMieru.Wire;

namespace PcMieru.Core.Management;

/// <summary>
/// Turns what Wire (and the Helper) report, a code and a message key, into the <see cref="OperationResult"/> the front
/// ends show. The code is passed on unchanged (it is also the CLI exit code); only the text is made here, in the
/// current language.
/// </summary>
internal static class OperationMapper
{
    public static OperationResult ToResult(ActionResult action, Language? language = null) =>
        new(action.Code, Text(action.MessageKey, action.Args, language));

    public static OperationResult ToResult(OperationCode code, string key, Language? language = null, params (string Name, string Value)[] args) =>
        ToResult(ActionResult.Of(code, key, args), language);

    /// <summary>
    /// What the Helper outcome means. The exit code (<see cref="HelperOutcome.Code"/>) is authoritative: the result file
    /// is only used for the wording, and only when it agrees with the exit code.
    /// </summary>
    public static OperationResult FromHelper(HelperOutcome outcome, Language? language = null)
    {
        if (outcome.UacDeclined)
        {
            return new OperationResult(OperationCode.Cancelled, Strings.Get(ManagementStrings.UacDeclined, language ?? Strings.Current));
        }

        if (outcome.Result is { } result && result.Code == outcome.Code && !string.IsNullOrEmpty(result.Message))
        {
            return ToResult(ActionResult.FromHelperResult(result), language);
        }

        if (outcome.Code == OperationCode.Cancelled)
        {
            // The caller gave up (or the Helper itself reported "cancelled" with nothing more): there is nothing to explain.
            return new OperationResult(OperationCode.Cancelled, "");
        }

        // No usable file (or one that disagrees with the exit code): say what is certain, which is the exit code.
        var key = outcome.Code == OperationCode.Ok ? ManagementStrings.HelperDone : MessageKeys.HelperNoResult;
        return new OperationResult(outcome.Code, Strings.Get(key, language ?? Strings.Current));
    }

    /// <summary>The Helper's answer as an <see cref="ActionResult"/>, with data (efficiency values, undo bytes) kept; null if there is none that can be trusted.</summary>
    public static ActionResult? ActionFromHelper(HelperOutcome outcome) =>
        !outcome.UacDeclined && outcome.Result is { } result && result.Code == outcome.Code
            ? ActionResult.FromHelperResult(result)
            : null;

    /// <summary>A message key and its values in the user's language. A message that is not one of our keys is kept as given.</summary>
    public static string Text(string key, IReadOnlyDictionary<string, string>? args, Language? language = null)
    {
        var lang = language ?? Strings.Current;
        if (!IsRegistered(key, lang))
        {
            return key;
        }

        if (args is not { Count: > 0 })
        {
            return Strings.Get(key, lang);
        }

        // Values that are themselves message keys (the "reason" of a restart that only stopped) are localized with the same values.
        var localized = new Dictionary<string, string>(args.Count, StringComparer.Ordinal);
        foreach (var (name, value) in args)
        {
            localized[name] = value.StartsWith("mgmt.", StringComparison.Ordinal) && IsRegistered(value, lang)
                ? Strings.Format(value, args, lang)
                : value;
        }

        return Strings.Format(key, localized, lang);
    }

    private static bool IsRegistered(string key, Language lang)
    {
        var text = Strings.Get(key, lang);
        return !(text.Length == key.Length + 2 && text[0] == '[' && text[^1] == ']' && text.AsSpan(1, key.Length).SequenceEqual(key));
    }
}
