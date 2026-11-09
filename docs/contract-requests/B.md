# Area B (GUI) - contract requests and hand-off notes

Nothing here has been applied. B edited nothing in `Core/Contracts/`, `Core/Localization/`, `Core/Services/`,
`PcMieruJsonContext.cs`, any csproj, props, slnx, CI or tools. No NuGet package is needed. Every item below says what the
GUI does today without the change (the "local workaround"), so none of them blocks the merge. Items 1 and 2 give the most.

> **Integration status (branch `integrate`).** "Nothing here has been applied" was true when B wrote the file. Now:
>
> | Item | Result | Commit |
> |---|---|---|
> | 1. Icons, grouping, sampler loop | Applied as proposed: the three types moved into `Core/Contracts`; the process list and detail pane show program icons (path-keyed, bounded, off-thread cache) and group by `IProcessGroupSource`, the local rule is only the fallback while the source cannot answer. `SamplerLoop` is `PcMieruServices.Sampler` and the GUI pump is a view of it. | fe5740e, 02d95f7 |
> | 2. Per-call redaction, honest `Redacted` | Applied, a little wider than proposed: `IRedactor.RedactText/RedactJson(.., bool redact)` and `IReportBuilder.RenderMarkdown/RenderJson` are default members; `ReportDocument.Redacted` is stamped with what the render really did. Volume label, audio name and USB name are blanked by their place in the tree, so the Markdown is covered too. The CLI report and the GUI use them instead of the local workaround. | 0a79a17 |
> | 3. CPU affinity | Applied in the GUI through D's `IProcessAffinityControl` (already in the tree), not through a new `IProcessControl` member: "Set CPU affinity..." check-list dialog, "back to all", protected processes refused before the dialog and again by the API. No `AffinityMask` in `ProcessDetails`, so the detail pane still has no affinity line. | a7b686c |
> | 4. CPU temperature | Contract added (`CpuInfo.TemperatureC`, trailing init property). The real sampler answers `Unsupported` with a reason, because no kernel driver is shipped; there is no GUI row for it. | 4eac55b |
> | 5. `PeekMany` | Applied (default member returns null for every key; `ProcessMetadataProvider` answers from its caches and starts the rest in the background). No GUI column uses it yet. | 1e482d0 |
> | 6. Hardware cache invalidation | Applied: `MainForm.WndProc` calls `IHardwareProvider.Invalidate()` on `WM_DEVICECHANGE`. The Hardware page does not reload by itself; the next read is fresh. | a3935df |
> | 7. One sampler, two consumers | Resolved: reports and diagnosis read the loop's `History` while it runs; otherwise (pcmi, MCP, paused) they sample the source themselves. The "largest unverified point" is covered by `ReportFromLoopTests` (a counting source proves nothing else samples) and a smoke check. | 02d95f7 |

## 1. Request: process icons, grouping and the sampler loop live outside the contracts

A's area (see `A/docs/contract-requests/A.md` section 2) registers three public types that are not in `Core/Contracts/`:
`IProcessGroupSource` (Apps / Windows / Background per `ProcessKey`), `IProcessIconSource` (`GetPngAsync(path, size)`)
and `ISamplerControl` + `SamplerLoop`. B was told to code only against the contracts, and those types do not exist in
the tree B works in (the Fakes of `FakeCollection.cs` only), so the GUI cannot use them yet.

**Local workaround, in place.**
- Grouping: `Gui/Data/WindowProbe.cs` (`EnumWindows`: visible, titled, unowned, not a tool window, not cloaked) plus
  `ProcessClassifier`. The first call always reads; then every second call. SPEC 5.2 says "signed, in the Windows folder
  = Windows"; the GUI cannot check a signature per row, so Windows = session 0 or a well-known Windows name
  (`svchost.exe`, `dwm.exe`, ...). It can therefore differ from A's verdict for a look-alike with the same name. The
  help text calls the rule the app's own, as SPEC 5.2 asks.
- Sampling: `Gui/Data/SnapshotPump.cs` (a WinForms timer, one sample in flight, 5 s while minimized, pause, resume drops
  the baseline through `ISnapshotSource.ResetBaseline()`).
- Icons: none. The process list has no icon column and the detail pane shows no icon. Nothing is drawn as a guess.

**Proposed diff.** A pure move of the three interfaces (and `ProcessGroup`) into the contracts, so that the GUI, the CLI
and the MCP server can ask for them without a project-internal reference. Exact signatures are A's, copied from
`A/src/PcMieru.Core/Collection/`:

```diff
--- a/src/PcMieru.Core/Contracts/Interfaces.cs
+++ b/src/PcMieru.Core/Contracts/Interfaces.cs
@@
 public interface IProcessDetailsProvider
 {
     /// <summary>Lazy, cached per ProcessKey. Never throws for a vanished process: the values come back as Gone.</summary>
     Task<ProcessDetails> GetAsync(ProcessKey key, CancellationToken ct = default);
 }
+
+/// <summary>Moved from Core/Collection. Cheap and safe from any thread; Gone for a key not in the latest sample.</summary>
+public interface IProcessGroupSource
+{
+    Value<ProcessGroup> GroupOf(ProcessKey key);
+}
+
+/// <summary>Moved from Core/Collection. PNG, 8-bit RGBA, cached by path, size and write time.</summary>
+public interface IProcessIconSource
+{
+    Task<Value<byte[]>> GetPngAsync(string path, int size = 32, CancellationToken ct = default);
+}
```

`ProcessGroup` (with its `CamelCaseEnumConverter`) moves with them; `ISamplerControl` can stay in `Core/Collection`
because only the pump needs it. Once merged, the GUI change is small: `ProcessTableModel.Update(snapshot, classify)`
already takes the classifier as a delegate (`key => groupSource.GroupOf(key)`), and `ProcessDetailPane` gets an
`Image` from `GetPngAsync(path, 32)` (the path is already read from `ProcessDetails`). A shows "checking" while a verdict
is `NotCollected`; the GUI would show the process under Background until then, as it does today for an unknown.

## 2. Request: a per-call redaction switch, and an honest `ReportDocument.Redacted`

`IRedactor` says "there is no per-call switch" and `ReportBuilder.BuildAsync` writes `Redacted: _redactor.Enabled` into
the document. The report page has a check box "hide personal info" (default on). Today the GUI:
- builds the document once (`BuildAsync`), then redacts the JSON tree (`RedactJson`) and the Markdown text
  (`RedactText`) only when the box is on, so switching it needs no second 10-second observation;
- overwrites `Redacted` on its own copy (`document with { Redacted = redact && redactor.Enabled }`), so a plain copy never
  says "redacted".

That works, but the redaction of the Markdown is by text content only (the user name, PC name, and the like), because
`RedactText` has no keys. Hardware serial numbers, MAC addresses and IP addresses are blanked in the JSON by key name
(`serialNumber`, `macAddress`, `ipAddresses`), but in the Markdown they are blanked only if they match the text patterns.
`C/docs/contract-requests/C.md` section 5 asks for the same kind of thing for `volumes[].label`.

Proposed (small and compatible, a default member so no implementer changes):

```diff
--- a/src/PcMieru.Core/Contracts/Interfaces.cs
+++ b/src/PcMieru.Core/Contracts/Interfaces.cs
@@
 public interface IReportBuilder
 {
     Task<ReportDocument> BuildAsync(ReportOptions options, CancellationToken ct = default);
+
+    /// <summary>The Markdown of the same document, redacted by key from the typed model (not from the finished text).</summary>
+    string RenderMarkdown(ReportDocument document, Language language, bool redact) => throw new NotSupportedException();
 }
```

If E prefers, the same result is `ReportMarkdown.Render(document, language)` taking the redactor and applying it to the
values as it writes them. Until then the GUI's Markdown is redacted as text; the report page shows the line "personal
info hidden" only for what it really did, and the redaction test (`DataAndPagesTests`) pins that the redactor is called
for both forms.

## 3. Request: CPU assignment (affinity) has no place in `IProcessControl`

SPEC 5.2 lists "CPU assignment (v1: hard affinity within one processor group)" in the process menu. `IProcessControl` has
priority and efficiency mode but no affinity method, so the GUI has no such menu item. **Not implemented in the GUI**; no
dialog, no button, nothing that looks as if it worked.

```diff
--- a/src/PcMieru.Core/Contracts/Interfaces.cs
+++ b/src/PcMieru.Core/Contracts/Interfaces.cs
@@
     Task<OperationResult> SetEfficiencyModeAsync(ProcessKey key, bool enabled, CancellationToken ct = default);
+
+    /// <summary>Hard affinity inside the process's one processor group. Bit n = logical processor n of that group.</summary>
+    Task<OperationResult> SetAffinityAsync(ProcessKey key, ulong mask, CancellationToken ct = default) =>
+        Task.FromResult(OperationResult.Fail(OperationCode.Unsupported, "affinity is not implemented"));
```

(`OperationCode.Unsupported` is used if the code exists; otherwise the member is abstract and D adds it.) D has to
refuse protected processes and to remember the old mask for the undo, as for the efficiency mode. After it lands, B adds
a check-list dialog "which CPUs may this process use" (one box per logical processor, at most 64) and a "back to all"
button. This is also why the detail pane has no affinity line: the current mask is not in `ProcessDetails` either
(add `Value<ulong> AffinityMask` to `ProcessDetails` in the same change if the line is wanted).

## 4. Request: CPU temperature

`CpuInfo` has load and clock but no temperature. The Performance CPU page and the Overview card show none; they do not
show a made-up zero or a "0 C". GPU temperature is shown where `GpuInfo.TemperatureC` has a value.

C's hardware area (SensorHost, no kernel driver) cannot read a CPU package temperature from user mode on most boards,
so this request is only a place to put it if C or a later version finds a source. Follow E's pattern (an init property
with a default, so no constructor call site changes):

```diff
--- a/src/PcMieru.Core/Contracts/Snapshot.cs
+++ b/src/PcMieru.Core/Contracts/Snapshot.cs
@@
 public sealed record CpuInfo(
     int LogicalProcessors,
     Value<double> TotalPercent,
     IReadOnlyList<Value<double>> PerLogicalPercent,
-    Value<double> CurrentMhz);
+    Value<double> CurrentMhz)
+{
+    /// <summary>NotCollected / Unsupported while there is no honest source. Never a guess.</summary>
+    public Value<double> TemperatureC { get; init; } = Value.NotCollected<double>("none");
+}
```

JSON gains `"temperatureC"` on `cpu`; golden fixtures that contain a `CpuInfo` need regenerating (same side effect as E's
request for `GpuInfo`). The Performance page then adds one row: `PerfMetrics` already has the line builder for the GPU.

## 5. Request (low priority): list data per process without 570 detail calls

The process list columns come from `ProcessInfo`; so the Name column shows the exe name, not the product name that
SPEC 5.2 puts first (a deviation, caused by this item). "User", "Architecture", "Efficiency mode", "Command line" and the
"Product" name need `ProcessDetails`, which is one lazy call per process. With 570 processes that is too much work for
a refresh, so:
- the list shows only what `ProcessInfo` has (name, PID, CPU, memory, I/O read and write, GPU, state);
- the detail pane asks for the selected process only (`DetailsCache`, a small bounded cache; a vanished process shows
  "gone");
- the Overview "heaviest apps" box asks for the product name of the top 10 candidates only (cached by `ProcessKey`,
  at most 256 entries), and merges the processes of the same product ("chrome.exe" x 15 become "Google Chrome").
  A product name that is generic ("Microsoft(R) Windows(R) Operating System") is not used as a name; kernel parts
  (PID 0, "System", "Memory Compression", "Registry") are not counted as apps.

If a list column for the user or the product is wanted, the cheap way is a batch read that returns what is already
cached and starts the rest in the background:

```diff
--- a/src/PcMieru.Core/Contracts/Interfaces.cs
+++ b/src/PcMieru.Core/Contracts/Interfaces.cs
@@
 public interface IProcessDetailsProvider
 {
     Task<ProcessDetails> GetAsync(ProcessKey key, CancellationToken ct = default);
+
+    /// <summary>Only what is cached right now (null for the rest), and starts the rest in the background. Never blocks.</summary>
+    IReadOnlyList<ProcessDetails?> PeekMany(IReadOnlyList<ProcessKey> keys) => new ProcessDetails?[keys.Count];
 }
```

## 6. Request: hardware cache invalidation has no caller

`IHardwareProvider.Invalidate()` is documented as "forget cached facts (WM_DEVICECHANGE)". The GUI page "PC parts" has a
"reload" button that calls it (and then `GetAsync(All)`), and nothing else does: a plugged USB device appears only after
that button.
**Local workaround:** the main form can override `WndProc` for `WM_DEVICECHANGE` and call `Invalidate()` (about 10 lines,
no contract change). B did not add it, because it makes the hardware page re-read (and, with C's SensorHost, start a
helper process) at every plug event on a machine B cannot observe; if the integrator wants it, it is one `case` in
`MainForm.WndProc`. No contract diff.

## 7. Notes for the integrator (no contract change)

- **One sampler, two consumers.** `Reports.BuildAsync` calls `ISnapshotSource.SampleAsync` itself for the observation
  window (`ObservationRun`), and the GUI pump samples the same instance on its timer. `ISnapshotSource` says "safe to
  call from one thread at a time", and the baseline is shared. With the real A source, a report running while the pump
  ticks makes both see shorter deltas (the second call's baseline is the first call's sample). The GUI's pump keeps running
  while a report is being made (the report page only shows its own progress timer). If A's `SamplerLoop` replaces the pump (item 1), the report should take its samples from the loop's
  `History` instead of sampling again. This is the largest unverified point of B's work.
- **Hardware JSON.** The real hardware model (integrate branch) wraps the facts as `details` records whose entries have
  no `status` key. `PartsModel` opens such an object recursively and shows each leaf as an expert row (folded by
  default, marked "expert") instead of one raw JSON cell. A model that adds `status` to those records would show them as
  normal rows with no GUI change.
- **Window title and state text.** Under sample data (`--fakes`) the title says "sample data, not measured values" and
  the Hardware "source" column shows "fake". Both come from `FakeAreas`; no real-data screen shows them.
- **Strings.** Only `gui.*` keys, ja and en, in `Gui/**/*Strings.cs`. 39 PNGs of the real window (light and dark, all
  nine pages, ja and en, plus the report page's formatted and Markdown views) are in `docs/screenshots/`; they show
  **sample data**, not this PC's processes.
- **No new NuGet package.** `DllImport` is used in four places only: `Data/WindowProbe.cs` (window list),
  `Controls/GridView.cs` (the header's own window and the scroll bars), `Theme/ThemeService.cs` (dark title bar and
  dark scroll bars) and `Smoke/SmokeRun.cs` (capture, DPI event).
