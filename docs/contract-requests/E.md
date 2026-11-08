# Contract requests from area E (CLI + Reporting + MCP)

Only one change to a frozen contract is wanted. Everything else E needed fitted inside its own folders.

## 1. `GpuInfo.TemperatureLimitC` (wanted, not blocking)

**Why.** SPEC section 6 has the observation "GPU temperature is close to the limit the vendor API reports". The rule
must never invent a limit, so it needs the number the vendor API gives (NVML slowdown/shutdown threshold, ADLX).
`GpuInfo` has `TemperatureC` but no limit, so there is nowhere to carry it from the sampler (area A) to the rule (area E).

**Local workaround (already in place, nothing is lost).** E defines `IGpuTemperatureLimits` in
`Core/Reporting/ObservationRules.cs` (`double? LimitC(string gpuId)`). `ReportingModule` takes it with
`registry.TryGet<IGpuTemperatureLimits>`; when nobody registered one the rule stays silent. So the product is correct
today, it just never says "GPU is near its limit" until a limit is supplied. Tests cover both the silent and the firing case.

**Proposed diff** (`src/PcMieru.Core/Contracts/Snapshot.cs`). An init property with a default, so no constructor call
site (A's sampler, `FakeCollection`, tests) has to change:

```diff
 public sealed record GpuInfo(
     string Id,
     string Name,
     Value<double> UtilizationPercent,
     IReadOnlyList<GpuEngineInfo> Engines,
     Value<long> DedicatedUsedBytes,
     Value<long> DedicatedTotalBytes,
     Value<long> SharedUsedBytes,
     Value<double> TemperatureC,
     Value<double> PowerWatts,
     Value<double> FanPercent,
-    Value<double> ClockMhz);
+    Value<double> ClockMhz)
+{
+    /// <summary>
+    /// The temperature at which the vendor API says the GPU slows down (NVML slowdown threshold, ADLX). NotCollected
+    /// when the API gives none. Never a guessed number.
+    /// </summary>
+    public Value<double> TemperatureLimitC { get; init; } = Value.NotCollected<double>("none");
+}
```

**Side effects to expect.**
- JSON gains `"temperatureLimitC": {"status":"notCollected","source":"none"}` on every GPU. Golden/schema fixtures that
  contain a `GpuInfo` (Core.Tests) need regenerating by whoever owns them.
- A fills it from NVML (`nvmlDeviceGetTemperatureThreshold`) when available. No other area changes.

**After it lands, E does:** `ObservationRules.ThermalReadings` reads `gpu.TemperatureLimitC` first and falls back to
`IGpuTemperatureLimits`; the interface can then be deleted. If the request is declined, A may instead register an
`IGpuTemperatureLimits` implementation in its module; E needs nothing more.

## 2. Notes for the integrator (no contract change)

- **Helper result file.** D's Helper (commit `d1262ba`) writes its result "only into the caller's pre-created nonce file",
  which is what the CLI does (`HelperElevation` creates the empty `<HelperDirectory>\<32-hex nonce>.json`, calls
  `IHelperClient`, deletes the file in `finally`). If D's real `IHelperClient` creates the file itself, the CLI's
  pre-creation is harmless (it is the same path) but `HelperRequest.ResultPath` must stay the single source of the path.
- **Root protection in `kill`.** `FakeProcessControl.PlanTerminateTreeAsync` puts the tree root into `Targets` without
  checking `IProtectionPolicy` for it. The CLI checks the root itself before planning, so a protected root is refused
  with exit 5 whatever the control does. D's real implementation should keep refusing protected targets on its own
  (defence in depth); the CLI does not rely on it.
- **No new NuGet package** is needed by E. Tables, width handling, argument parsing and the MCP server are plain .NET.
- **MCP stays read-only by construction.** `McpServer` takes only `IReadOnlyInsights` and `IRedactor`; `McpBoundaryTests`
  fails if its constructor, its public surface or its sources reach process control, the Helper, the launcher or the network.
  Adding a method to `IReadOnlyInsights` is therefore a contract change that also needs a decision about the MCP tools.
