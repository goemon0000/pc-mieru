using System.Globalization;
using System.Numerics;

namespace PcMieru.Checks;

/// <summary>How a value is written (DESIGN-A §3). The registry fixes this for every item ID.</summary>
public enum CheckValueType
{
    /// <summary>A JSON integer.</summary>
    Int,

    /// <summary>A finite JSON number.</summary>
    Number,

    /// <summary>An unsigned decimal integer as a string ("dec"), up to 128 bits. Big counters and byte counts.</summary>
    Dec,

    /// <summary>A signed decimal integer as a string (the .delta items).</summary>
    SignedDec,

    /// <summary>A JSON string.</summary>
    Text,

    /// <summary>A JSON boolean.</summary>
    Bool,

    /// <summary>The object {name, displayVersion, build, ubr} of system.os.version.</summary>
    OsVersion,
}

/// <summary>The value of system.os.version (DESIGN-A §14.1). The two nullable parts are written as explicit nulls.</summary>
public sealed record OsVersionValue(string Name, string? DisplayVersion, int Build, int? Ubr);

/// <summary>
/// One item value, immutable. Build it with the factory that matches the registry type of the item; a mismatch is a
/// validation error. Decimal strings are held in canonical form (no sign for dec, no leading zeros, no "+", no "-0")
/// so that equal numbers are equal bytes.
/// </summary>
public sealed class CheckValue : IEquatable<CheckValue>
{
    private readonly long _int;
    private readonly double _number;
    private readonly string? _text;
    private readonly bool _bool;
    private readonly OsVersionValue? _os;

    private CheckValue(CheckValueType type, long i = 0, double d = 0, string? text = null, bool b = false, OsVersionValue? os = null)
    {
        Type = type;
        _int = i;
        _number = d;
        _text = text;
        _bool = b;
        _os = os;
    }

    public CheckValueType Type { get; }

    public long AsInt => Type == CheckValueType.Int ? _int : throw Wrong(CheckValueType.Int);

    public double AsNumber => Type == CheckValueType.Number ? _number : throw Wrong(CheckValueType.Number);

    /// <summary>The canonical decimal string of a Dec, a SignedDec or a Text value.</summary>
    public string AsText => Type is CheckValueType.Dec or CheckValueType.SignedDec or CheckValueType.Text ? _text! : throw Wrong(CheckValueType.Text);

    public bool AsBool => Type == CheckValueType.Bool ? _bool : throw Wrong(CheckValueType.Bool);

    public OsVersionValue AsOsVersion => Type == CheckValueType.OsVersion ? _os! : throw Wrong(CheckValueType.OsVersion);

    public static CheckValue Int(long value) => new(CheckValueType.Int, i: value);

    /// <summary>A finite number. NaN and infinity cannot be written to JSON and are rejected here.</summary>
    public static CheckValue Number(double value) =>
        double.IsFinite(value)
            ? new CheckValue(CheckValueType.Number, d: value == 0 ? 0 : value) // -0 and 0 are one value (one set of bytes)
            : throw new ArgumentOutOfRangeException(nameof(value), value, "A number must be finite.");

    public static CheckValue Dec(ulong value) => new(CheckValueType.Dec, text: value.ToString(CultureInfo.InvariantCulture));

    public static CheckValue Dec(UInt128 value) => new(CheckValueType.Dec, text: value.ToString(CultureInfo.InvariantCulture));

    /// <summary>A dec from text; the text must already be canonical (the form <see cref="Dec(UInt128)"/> writes).</summary>
    public static CheckValue Dec(string value) =>
        IsCanonicalDec(value) ? new CheckValue(CheckValueType.Dec, text: value) : throw new ArgumentException($"Not a canonical unsigned decimal: '{value}'.", nameof(value));

    public static CheckValue SignedDec(BigInteger value) =>
        FitsSigned(value) ? new CheckValue(CheckValueType.SignedDec, text: value.ToString(CultureInfo.InvariantCulture)) : throw new ArgumentOutOfRangeException(nameof(value), "A signed dec holds at most 128 bits of magnitude.");

    public static CheckValue SignedDec(string value) =>
        IsCanonicalSignedDec(value) ? new CheckValue(CheckValueType.SignedDec, text: value) : throw new ArgumentException($"Not a canonical signed decimal: '{value}'.", nameof(value));

    public static CheckValue Text(string value) => new(CheckValueType.Text, text: value ?? throw new ArgumentNullException(nameof(value)));

    public static CheckValue Bool(bool value) => new(CheckValueType.Bool, b: value);

    public static CheckValue OsVersion(OsVersionValue value) => new(CheckValueType.OsVersion, os: value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>"0" or digits without a leading zero, at most 128 bits.</summary>
    public static bool IsCanonicalDec(string? text) =>
        text is { Length: > 0 and <= 39 }
        && IsDigits(text)
        && (text.Length == 1 || text[0] != '0')
        && UInt128.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    /// <summary>"0" or an optional "-" and digits without a leading zero; the magnitude fits 128 bits.</summary>
    public static bool IsCanonicalSignedDec(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text[0] != '-')
        {
            return IsCanonicalDec(text);
        }

        // "-0" is not canonical, so a negative has at least one non-zero digit.
        var magnitude = text.AsSpan(1).ToString();
        return magnitude != "0" && IsCanonicalDec(magnitude);
    }

    private static bool FitsSigned(BigInteger value) => BigInteger.Abs(value) <= (BigInteger)UInt128.MaxValue;

    private static bool IsDigits(string text)
    {
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private InvalidOperationException Wrong(CheckValueType wanted) =>
        new($"The value is of type {Type}, not {wanted}.");

    public bool Equals(CheckValue? other) =>
        other is not null
        && Type == other.Type
        && _int == other._int
        && _number.Equals(other._number)
        && _bool == other._bool
        && string.Equals(_text, other._text, StringComparison.Ordinal)
        && Equals(_os, other._os);

    public override bool Equals(object? obj) => obj is CheckValue other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Type, _int, _number, _bool, _text, _os);

    public override string ToString() => Type switch
    {
        CheckValueType.Int => _int.ToString(CultureInfo.InvariantCulture),
        CheckValueType.Number => _number.ToString("R", CultureInfo.InvariantCulture),
        CheckValueType.Bool => _bool ? "true" : "false",
        CheckValueType.OsVersion => _os!.ToString(),
        _ => _text!,
    };
}
