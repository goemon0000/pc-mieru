# Contract requests and notes from area D (Management + Helper)

Nothing in Core/Contracts, Core/Localization, Core/Services, PcMieruJsonContext.cs, Directory.Build.props,
Directory.Packages.props, the slnx, CI or tools/ was edited. No new NuGet package. No change to tools/publish.ps1
is needed (see 5). The requests below are small and none of them blocks the merge.

## 1. REQUIRED for the integrator: tests that call management operations must use ForceFakes

`ManagementModule.Register` now fills the slots with REAL implementations unless `PcMieruOptions.ForceFakes` is set.
A test (any area) that creates `PcMieruServices` with default options and then calls `ProcessControl.TerminateAsync`,
`Startup.SetEnabledAsync`, `Services.StartAsync`, `Tasks.Run`, `Helper.RunAsync` ... acts on THIS PC (the Fake knew a
"notepad.exe"; the real one would end a real notepad, or toggle a real startup entry, or show a UAC prompt).
Read-only calls (`ListAsync`, `Evaluate`) are harmless.

Already done by me, in a shared test file (please keep it; diff against 2ccce94):

```diff
--- a/tests/PcMieru.Core.Tests/ServicesTests.cs
+++ b/tests/PcMieru.Core.Tests/ServicesTests.cs
+    private static PcMieruServices CreateFakes() =>
+        PcMieruServices.Create(new PcMieruOptions { ForceFakes = true, Clock = static () => Fixture.FixedTime });
 ...
-        using var s = Create();      // 5 tests that act on protected/killable processes, the startup toggle and the helper
+        using var s = CreateFakes();
```

Checked: `tests/PcMieru.Cli.Tests/Harness.cs` and `tests/PcMieru.Gui.Tests` already pass `ForceFakes = true`. The real CLI
(`CliApp.DefaultFactory`) and the real GUI (`Program.cs`) use the real services, which is the intent. Agents B and E
should keep ForceFakes in every automated test they add.

## 2. New interfaces (own folder, no contract change)

`Core/Management/ManagementInterfaces.cs`; resolve with `services.Get<T>()` (already public on PcMieruServices):

| Interface | For |
|---|---|
| `IElevatedProcessActions` | "retry as administrator" after a `Denied`: the same 3 process operations through the Helper, one UAC prompt per call; the Helper repeats the protection and identity checks |
| `IProcessAffinityControl` | `QueryAffinity` / `SetAffinityAsync` (never via the Helper) |
| `IShellActions` | `RestartExplorerAsync` (refused while PC Mieru is elevated) |
| `IStartupUndo` | `HasUndo(id)` / `UndoAsync(id)`; the real `StartupManager` is registered as both `IStartupManager` and `IStartupUndo` (one object) |

Message texts: `mgmt.*` keys (`ManagementStrings`, ja+en, registered by `ManagementModule.Register`). Wire's
`MessageKeys` (also `mgmt.*`) are registered the same way; a test checks that no key is defined twice.

## 3. Behaviour front ends should know

- `HelperClient.RunAsync` ignores the nonce/result path of the request it is given and makes its own (callers pass
  placeholders). The exit code is the outcome; the result file is only read for its wording when its nonce AND op match.
- A request the Helper would refuse (bad name, non-HKLM startup provider, missing value) is refused by the client
  BEFORE the UAC prompt (`InvalidArguments`, text `mgmt.helper.bad-request`).
- The UAC prompt is modal; `ct` cannot cancel it, only the wait after it (the elevated Helper is then left to finish).
- `smart-read` / `nvme-health` return raw bytes as base64 in `ActionResult.Data` (keys in `HelperDataKeys`:
  `smart.data`, `smart.thresholds`, `smart.source`, `smart.predict-failure`, `nvme.health`, `nvme.errors`, `bus`).
  Agent C's parsers read these; the integrator wires them. Not exposed through any interface yet.
- Efficiency mode off needs the previous values: they travel in `ActionResult.Data` (`prev-priority`, `prev-control`,
  `prev-state`, `prev-known`) and are kept in `%LOCALAPPDATA%\PCMieru\eco-state.json`. A startup switch keeps its undo
  record in `%LOCALAPPDATA%\PCMieru\startup-undo.json` (first record per entry wins). Nothing is written until the
  first operation.
- Services: Start, Stop and Restart ALWAYS go through the Helper (one UAC prompt each) unless PC Mieru already runs
  elevated. Stop refuses while dependents are running (the list is shown in the message); it does not stop them.
- Startup: only Run (HKCU/HKLM/HKLM32) and the two Startup folders toggle (StartupApproved, never the Run value or the
  file). Scheduled tasks and packaged apps are list-only (`CanToggle = false`). `\Microsoft\Windows*` tasks are left out
  of the list on purpose (hundreds of OS tasks, not startup items for a user). Machine-side state is read from the
  machine-side StartupApproved only (the per-user override of a machine entry is not modelled; unverified).
- An unknown StartupApproved value (anything but 2/6 or 3/7 in a 12-byte value) is shown as `Unknown` and refused for
  toggling, not guessed.
- `ListAsync` of services/startup has no error channel in the frozen interface: an unreadable place is an empty place.

## 4. Helper project (src/PcMieru.Helper) for the integrator

- NativeAOT, 1.9 MB, references only Wire, `asInvoker` manifest (elevated only by `runas` from Core; a plain run is
  harmless and refuses everything it cannot do). `/DEPENDENTLOADFLAG:0x800` is in the exe (verified with dumpbin).
- The csproj contains a post-link check (`CheckLazyNativeModules`, copied from pl-sensors, MIT, same owner; provenance
  comment at the top of the file) that fails the publish if an assembly brings a lazy native load that also searches the
  exe's folder. `S_P_CoreLib` and the pinned list are in `PinnedNativeSearchAssemblies`; `Program.cs` (`NativeSearch.Pin`)
  pins them to System32. Needs `vswhere.exe` on PATH for the AOT link (publish.ps1 does this: `Add-VswhereToPath`).
- Imports seen in the published exe: ADVAPI32, bcrypt, KERNEL32, ole32 and api-ms-win-crt-*; all already in
  `$AllowedHelperImports` / the api-ms pattern, so no publish.ps1 change is requested.
- The Helper writes its result ONLY into a pre-created, empty, non-reparse-point file named `<nonce>.json` on a local
  drive, and refuses a result path that has a reparse point in any parent.
- Wire got `InternalsVisibleTo PcMieru.Core.Tests` and `AllowUnsafeBlocks` (own csproj).
- Portable installs: if the app folder is user-writable, a swapped `PCMieru.Helper.exe` would be elevated by the user's
  own click. The Helper is not signature-checked before `runas`. For the installer/zip this is a packaging decision
  (an install under Program Files closes it); noted as a risk, no request.

## 5. Left for later / not verifiable on a development PC

- Elevated paths were never run for real: UAC prompt, service start/stop/restart (logic tested with fakes and a staged
  clock only), Explorer restart (fake host only), `runas` of a launched task, HKLM StartupApproved writes (done only in
  a temp HKCU key `Software\PcMieruTest\<guid>`, deleted by every test).
- `WindowOperations.CloseWindow` (EnumWindows under AOT) and the `ScmServiceApi` struct layouts are exercised only by
  the non-AOT test build.
- Non-elevated SMART SATA behaviour: the test session itself was elevated.
