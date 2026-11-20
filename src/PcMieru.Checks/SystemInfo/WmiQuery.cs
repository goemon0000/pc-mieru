namespace PcMieru.Checks.SystemInfo;

/// <summary>One row of a WMI result: a property by name. The seam of the mapper, so that a test needs no WMI.</summary>
internal interface IWmiRow
{
    /// <summary>The value of the property, or null when the row has no such property or it has no value.</summary>
    object? this[string name] { get; }
}

/// <summary>The namespaces the checks read (DESIGN-A §14.0, source <c>wmi</c>). Fixed text.</summary>
internal static class WmiScopes
{
    public const string Cimv2 = @"root\cimv2";

    public const string Storage = @"\\.\root\microsoft\windows\storage";
}

/// <summary>
/// One fixed WMI question: the namespace, the query text and the row mapper. The text and the scope are constants of the code that
/// asks, never built from a name or a value of the machine or the user. The mapper is part of the question because the rows are only
/// read inside the deadline: connect, execute, enumerate and map all happen before it passes, or the answer is a reason.
/// </summary>
internal sealed class WmiQuery<T>
{
    public WmiQuery(string scope, string text, Func<IWmiRow, T> map)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(map);
        Scope = scope;
        Text = text;
        Map = map;
    }

    public string Scope { get; }

    public string Text { get; }

    public Func<IWmiRow, T> Map { get; }
}

/// <summary>
/// Reads all rows of a fixed WMI question within a deadline (DESIGN-A §6.1). The answer is the complete list of the mapped rows or a
/// reason; a list that was cut short (deadline, error, stop) is thrown away.
/// </summary>
internal interface IWmiReader
{
    Task<CheckReading<IReadOnlyList<T>>> ReadAsync<T>(WmiQuery<T> query, QueryDeadline deadline, CancellationToken ct);
}

/// <summary>
/// The one thing the reader needs from WMI: connect to <paramref name="scope"/>, run <paramref name="query"/>, and hand the rows one by one
/// to <paramref name="visit"/> until it returns false or there are no more. <paramref name="timeout"/> is what is left of the deadline, for the
/// connection and for the enumeration. It runs on the worker thread of the lane; it may throw (a Windows or WMI failure), and the reader
/// turns that into a reason. The seam of the unit tests; <see cref="ManagementWmiExecutor"/> is the thin real one.
/// </summary>
internal interface IWmiExecutor
{
    void Run(string scope, string query, TimeSpan timeout, Func<IWmiRow, bool> visit);
}
