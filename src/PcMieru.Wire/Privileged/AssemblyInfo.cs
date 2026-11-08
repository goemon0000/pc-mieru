using System.Runtime.InteropServices;
using System.Runtime.Versioning;

// Every P/Invoke in this assembly loads its DLL from System32 only (the Helper runs elevated, often from a folder
// the user can write to). The Helper adds SetDefaultDllDirectories and a resolver for the framework's own imports.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
[assembly: SupportedOSPlatform("windows")]
