using System.Globalization;

namespace PcMieru.Wire;

/// <summary>
/// The physical drive numbers of one disk-health-all request: 1 to <see cref="MaxCount"/> distinct small integers
/// (0 to <see cref="MaxNumber"/>), smallest first. On the command line they are one option, "--drives 0,2,3": plain
/// decimal digits separated by commas, no signs, no spaces, no paths, always in this one canonical order. A list that is
/// not exactly that is refused by <see cref="TryParse"/>, so the argument the Helper sees is the only spelling there is.
/// </summary>
public sealed class DriveList : IEquatable<DriveList>
{
    public const int MaxCount = 16;

    public const int MaxNumber = 255;

    private readonly int[] _numbers;

    private DriveList(int[] numbers) => _numbers = numbers;

    public IReadOnlyList<int> Numbers => _numbers;

    /// <summary>Sorts the numbers. Refuses an empty list, a list longer than <see cref="MaxCount"/>, a number outside 0..255 and a repeated number.</summary>
    public static bool TryCreate(IEnumerable<int> numbers, out DriveList? list)
    {
        list = null;
        var sorted = numbers.Order().ToArray();
        if (sorted.Length is 0 or > MaxCount)
        {
            return false;
        }

        for (var i = 0; i < sorted.Length; i++)
        {
            if (sorted[i] is < 0 or > MaxNumber || (i > 0 && sorted[i] == sorted[i - 1]))
            {
                return false;
            }
        }

        list = new DriveList(sorted);
        return true;
    }

    /// <summary>The wire form: strictly ascending decimal numbers separated by single commas, nothing else.</summary>
    public static bool TryParse(string text, out DriveList? list)
    {
        list = null;
        var parts = text.Split(',');
        if (parts.Length is 0 or > MaxCount)
        {
            return false;
        }

        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > 3 || (part.Length > 1 && part[0] == '0') || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]) || (i > 0 && numbers[i] <= numbers[i - 1]))
            {
                return false;
            }
        }

        return TryCreate(numbers, out list);
    }

    public string ToWire() => string.Join(',', _numbers.Select(n => n.ToString(CultureInfo.InvariantCulture)));

    public bool Equals(DriveList? other) => other is not null && _numbers.AsSpan().SequenceEqual(other._numbers);

    public override bool Equals(object? obj) => Equals(obj as DriveList);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var n in _numbers)
        {
            hash.Add(n);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => ToWire();
}
