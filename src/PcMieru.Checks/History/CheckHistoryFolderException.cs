using System.Globalization;

namespace PcMieru.Checks;

/// <summary>
/// The history folder itself could not be opened or enumerated (a permission error, an I/O error, a path that is not a folder or
/// is not valid). It is the one failure of a listing that is not a warning about a file: <see cref="IReadOnlyCheckHistory.ListAsync"/>
/// and <c>GetAsync</c> throw it, and the CLI answers it with exit 1. A folder that does not exist is not this: it is an empty history.
/// <see cref="Exception.InnerException"/> is the original error. The message names the exception type and HRESULT, never a path.
/// </summary>
public sealed class CheckHistoryFolderException : IOException
{
    public CheckHistoryFolderException(Exception inner)
        : base(Describe(inner), inner)
    {
        HResult = inner.HResult;
    }

    private static string Describe(Exception inner) =>
        string.Create(CultureInfo.InvariantCulture, $"The check history folder cannot be opened: {inner.GetType().Name} 0x{inner.HResult:X8}");
}
