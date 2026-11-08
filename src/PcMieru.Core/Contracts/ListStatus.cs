namespace PcMieru.Core;

/// <summary>
/// A list (disks, network adapters, GPUs) cannot carry a status, so <see cref="SystemInfo"/> carries one next to each:
/// Ok with the number of entries when the source was read (zero is then a real answer), otherwise the status and the reason.
/// An empty list with a status that is not Ok means "could not read", never "there are none".
/// </summary>
public static class ListStatus
{
    /// <summary>Nobody filled the status: a hand-built <see cref="SystemInfo"/>, or a JSON file from before the status existed. It says nothing about the list.</summary>
    public static bool IsUnset(Value<int> state) =>
        state.Status == ValueStatus.NotCollected && string.IsNullOrEmpty(state.Source);

    /// <summary>True when the list is empty and its status says why: the front end shows that text instead of an empty box.</summary>
    public static bool ExplainsEmptyList(int count, Value<int> state) =>
        count == 0 && !state.IsOk && !IsUnset(state);

    /// <summary>"Error: GetIfTable2 returned 5", the status word in the given language followed by the reason when there is one.</summary>
    public static string Describe(Value<int> state, Language? language = null)
    {
        var word = Strings.Status(state.Status, language);
        return string.IsNullOrWhiteSpace(state.Detail) ? word : word + ": " + state.Detail;
    }
}
