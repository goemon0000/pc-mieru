namespace PcMieru.Update.Bundle;

/// <summary>
/// The name rules of a bundle (DESIGN-B 3.2, the table and item 2) as pure functions. The same rules judge a ZIP entry, a path in a manifest and a file in
/// a folder; tools/publish.ps1 reaches them through tools/BundleCheck, so what publish adds can never disagree with what the client accepts.
///
/// A <c>&lt;stem&gt;</c> starts with an ASCII letter or digit; the rest are ASCII letters, digits, '.', '_' and '-'.
/// The allowed relative names, spelled exactly (the extensions are lower case):
/// <list type="bullet">
/// <item>root exe: <c>pcmi.exe</c>, <c>PCMieru.exe</c>, <c>PCMieru.&lt;stem&gt;.exe</c>, <c>createdump.exe</c></item>
/// <item>root dll: <c>&lt;stem&gt;.dll</c></item>
/// <item>root side files: <c>&lt;stem&gt;.json</c>, <c>&lt;stem&gt;.txt</c>, <c>&lt;stem&gt;.md</c>, <c>&lt;stem&gt;.pdb</c> and <c>LICENSE</c></item>
/// <item>satellites: <c>ja/&lt;stem&gt;.resources.dll</c> and <c>en/&lt;stem&gt;.resources.dll</c></item>
/// </list>
/// Everything else is refused. Case collisions are judged without regard to case (Windows does), but the spelling of a name must match the manifest's.
/// </summary>
public static class BundleNames
{
    /// <summary>The only folders a bundle may have besides its root (the satellite languages).</summary>
    public static IReadOnlyList<string> SatelliteFolders { get; } = ["ja", "en"];

    private static readonly string[] DeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Judges one relative name of a FILE of the bundle (as written in a manifest, below the root folder: "pcmi.exe", "ja/x.resources.dll").
    /// The character rules first (<see cref="CheckSyntax"/>), then the table of allowed shapes.
    /// </summary>
    public static BundleVerdict Check(string entryName)
    {
        var syntax = CheckSyntax(entryName);
        if (!syntax.Ok)
        {
            return syntax;
        }

        var slash = entryName.IndexOf('/');
        if (slash < 0)
        {
            return CheckRootFile(entryName)
                ? BundleVerdict.Pass
                : BundleVerdict.Refuse(BundleFault.NotAllowed, $"'{entryName}' is not an allowed root file name");
        }

        var folder = entryName[..slash];
        var file = entryName[(slash + 1)..];
        if (!IsSatelliteFolder(folder) || file.Contains('/'))
        {
            return BundleVerdict.Refuse(BundleFault.NotAllowed, $"'{entryName}': only ja/ and en/ may hold files below the root");
        }

        return CheckSatellite(file)
            ? BundleVerdict.Pass
            : BundleVerdict.Refuse(BundleFault.NotAllowed, $"'{entryName}' is not '<stem>.resources.dll'");
    }

    /// <summary>
    /// Judges a directory entry of the bundle (relative to the root, without the trailing "/"): only ja and en.
    /// </summary>
    public static BundleVerdict CheckDirectory(string relativeDirectory)
    {
        var syntax = CheckSyntax(relativeDirectory);
        if (!syntax.Ok)
        {
            return syntax;
        }

        return IsSatelliteFolder(relativeDirectory)
            ? BundleVerdict.Pass
            : BundleVerdict.Refuse(BundleFault.DirectoryNotAllowed, $"'{relativeDirectory}/' is not ja/ or en/");
    }

    /// <summary>True for "ja" and "en", spelled exactly.</summary>
    public static bool IsSatelliteFolder(string name) => name is "ja" or "en";

    /// <summary>
    /// The character rules of item 2, for any path made of '/'-separated elements (a ZIP entry name without its trailing "/", a manifest path, the
    /// relative path of a real file): no empty name, no backslash, no leading '/', no drive name, no ':', no control character, no empty, '.' or '..' element,
    /// no element that ends with a dot or a space, no DOS device name (with or without an extension, in any case).
    /// </summary>
    public static BundleVerdict CheckSyntax(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return BundleVerdict.Refuse(BundleFault.EmptyName, "empty name");
        }

        if (name.Contains('\\'))
        {
            return BundleVerdict.Refuse(BundleFault.Backslash, $"'{Show(name)}' has a backslash (entry names use '/')");
        }

        foreach (var c in name)
        {
            if (char.IsControl(c))
            {
                return BundleVerdict.Refuse(BundleFault.ControlCharacter, $"'{Show(name)}' has a control character");
            }
        }

        if (name[0] == '/')
        {
            return BundleVerdict.Refuse(BundleFault.LeadingSlash, $"'{name}' starts with '/'");
        }

        if (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':')
        {
            return BundleVerdict.Refuse(BundleFault.DriveName, $"'{name}' starts with a drive name");
        }

        if (name.Contains(':'))
        {
            return BundleVerdict.Refuse(BundleFault.Colon, $"'{name}' has a ':'");
        }

        foreach (var segment in name.Split('/'))
        {
            if (segment.Length == 0)
            {
                return BundleVerdict.Refuse(BundleFault.EmptySegment, $"'{name}' has an empty element");
            }

            if (segment is "." or "..")
            {
                return BundleVerdict.Refuse(BundleFault.DotSegment, $"'{name}' has a '{segment}' element");
            }

            if (segment[^1] is '.' or ' ')
            {
                return BundleVerdict.Refuse(BundleFault.TrailingDotOrSpace, $"'{name}': element '{segment}' ends with a dot or a space");
            }

            if (IsDeviceName(segment))
            {
                return BundleVerdict.Refuse(BundleFault.DeviceName, $"'{name}': element '{segment}' is a DOS device name");
            }
        }

        return BundleVerdict.Pass;
    }

    /// <summary>A DOS device name: the part before the first '.' (spaces before the dot dropped) is CON, PRN, AUX, NUL, COM0..9 or LPT0..9 in any case (Windows reserves COM0 and LPT0 too).</summary>
    private static bool IsDeviceName(string segment)
    {
        var dot = segment.IndexOf('.');
        var stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
        foreach (var device in DeviceNames)
        {
            if (stem.Equals(device, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>"x.y-z_1": starts with an ASCII letter or digit, then letters, digits, '.', '_' and '-'.</summary>
    internal static bool IsStem(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty || !char.IsAsciiLetterOrDigit(text[0]))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CheckRootFile(string name)
    {
        if (name == "LICENSE")
        {
            return true;
        }

        if (name.EndsWith(".exe", StringComparison.Ordinal))
        {
            if (name is "pcmi.exe" or "PCMieru.exe" or "createdump.exe")
            {
                return true;
            }

            // PCMieru.<stem>.exe
            const string prefix = "PCMieru.";
            return name.StartsWith(prefix, StringComparison.Ordinal) && IsStem(name.AsSpan(prefix.Length, name.Length - prefix.Length - ".exe".Length));
        }

        foreach (var extension in (ReadOnlySpan<string>)[".dll", ".json", ".txt", ".md", ".pdb"])
        {
            if (name.EndsWith(extension, StringComparison.Ordinal))
            {
                return IsStem(name.AsSpan(0, name.Length - extension.Length));
            }
        }

        return false;
    }

    private static bool CheckSatellite(string file)
    {
        const string suffix = ".resources.dll";
        return file.EndsWith(suffix, StringComparison.Ordinal) && IsStem(file.AsSpan(0, file.Length - suffix.Length));
    }

    /// <summary>A name made safe to show in a message (control characters replaced).</summary>
    internal static string Show(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]))
            {
                chars[i] = '?';
            }
        }

        return new string(chars);
    }
}
