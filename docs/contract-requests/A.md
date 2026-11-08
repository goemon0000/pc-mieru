# Area A (Collection) - contract requests and hand-off notes

Nothing here has been applied. Every item that touches a frozen contract is an exact proposed diff for the integrator.
Items marked "note" change no contract; they tell B, C, D, E what area A actually serves.

> **Integration status (branch `integrate`).** The two paragraphs above describe the state when A wrote this file.
>
> | Item | Result | Commit |
> |---|---|---|
> | 1. `ProcessGroup` on `ProcessInfo` | **Declined.** The verdict is asynchronous (`NotCollected` while it is worked out), so a copy inside a snapshot would be a tick stale; it would add about 100 bytes per process to every snapshot, history entry and MCP reply. The live lookup is the contract instead. | fe5740e |
> | 2. `IProcessGroupSource`, `IProcessIconSource`, `ProcessGroup` | Moved into `Core/Contracts` (same namespace). `PcMieruServices.ProcessGroups` / `ProcessIcons` expose them; the GUI uses them for grouping and the program icons. | fe5740e |
> | 2. `SamplerLoop` | Registered by `CollectionModule` in both modes and reachable as `PcMieruServices.Sampler`. The GUI pump is a view of the loop; reports and diagnosis read `SamplerLoop.History`. `ISamplerControl` / `SamplerLoop` stay public in `Core/Collection`. | 02d95f7 |
> | 3. "No status carrier on lists" | Applied in another form: `SystemInfo` has `DisksState` / `NicsState` / `GpusState` (`Value<int>`) beside the lists instead of wrapping the lists. Fixtures regenerated, only those three objects added. | 4b17b5d |
> | 4. Sampler cost | No change by the integrator (the default interval is still B's / SPEC's decision). | - |
> | 5. `ForceFakes` in shared tests | Kept everywhere. | - |
>
> Not done: the GUI never sets `GpuDefaultOn = false` (section 2 recommends it), so the GPU counters are sampled all the time.

## 1. Request: `ProcessGroup` on `ProcessInfo` (optional, not needed to ship)

Today grouping (SPEC 5.2: Apps / Windows processes / Background processes) is answered by `IProcessGroupSource`, a type
that lives in `Core/Collection/` and is registered next to `ISnapshotSource` (`services.Get<IProcessGroupSource>()`).
It returns `Value<ProcessGroup>` per `ProcessKey`: `NotCollected` while the verdict is still being worked out (shown as
"checking"), `Gone` for a key that is not in the latest sample. Because it is a separate lookup, B can adopt it without
any contract change. If the integrator prefers the group inside the snapshot (one source of truth, history keeps it,
JSON carries it), the diff is:

```diff
--- a/src/PcMieru.Core/Contracts/ProcessInfo.cs
+++ b/src/PcMieru.Core/Contracts/ProcessInfo.cs
@@
     Value<double> GpuPercent,
-    Value<ProcessState> State);
+    Value<ProcessState> State,
+    Value<ProcessGroup> Group);
```

and `ProcessGroup` (currently in `Core/Collection/ProcessGrouping.cs`, already carrying `CamelCaseEnumConverter`) moves to
`Core/Contracts/`, plus `Value<ProcessGroup>` is added to `PcMieruJsonContext`. Every `new ProcessInfo(...)` call site in
Fakes, tests and the CLI gets one more argument, which is why this is a request and not something A does alone.
Until then A keeps `IProcessGroupSource` and nothing else breaks.

## 2. Note: what A registers (for B and E)

`PcMieruServices.Create(options)` without `ForceFakes` now serves the real objects. All are plain `services.Get<T>()`:

| Registered as | Real type | Notes |
|---|---|---|
| `ISnapshotSource` | `WindowsSnapshotSource` | same instance also answers `IProcessGroupSource` |
| `IProcessGroupSource` | the same object | grouping follows the latest sample |
| `IProcessDetailsProvider` | `ProcessMetadataProvider` | lazy, cached, never samples |
| `IProcessIconSource` | `ProcessIconSource` | `GetPngAsync(path, size)`; `ProcessDetails` has no icon field, so icons come from here |
| `ISamplerControl` | `SamplerSettings` | registered in both modes (Fakes too) |

With `ForceFakes = true` the four Fakes are served (`FakeSnapshotSource`, `FakeProcessDetailsProvider`,
`FakeProcessGroupSource`, `FakeProcessIconSource`) and `FakeAreas` lists them. There is no silent fallback from real to
fake: a real source that cannot measure says `Denied` / `Unsupported` / `Error` in the value.

`SamplerLoop` (public, `Core/Collection/SamplerLoop.cs`) is the timer B's `SnapshotPump` can use instead of its own:
`new SamplerLoop(services.Snapshots, services.Get<ISamplerControl>(), clock)`, then `Start()`. It gives the 60-point
history ring (`History`, full snapshots, oldest first), `Latest`, `Sampled` / `Failed` events, `RequestSampleNow()` for the
Refresh button, beat-based timing (no drift; one overrun costs a quarter-interval rest), and it resets the baseline on
Start and on resume from pause. A listener that throws is contained. Nothing in the contracts changes.

GPU is the one costly optional piece: a GUI should create the registry with `GpuDefaultOn = false` and hold
`SubscribeGpu()` (dispose to release) while a GPU column or tab is visible. While nobody subscribes the snapshot says
`NotCollected` for GPU instead of paying for it.

## 3. Note: facts a consumer must know

- **Signer string.** `SignatureInfo.Signer` is the certificate display name as Windows reports it ("Microsoft Windows",
  ".NET", "Adobe Inc."), not a subject DN. E's `IKnownProcessCatalog` should match signers with "contains", not equality.
- **Signature states seen on this PC:** embedded, catalog-signed (the common case in System32), unsigned, and one
  `Invalid` (an installed Adobe helper whose file changed after signing: `0x80096010`). Verification is offline
  (no revocation lookups, no network).
- **Command line.** Kernel-owned processes (System, Registry, Memory Compression, Secure System) have no user-mode
  environment, so the command line is `Unsupported`; a virtualization-protected process (LsaIso) is `Denied`. `Error` is
  left for real faults. The Idle process (PID 0) is `Denied` for every handle-based fact.
- **GPU.** `GpuInfo.Id` is `luid_0x{high:x8}_0x{low:x8}` (the text DXGI and the sensor host use). Engines are the busiest instance per engine type (3D, Copy,
  VideoDecode, ...). On the first tick every non-software adapter is `NotCollected` (the first PDH read has no
  baseline).
- **No status carrier on lists.** `SystemSample.Disks`, `Nics`, `Gpus` are plain lists. "The PDH disk counters could not
  be created" is therefore an empty list, not a `Value`. Proposed (not applied): wrap them as `Value<IReadOnlyList<...>>`.
  Affects B (bindings), E (output), D (reports); that is why it is a request and not done here.
- **NICs** come from `GetIfTable2`; `GetAdaptersAddresses` is not used (A does not report IP addresses, which the SPEC
  assigns to C's hardware model).
- **A cached process that later exits** keeps its cached stable facts (path, version, signature); only Efficiency, which is
  read live, becomes `Gone`.
- **CPU percentages are statistical.** The kernel accounts process CPU in 15.6 ms clock ticks, so at a 1 s interval a
  single process reads in steps of about 1.5 percent of one core. Averages over several ticks are accurate (measured
  below); a single tick of a light process is not.
- **Unattributed time.** Interrupt and DPC time belongs to no process, so the sum of all process CPU is 93-98 percent of the
  system total (measured).

## 4. Finding: SPEC 11 sampler cost target is not met by the sampler alone

SPEC 11 aims at about 10 ms of CPU per second (one core) for the whole app at the default interval. Measured on this PC
(32 logical processors, 570-580 processes, about 9,500 threads), release build, quiet machine:

- Whole tick: 20-24 ms CPU, about 15.5 ms wall, about 190 KiB allocated.
- With other agents compiling at the same time it rises to 27-55 ms per tick.
- Wall breakdown of a tick: the kernel call `NtQuerySystemInformation(SystemProcessInformation)` about 15 ms (it hands back a
  1.3 MB buffer and walks every thread; the other stages overlap with it); NIC table 3.7 ms; window probe 1.4 ms; PDH 0.6 ms;
  GPU 0.6 ms; memory 0.5 ms; parse 0.6 ms; deltas 0.3 ms; system CPU 0.2 ms.

A did not find a cheaper source of per-process CPU and I/O than this one call (the alternative, a handle and a query per process, was not measured and means about 570 handles per tick). The levers
are: default interval 2 s (halves the cost; SPEC offers 0.5 / 1 / 2 / 5 s), or re-baselining SPEC 11 for 500+ process
machines. A can still save about 2.5 ms of the 3.7 ms NIC cost with per-interface `GetIfEntry2` caching, but that does not
change the picture. Decision for the integrator; A did not change the default interval, which is B's / SPEC's.

History memory: 60 snapshots of 577 processes retain 9.7 MiB (166 KiB each), measured after a forced GC.

## 5. Merge note: two shared test files were edited

`tests/PcMieru.Core.Tests/ServicesTests.cs` and `tests/PcMieru.Core.Tests/StringsTests.cs` test the Fakes through
`PcMieruServices.Create`. Once a module registers a real implementation by default, those tests have to ask for
`new PcMieruOptions { ForceFakes = true }`. A made that edit for its own module. C, D and E will make the same one-line edits
in the same places, so expect small textual conflicts there; the resolution is to keep `ForceFakes = true` everywhere.

## 6. Provenance

All code in `Core/Collection/` and `Core.Tests/Collection/` was written fresh for this project (no copied sources). No new
NuGet packages are needed: every native call (ntdll, kernel32, pdh, dxgi, iphlpapi, wintrust, user32, shell32) is a
`LibraryImport` P/Invoke.
