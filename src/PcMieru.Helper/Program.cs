using System.Runtime.InteropServices;
using System.Text.Json;
using PcMieru.Helper;
using PcMieru.Wire;

return HelperMain.Run(args);

namespace PcMieru.Helper
{
    /// <summary>
    /// Entry point of the elevated Helper. The request was fixed on the command line at ShellExecute time;
    /// nothing is read from stdin, the network or a config file. Exit code = <see cref="OperationCode"/>.
    /// The operation table is closed (<see cref="HelperOperations"/>): what is not in it is refused by the parser.
    /// A plain, non-elevated run is harmless: every operation then ends in "denied".
    /// </summary>
    internal static class HelperMain
    {
        private const uint LoadLibrarySearchSystem32 = 0x00000800;

        public static int Run(string[] args)
        {
            // First of all: from here on this process only loads DLLs from System32 (the exe is often unpacked in
            // Downloads and elevated from there). If that does not take effect, do not go on as administrator.
            if (!NativeSearch.TryRestrictToSystem32())
            {
                return (int)OperationCode.Error;
            }

            NativeSearch.Pin();

            if (!HelperRequest.TryParse(args, out var request, out var error) || request is null)
            {
                Console.Error.WriteLine(error);
                return (int)OperationCode.InvalidArguments;
            }

            var opName = HelperOperations.Get(request.Op).Name;
            ActionResult result;
            try
            {
                result = HelperExecutor.Execute(request, HelperEnvironment.Real());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = ActionResult.Of(OperationCode.Error, MessageKeys.ProcessError, ("code", ex.GetType().Name));
            }

            ResultFile.Write(request.ResultPath, result.ToHelperResult(request.Nonce, opName));
            return (int)result.Code;
        }
    }

    internal static partial class NativeSearch
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetDefaultDllDirectories(uint flags);

        public static bool TryRestrictToSystem32() => SetDefaultDllDirectories(0x00000800);

        /// <summary>
        /// The framework libraries look for the DLLs they load at run time next to the exe first (and in NativeAOT that is a
        /// real load), where a planted file would be picked up with our rights. Their loads are pinned to System32 by absolute
        /// path. The csproj check fails the publish if a library that is not pinned here brings in such a load.
        /// </summary>
        public static void Pin()
        {
            foreach (var assembly in new[]
            {
                typeof(object).Assembly, // System.Private.CoreLib: ntdll.dll, shell32.dll (both KnownDLLs, pinned all the same)
                typeof(Microsoft.Win32.Registry).Assembly,
                typeof(System.Diagnostics.Process).Assembly,
                typeof(System.Security.AccessControl.ObjectSecurity).Assembly,
                typeof(System.Security.Principal.WindowsIdentity).Assembly,
            })
            {
                NativeLibrary.SetDllImportResolver(assembly, FromSystem32);
            }
        }

        private static IntPtr FromSystem32(string name, System.Reflection.Assembly assembly, DllImportSearchPath? paths)
        {
            var file = Path.GetFileName(name);
            if (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                file += ".dll";
            }

            // Not found = an exception. It never falls back to the default search.
            return NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, file));
        }
    }

    /// <summary>
    /// Writes the result into the file the caller made beforehand, for display only (the caller trusts the exit code).
    /// It never creates a path, and it refuses a file that is not exactly what a fresh pre-created file looks like:
    /// not empty, a link, or below a link.
    /// </summary>
    internal static class ResultFile
    {
        public static void Write(string path, HelperResult result)
        {
            try
            {
                if (!IsSafeTarget(path))
                {
                    return;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                if (stream.Length != 0)
                {
                    return;
                }

                JsonSerializer.Serialize(stream, result, WireJsonContext.Default.HelperResult);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static bool IsSafeTarget(string path)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length != 0 || (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                return false;
            }

            for (var directory = file.Directory; directory is not null; directory = directory.Parent)
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
