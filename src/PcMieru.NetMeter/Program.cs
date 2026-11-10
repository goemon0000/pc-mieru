using System.Runtime.InteropServices;
using PcMieru.NetMeter;
using PcMieru.Wire;

return MeterMain.Run(args);

namespace PcMieru.NetMeter
{
    /// <summary>
    /// Entry point of the elevated network meter. Everything it is asked to do was fixed on the command line by the front end
    /// (<see cref="NetMeterArguments"/>, parsed strictly); nothing is read from stdin, the network or a config file. A plain,
    /// non-elevated run is harmless: it cannot start the ETW session and says so over the pipe, and nobody but the front end
    /// that started it is ever served.
    /// </summary>
    internal static class MeterMain
    {
        public static int Run(string[] args)
        {
            // First of all: from here on this process only loads DLLs from System32 (the exe sits in the app folder, which
            // a user can often write to, and it is elevated). If that does not take effect, do not go on.
            if (!NativeSearch.TryRestrictToSystem32())
            {
                return MeterExit.NativeSearch;
            }

            NativeSearch.Pin();

            if (!NetMeterArguments.TryParse(args, out var arguments, out _) || arguments is null)
            {
                return MeterExit.InvalidArguments;
            }

            try
            {
                return MeterHost.Run(arguments);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return MeterExit.Internal;
            }
        }
    }

    internal static partial class NativeSearch
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetDefaultDllDirectories(uint flags);

        public static bool TryRestrictToSystem32() => SetDefaultDllDirectories(0x00000800);

        /// <summary>
        /// The framework library looks for the DLLs it loads at run time next to the exe first (and in NativeAOT that is a real
        /// load), where a planted file would be picked up with our rights. Its loads are pinned to System32 by absolute path.
        /// Only System.Private.CoreLib is used here (no Process, Registry or Security classes); the csproj check fails the
        /// publish if a library that is not pinned here brings in such a load.
        /// </summary>
        public static void Pin() => NativeLibrary.SetDllImportResolver(typeof(object).Assembly, FromSystem32);

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
