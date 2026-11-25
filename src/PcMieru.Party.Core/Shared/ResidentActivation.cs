namespace PcMieru.Party;

/// <summary>
/// Which running PCMieru.Party is "the resident" a second start should bring to the front (DESIGN-C 3 item 2). Pure: the caller reads the processes
/// (id, session, full image path) and this only decides, so the rule can be tested without starting anything.
///
/// A process counts only when ALL of these hold: it is not this process, it runs in the SAME Windows session (another signed-in user's Party is not ours,
/// and a window of another session cannot be reached anyway), and its full image path is the same file as ours (a different install, an old Parts Life or
/// a copy elsewhere is not the resident of this install). A process whose path could not be read counts for nothing: unknown is not "the same".
/// </summary>
public static class ResidentActivation
{
    /// <summary>The process ids that qualify, in the order given.</summary>
    public static IReadOnlyList<int> Pick(
        IEnumerable<(int Pid, int SessionId, string? ImagePath)> candidates,
        int selfPid, int selfSessionId, string? selfImagePath)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var self = Normalize(selfImagePath);
        var picked = new List<int>();
        if (self is null)
        {
            return picked;
        }

        foreach (var (pid, session, path) in candidates)
        {
            if (pid == selfPid || session != selfSessionId)
            {
                continue;
            }

            if (Normalize(path) is { } other && string.Equals(other, self, StringComparison.OrdinalIgnoreCase))
            {
                picked.Add(pid);
            }
        }

        return picked;
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
