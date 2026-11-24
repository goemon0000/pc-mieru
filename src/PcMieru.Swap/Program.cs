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
    ///
    /// There is no test entry here either. The test build of the runner (tests/PcMieru.Swap.TestBuild) compiles this very file with the symbol PCM_SWAP_TESTBUILD, which routes the command line through its own entry first.
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
            var host = new SwapHost
            {
                SelfPath = self,
                Out = Console.Out,
                Error = Console.Error,
#if PCM_SWAP_TESTBUILD
                // The test build can be told another L (a scratch folder) for the install it is run in; the product cannot (the system's answer is the only one).
                LocalFolder = PcMieru.Swap.TestBuild.TestEntry.LocalFolder(self, Environment.GetEnvironmentVariable),
                IsElevated = PcMieru.Swap.TestBuild.TestEntry.IsElevated(self, Environment.GetEnvironmentVariable),
#endif
            };
#if PCM_SWAP_TESTBUILD
            // Only tests/PcMieru.Swap.TestBuild defines the symbol (the product's csproj stops the build if it is defined there): the test-only entry lives in that project, never in the product.
            return PcMieru.Swap.TestBuild.TestEntry.Run(args, host, Environment.GetEnvironmentVariable);
#else
            return SwapCli.Run(args, host);
#endif
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
