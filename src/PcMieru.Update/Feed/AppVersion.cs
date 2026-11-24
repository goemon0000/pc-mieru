// Provenance: ported from the owner's pl-cloud src/PartsLife.Update/AppVersion.cs (Parts Life, MIT, same owner; see NOTICE.txt).
// What is new here: a number with a leading zero ("v01.2.3", "1.02.3") is not read (one spelling per version), and the comments are in English.
using System.Globalization;

namespace PcMieru.Update.Feed;

/// <summary>
/// A version number (X.Y.Z). The tag is <c>vX.Y.Z</c>.
///
/// **Only three plain numbers are read.** <c>v1.2.3-beta</c>, <c>v1.2</c>, <c>v1.2.3+build</c> and <c>v1.02.3</c> are not (so they are never an update
/// candidate): a pre-release tag that was marked "latest" by mistake must not be handed out automatically, and one version has exactly one spelling.
/// </summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch) : IComparable<AppVersion>
{
    /// <summary>The most digits one number may have (the old rule: "v1234567.0.0" is refused).</summary>
    private const int MaxDigits = 6;

    /// <summary>Reads "v1.2.3", "V1.2.3" or "1.2.3". Anything else (a suffix, a missing part, a sign, a space, a leading zero) is false.</summary>
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var s = text.AsSpan();
        if (s[0] is 'v' or 'V')
        {
            s = s[1..];
        }

        Span<int> parts = stackalloc int[3];
        for (var i = 0; i < 3; i++)
        {
            var dot = i < 2 ? s.IndexOf('.') : s.Length;
            if (dot <= 0 || dot > MaxDigits)
            {
                return false;
            }

            var digits = s[..dot];
            foreach (var c in digits)
            {
                if (c is < '0' or > '9')
                {
                    return false;
                }
            }

            if (digits.Length > 1 && digits[0] == '0')
            {
                return false;
            }

            parts[i] = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
            s = i < 2 ? s[(dot + 1)..] : ReadOnlySpan<char>.Empty;
        }

        version = new AppVersion(parts[0], parts[1], parts[2]);
        return true;
    }

    /// <summary>From an assembly version (0.7.2.0). The fourth part is not used.</summary>
    public static AppVersion From(Version? v) =>
        v is null ? default : new AppVersion(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

    public int CompareTo(AppVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major)
        : Minor != other.Minor ? Minor.CompareTo(other.Minor)
        : Patch.CompareTo(other.Patch);

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;

    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;

    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;

    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    /// <summary>"1.2.3": the spelling used in the manifest and in the asset names.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>"v1.2.3": the spelling of the release tag.</summary>
    public string Tag => "v" + ToString();
}
