// Provenance: ported from the owner's pl-cloud src/PartsLife.Update/UpdateSchedule.cs (Parts Life, MIT, same owner; see NOTICE.txt).
// What is new here: the comments are in English. The rules (a day apart, an hourly look, a clock set back does not block, a rate limit waits a day or what it asks, at most a week) are unchanged.
namespace PcMieru.Update.Feed;

/// <summary>
/// When the feed may be asked (DESIGN-B 3.1): the first time 60 seconds after the start, then when a day has passed since the last check, looked at
/// once an hour while the app runs. A rate limit (403 / 429) is waited out: at least a day, or what the server asks, never more than a week. A manual
/// check waits too. Pure functions of the state and the time, so a test can drive them with a clock of its own.
/// </summary>
public static class UpdateSchedule
{
    /// <summary>From the start of the app to the first look.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(60);

    /// <summary>The wait between two checks.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>How often a running app looks at whether a check is due.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromHours(1);

    /// <summary>The wait after a check that got no answer (no network, an error status from GitHub): asked again after two hours, not a day. Never earlier than a back-off.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(2);

    /// <summary>The least wait after a rate limit.</summary>
    public static readonly TimeSpan RateLimitBackoff = TimeSpan.FromHours(24);

    /// <summary>The most wait after a rate limit, whatever the server asks (and whatever a damaged state file says).</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromDays(7);

    /// <summary>True while a rate limit is being waited out. A time further away than <see cref="MaxBackoff"/> is not believed.</summary>
    public static bool InBackoff(UpdateState state, DateTimeOffset now) =>
        state.BackoffUntilUtc is { } until && now < until && until - now <= MaxBackoff;

    /// <summary>The wait between two scheduled checks: <see cref="RetryAfterFailure"/> after a check that got no answer, else <see cref="Interval"/>.</summary>
    public static TimeSpan IntervalFor(UpdateState state) =>
        Enum.TryParse<UpdateRefusal>(state.LastRefusal, ignoreCase: false, out var last) && UpdateRefusals.IsTransient(last) ? RetryAfterFailure : Interval;

    /// <summary>A scheduled check is due: not in a back-off, and never checked, or a day ago, or the last check is in the future (the clock was set back).</summary>
    public static bool IsDue(UpdateState state, DateTimeOffset now)
    {
        if (InBackoff(state, now))
        {
            return false;
        }

        if (state.LastCheckUtc is not { } last)
        {
            return true;
        }

        return last > now || now - last >= IntervalFor(state);
    }

    /// <summary>A manual check may go ahead unless a rate limit is being waited out.</summary>
    public static bool CanCheckNow(UpdateState state, DateTimeOffset now) => !InBackoff(state, now);

    /// <summary>When the next scheduled check falls due (for a page that says so). Null: now.</summary>
    public static DateTimeOffset? NextDue(UpdateState state, DateTimeOffset now)
    {
        if (state.LastCheckUtc is not { } last || last > now)
        {
            return null;
        }

        var due = last + IntervalFor(state);
        return state.BackoffUntilUtc is { } until && until > due ? until : due;
    }

    /// <summary>403 and 429 are what GitHub says for "too many requests".</summary>
    public static bool IsRateLimited(int statusCode) => statusCode is 403 or 429;

    /// <summary>The end of the wait after a rate limit: the longest of a day, what Retry-After says and what x-ratelimit-reset says, capped at a week.</summary>
    public static DateTimeOffset BackoffUntil(DateTimeOffset now, TimeSpan? retryAfter, DateTimeOffset? reset)
    {
        var until = now + RateLimitBackoff;
        if (retryAfter is { } wait && now + wait > until)
        {
            until = now + wait;
        }

        if (reset is { } at && at > until)
        {
            until = at;
        }

        var cap = now + MaxBackoff;
        return until > cap ? cap : until;
    }
}
