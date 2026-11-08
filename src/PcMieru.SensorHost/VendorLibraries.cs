using System.Runtime.InteropServices;

namespace PcMieru.SensorHost;

/// <summary>
/// Finds and loads a GPU vendor's monitoring DLL. Only fixed, administrator-owned places are looked at, and always by the full path: System32
/// first (where the vendor's driver puts it), then the driver store's own folders. Never the current directory, never PATH, never the folder
/// of this program, so a DLL of the same name dropped next to the app is not picked up.
/// </summary>
internal static class VendorLibraries
{
    private const int MaxDriverStoreFolders = 8;

    /// <param name="dllName">File name only, for example nvml.dll.</param>
    /// <param name="driverStorePatterns">Folder name patterns inside the driver store, for example nv*.inf_amd64_*.</param>
    /// <param name="extraPaths">Other well-known full paths, tried after System32.</param>
    public static IEnumerable<string> Candidates(string dllName, string[] driverStorePatterns, params string[] extraPaths)
    {
        yield return Path.Combine(Environment.SystemDirectory, dllName);
        foreach (var extra in extraPaths)
        {
            yield return extra;
        }

        var repository = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository");
        var folders = new List<DirectoryInfo>();
        foreach (var pattern in driverStorePatterns)
        {
            try
            {
                folders.AddRange(new DirectoryInfo(repository).EnumerateDirectories(pattern, SearchOption.TopDirectoryOnly));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // No driver store to look in: System32 was the place.
            }
        }

        // Newest driver first: an older copy of the same DLL may not match the running kernel driver.
        foreach (var folder in folders.OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxDriverStoreFolders))
        {
            yield return Path.Combine(folder.FullName, dllName);
        }
    }

    /// <summary>Loads the first candidate that exists and is a loadable DLL. The path of the one that worked is returned for the log line.</summary>
    public static bool TryLoad(IEnumerable<string> candidates, out IntPtr library, out string? path)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out library))
                {
                    path = candidate;
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                // Try the next place.
            }
        }

        library = IntPtr.Zero;
        path = null;
        return false;
    }
}
