// Provenance: ported from the owner's pl-cloud src/PartsLife.Update/Sha256List.cs (Parts Life, MIT, same owner; see NOTICE.txt).
// What is new here: the comments are in English. The rules (name match to the letter, a name that appears with two values is not used) are unchanged.
using System.Security.Cryptography;

namespace PcMieru.Update.Feed;

/// <summary>
/// The SHA256.txt that goes with a release (tools/publish.ps1 writes it). One line per file: 64 hex digits, two spaces, the name. The same shape
/// Get-FileHash and sha256sum print. A release has two lines: the zip and the outer manifest (DESIGN-B 3.1).
///
///   72b90b00...78ac  PCMieru-0.5.0-win-x64.zip
///   2c0f3321...88de  PCMieru-0.5.0-win-x64.manifest.json
///
/// **Only a name that matches to the letter (case included) counts.** A name that appears twice with different values is not used at all.
/// </summary>
public sealed class Sha256List
{
    /// <summary>The upper bound for a SHA256.txt (a real one is a few hundred bytes).</summary>
    public const int MaxBytes = 64 * 1024;

    private readonly Dictionary<string, string?> _byName = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Names => _byName.Keys;

    public static Sha256List Parse(string text)
    {
        var list = new Sha256List();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            if (line.Length > 0 && line[0] == '\uFEFF')
            {
                line = line[1..];
            }

            // "value, space, (space or *), name". The * is the binary-mode mark of sha256sum.
            if (line.Length < 66 || line[64] != ' ' || line[65] is not (' ' or '*'))
            {
                continue;
            }

            string hex = line[..64], name = line[66..];
            if (!IsHex64(hex) || name.Length == 0)
            {
                continue;
            }

            hex = hex.ToLowerInvariant();
            if (list._byName.TryGetValue(name, out var seen) && seen != hex)
            {
                list._byName[name] = null; // two values for one name
            }
            else
            {
                list._byName[name] = hex;
            }
        }

        return list;
    }

    /// <summary>The value of that name (lower-case hex), or null when it is absent or listed with two values.</summary>
    public string? For(string name) => _byName.GetValueOrDefault(name);

    public static bool IsHex64(string s)
    {
        if (s.Length != 64)
        {
            return false;
        }

        foreach (var c in s)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The SHA-256 of a file (lower-case hex).</summary>
    public static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    /// <summary>Whether two values are equal (case-insensitive). False when either is empty.</summary>
    public static bool Same(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
