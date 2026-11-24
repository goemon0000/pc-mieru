using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcMieru.Update.Transaction;

/// <summary>
/// Whether this process runs with an ELEVATED token (started with "run as administrator", or by an installer that did). The question is the token's own TokenElevation flag, not "is the user an administrator"
/// (a person who is an administrator but runs a normal window has a filtered token and is NOT elevated).
///
/// <b>Why it matters here.</b> An elevated swap or recovery would write files that the normal-rights start of PC Mieru can no longer replace (the owner of a new file is the administrators group, an ACL is inherited from
/// an elevated creator), and an elevated recovery would also reach files that the person never expected a program of theirs to touch. So an update, and a recovery with it, runs only with normal rights, and the
/// swapper says so (<see cref="SwapExit.Elevated"/>) instead of trying.
///
/// Library-import P/Invoke (the swapper is NativeAOT); the DLL is resolved from System32 only. If the token cannot be read the answer is false (a process can always open its own token, so this does not happen in
/// practice; the check protects the person from a surprise, it is not a gate against an attacker).
/// </summary>
public static partial class ProcessElevation
{
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out SafeFileHandle token);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(SafeFileHandle token, int informationClass, out int information, int length, out int returned);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    /// <summary>
    /// True when the token of this process is elevated.
    ///
    /// <b>The forced build.</b> A hosted CI runner (GitHub's Windows runner) runs with an elevated token, and a developer's machine does not, so a test that reads the real token passes on one and fails on the other.
    /// A build with <c>-p:PcmForceElevated=true</c> (the compile symbol PCM_FORCE_ELEVATED, set in PcMieru.Update.csproj and nowhere else) answers true here, in every program that is built from this project, so
    /// the tests can be run locally in the state the runner is in, without ever running elevated. It is a build of the TEST runs only: the csproj stops a publish that has it, and TestBuildSeparationTests says
    /// the symbol is in this one place.
    /// </summary>
    public static bool IsElevated()
    {
#if PCM_FORCE_ELEVATED
        return true;
#else
        return TokenIsElevated();
#endif
    }

    private static bool TokenIsElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token) || token.IsInvalid)
        {
            return false;
        }

        using (token)
        {
            return GetTokenInformation(token, TokenElevationClass, out var elevated, sizeof(int), out _) && elevated != 0;
        }
    }
}
