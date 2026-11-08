using System.Runtime.InteropServices;
using System.Text;

namespace PcMieru.Core.Collection;

/// <summary>Product, description and company from a file's version resource. These strings are whatever the file says: they are not verified.</summary>
internal readonly record struct FileVersionStrings(Value<string> Product, Value<string> Description, Value<string> Company)
{
    public static FileVersionStrings All(ValueStatus status, string source, string detail) => new(
        Value.Missing<string>(status, source, detail),
        Value.Missing<string>(status, source, detail),
        Value.Missing<string>(status, source, detail));
}

internal static unsafe class VersionInfoReader
{
    public const string Source = "file version resource (not verified)";

    private const uint FileVerGetLocalised = 0x1;
    private const int MaxResourceBytes = 1 << 20;
    private const int MaxTextLength = 200;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorBadExeFormat = 193;
    private const int ErrorResourceDataNotFound = 1812;
    private const int ErrorResourceTypeNotFound = 1813;
    private const int ErrorResourceLangNotFound = 1814;

    // The language the file's strings are most often stored in, tried after the file's own translation list.
    private static readonly string[] FallbackTables = ["040904B0", "040904E4", "000004B0", "041104B0", "04110000"];

    public static FileVersionStrings Read(string path)
    {
        var size = Win32.GetFileVersionInfoSizeEx(FileVerGetLocalised, path, out _);
        if (size == 0)
        {
            return Missing(Marshal.GetLastPInvokeError());
        }

        if (size > MaxResourceBytes)
        {
            return FileVersionStrings.All(ValueStatus.Error, Source, "the version resource is unreasonably large and was not read");
        }

        var buffer = new byte[size];
        fixed (byte* block = buffer)
        {
            if (!Win32.GetFileVersionInfoEx(FileVerGetLocalised, path, 0, size, block))
            {
                return Missing(Marshal.GetLastPInvokeError());
            }

            var tables = TableNames(block);
            return new FileVersionStrings(
                Field(block, tables, "ProductName"),
                Field(block, tables, "FileDescription"),
                Field(block, tables, "CompanyName"));
        }
    }

    private static FileVersionStrings Missing(int error) => error switch
    {
        ErrorResourceDataNotFound or ErrorResourceTypeNotFound or ErrorResourceLangNotFound or ErrorBadExeFormat =>
            FileVersionStrings.All(ValueStatus.Unsupported, Source, "this file has no version resource"),
        ErrorFileNotFound or ErrorPathNotFound =>
            FileVersionStrings.All(ValueStatus.Error, Source, "the image file does not exist (deleted or replaced after the process started)"),
        (int)Win32.ErrorAccessDenied => FileVersionStrings.All(ValueStatus.Denied, Source, "access denied reading the file's version resource"),
        _ => FileVersionStrings.All(ValueStatus.Error, Source, $"the version resource could not be read (Win32 error {error})"),
    };

    /// <summary>"LLLLCCCC" (language, code page) for every translation the file lists, then the usual fallbacks.</summary>
    private static List<string> TableNames(byte* block)
    {
        var names = new List<string>(8);
        if (Win32.VerQueryValue(block, "\\VarFileInfo\\Translation", out var translation, out var length) && translation is not null)
        {
            for (var offset = 0; offset + 4 <= length && names.Count < 4; offset += 4)
            {
                var language = *(ushort*)(translation + offset);
                var codePage = *(ushort*)(translation + offset + 2);
                names.Add(language.ToString("X4") + codePage.ToString("X4"));
            }
        }

        foreach (var fallback in FallbackTables)
        {
            if (!names.Contains(fallback))
            {
                names.Add(fallback);
            }
        }

        return names;
    }

    private static Value<string> Field(byte* block, List<string> tables, string name)
    {
        foreach (var table in tables)
        {
            if (!Win32.VerQueryValue(block, $"\\StringFileInfo\\{table}\\{name}", out var text, out var length) || text is null || length == 0)
            {
                continue;
            }

            var clean = Clean(new string((char*)text, 0, (int)length));
            if (clean.Length > 0)
            {
                return Value.Ok(clean, Source);
            }
        }

        return Value.Unsupported<string>(Source, $"the file's version resource has no {name}");
    }

    /// <summary>Resource strings are arbitrary text: drop the terminator and control characters, collapse blanks and cap the length.</summary>
    internal static string Clean(string raw)
    {
        var builder = new StringBuilder(Math.Min(raw.Length, MaxTextLength));
        var lastWasBlank = true;
        foreach (var c in raw)
        {
            if (c == '\0')
            {
                break;
            }

            var blank = char.IsControl(c) || char.IsWhiteSpace(c);
            if (blank)
            {
                if (!lastWasBlank)
                {
                    _ = builder.Append(' ');
                }

                lastWasBlank = true;
            }
            else
            {
                _ = builder.Append(c);
                lastWasBlank = false;
            }

            if (builder.Length >= MaxTextLength)
            {
                break;
            }
        }

        return builder.ToString().TrimEnd();
    }
}
