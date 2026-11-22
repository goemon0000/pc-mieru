namespace PcMieru.Core.Collection;

/// <summary>
/// A snapshot source that wants to know the period of the loop that drives it. The first sample after a start or a pause has no previous one to
/// measure against, so it states a NOMINAL interval; that nominal interval must be the loop's beat (the recording period while a recording lease
/// is held), not the user's window interval. The loop tells the source once, when it is built; the function is cheap and may be called on any thread.
/// </summary>
internal interface IBeatAware
{
    void UseBeat(Func<TimeSpan> beat);
}
