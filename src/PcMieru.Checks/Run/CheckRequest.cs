namespace PcMieru.Checks;

/// <summary>
/// What a caller asks for (DESIGN-A §2). <see cref="Duration"/> is the length of a load test (CPU and memory): 30, 120 or 300 seconds,
/// or null for the check's own default; the other kinds take none. <see cref="AllowElevation"/> is for the storage check only: it lets the
/// inspection hand the drives Windows denied to the elevated helper, and it is a separate, explicit user action (false is the default).
/// A request that breaks these rules is a programming error and throws <see cref="ArgumentException"/> before anything starts: no run
/// exists, nothing is saved.
/// </summary>
public sealed record CheckRequest(CheckKind Kind, TimeSpan? Duration = null, bool AllowElevation = false)
{
    /// <summary>The load lengths a request may name.</summary>
    public static IReadOnlyList<TimeSpan> AllowedDurations { get; } = [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(300)];

    /// <summary>What is wrong with the request, or null when it is fine.</summary>
    public string? Problem
    {
        get
        {
            if (!Enum.IsDefined(Kind))
            {
                return "The kind of check is not known.";
            }

            if (Duration is { } duration)
            {
                if (Kind is not (CheckKind.Cpu or CheckKind.Memory))
                {
                    return "A duration belongs to a CPU or memory check only.";
                }

                if (!AllowedDurations.Contains(duration))
                {
                    return "The duration must be 30, 120 or 300 seconds.";
                }
            }

            if (AllowElevation && Kind != CheckKind.Storage)
            {
                return "AllowElevation belongs to the storage check only.";
            }

            return null;
        }
    }

    /// <summary>Throws <see cref="ArgumentException"/> when <see cref="Problem"/> says something.</summary>
    public void EnsureValid()
    {
        if (Problem is { } problem)
        {
            throw new ArgumentException(problem, nameof(CheckRequest));
        }
    }
}
