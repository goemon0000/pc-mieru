using System.Runtime.InteropServices;
using System.Text;
using PcMieru.Swap;
using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;

return SwapMain.Run(args);

namespace PcMieru.Swap
{
    /// <summary>
    /// The entry of PCMieru.Swap.exe. The program itself is <see cref="SwapCli"/> (in PcMieru.Update, so that it is tested in-process); this is only what has to happen before it: the library search is fixed to
    /// System32, the console speaks UTF-8, and the exe's own FINAL path is found (the role of the process, Runner or Launcher, is decided from it).
    ///
    /// There is NO start gate call here, on purpose: the Launcher has to work while the gate says "recovery pending" (that is what it is for), and the Runner lives in I\update, where no gate is beside it.
    /// What the Runner does with the gate is the point of the program: it takes it exclusively (<c>InstallGate.EnterExclusive</c>) before it changes anything.
    /// </summary>
    internal static class SwapMain
    {
        public static int Run(string[] args)
        {
            // First of all: from here on this process only loads DLLs from System32 (the exe is often run from a folder a person unzipped).
            if (!NativeSearch.TryRestrictToSystem32())
            {
                return SwapExit.Crashed;
            }

            NativeSearch.Pin();

            try
            {
                Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }
            catch (IOException)
            {
                // no console to set it on (started without one): the writes below are then dropped by the runtime
            }

            var processPath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(processPath) || !Path.IsPathFullyQualified(processPath))
            {
                Console.Error.WriteLine("swap: the path of this program is not known");
                return SwapExit.WrongPlace;
            }

            var self = Path.Combine(InstallGate.InstallFolderOf(processPath), Path.GetFileName(processPath));
            return SwapCli.Run(args, new SwapHost { SelfPath = self, Out = Console.Out, Error = Console.Error });
        }
    }

    internal static partial class NativeSearch
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetDefaultDllDirectories(uint flags);

        public static bool TryRestrictToSystem32() => SetDefaultDllDirectories(0x00000800);

        /// <summary>
        /// The framework libraries look for the DLLs they load at run time next to the exe first (and in NativeAOT that is a real load), where a planted file would be picked up with our rights. Their loads are
        /// pinned to System32 by absolute path. The csproj check fails the publish if a library that is not pinned here brings in such a load.
        /// </summary>
        public static void Pin()
        {
            foreach (var assembly in new[]
            {
                typeof(object).Assembly, // System.Private.CoreLib: ntdll.dll, shell32.dll (both KnownDLLs, pinned all the same)
                typeof(System.Diagnostics.Process).Assembly, // advapi32.dll, kernel32.dll, ntdll.dll, shell32.dll
                typeof(System.Security.Cryptography.SHA256).Assembly, // bcrypt.dll (the hashes of the files)
                typeof(Microsoft.Win32.Registry).Assembly, // advapi32.dll (reached through the framework, the swap itself reads no registry)
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
}
