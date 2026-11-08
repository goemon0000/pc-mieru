# Contract requests and notes from area C (Hardware + SensorHost)

Nothing in Core/Contracts, Core/Localization, Core/Services, PcMieruJsonContext.cs, Directory.Build.props,
Directory.Packages.props, the slnx, CI or tools/ was edited. No new NuGet package (WMI is used late-bound through COM,
everything else is P/Invoke). Strings are only `hw.*`. The items below are small; none blocks the merge, 1 and 2 are
needed for a correct merged result.

> **Integration status (branch `integrate`).** The paragraph above is C's original state. Now:
>
> | Item | Result | Commit |
> |---|---|---|
> | 1. `ForceFakes` in tests | Kept everywhere (every test that builds `PcMieruServices` uses it). | - |
> | 2. SensorHost next to the apps | Applied, but not as the one-line `ProjectReference` alone: `ReferenceOutputAssembly="false"` copies the apphost, runtimeconfig and deps.json but not `PCMieru.SensorHost.dll`, so the exe in `bin/` could not start and every GPU live value read "no response". A shared targets file copies the dll and pdb as well. Checked on this PC with `pcmi hw gpu`: VBIOS, power limit and temperature are read. | 5b2af93 |
> | 3. NOTICE.txt | Done, with the lines from this file and the sources the other areas named; a RepoGuard test fails when a file under `src/` names parts-life / pl-sensors and NOTICE.txt does not list it. | fff0d8a |
> | 4. Elevated SATA health | One UAC prompt for all drives (new Helper operation `disk-health-all`); `HelperDataKeys.*` replace the repeated strings. See the note in section 4. | f2b2df1 |
> | 5. Privacy (`volumes[].label`, audio name, USB name) | Done by path, not by the global keys. See the note in section 5. | 0a79a17 |
> | 8. AMD iGPU power | Shown only in the GPU's own fact list with a ja/en note. See the note in section 8. | 82ec41b |
>
> Fixes that came back from E's checks on the merged tree: duplicate DIMM names (6a1ddf3), TOSHIBA attribute 194 shown as the temperature byte in `pcmi health` (d29fa79), Wi-Fi rows left out of wired adapters in the text and window views (de27a10; the JSON keeps them as `unsupported` with `NetworkAdapter.NotWirelessReason`). VBIOS and power limit were checked on the real PC after 5b2af93.

## 1. REQUIRED: tests that create PcMieruServices must use ForceFakes

`HardwareModule.Register` now builds the REAL implementations unless `PcMieruOptions.ForceFakes` is set. A test that creates
`PcMieruServices` with default options would then read this PC's hardware (and launch PCMieru.SensorHost.exe on the first GPU
read). Done by me in a shared test file, please keep it (the same edit is needed from A, D and E; D made it too):

```diff
--- a/tests/PcMieru.Core.Tests/ServicesTests.cs
+        PcMieruServices.Create(new PcMieruOptions { ForceFakes = true, ... })
```

`FakeAreas` stays accurate: under `ForceFakes` the three hardware fakes (provider, storage health, sensor client) are
registered and listed; without it none is.

## 2. REQUIRED for the GUI and CLI: copy PCMieru.SensorHost.exe next to the apps

`SensorHostClient` looks for `PCMieru.SensorHost.exe` in `AppContext.BaseDirectory`. `tools/publish.ps1` already publishes
and merges SensorHost (checked: a Release self-contained ReadyToRun publish of the project alone works, the 160 KB exe runs,
`--once` prints one frame in 0.7 s and loads `nvml.dll` and `amdadlx64.dll` from System32). A plain `dotnet build` / F5 of
Gui or Cli does NOT copy it, because neither csproj references the project. Request (csproj of Gui and Cli, not mine):

```xml
<ProjectReference Include="..\PcMieru.SensorHost\PcMieru.SensorHost.csproj" ReferenceOutputAssembly="false" />
```

Without the exe nothing breaks: every GPU live value answers `Unsupported` with the detail
"PCMieru.SensorHost.exe was not found next to the app" (permanent for that run, no retry loop) and VBIOS / power limit /
live temperature stay "not available" instead of being guessed.

## 3. NOTICE.txt: code taken from goemon0000/parts-life (MIT, same owner)

I did not edit NOTICE.txt (other areas may add lines of their own, for example PDH; one editor avoids a merge conflict). Suggested text for the
"Code taken from other projects" part, each source file also names its origin in its header:

```
goemon0000/parts-life (MIT, same owner as PC Mieru)
  src/PcMieru.SensorHost/Nvml.cs               from src/PartsLife.Checks/Gpu/Nvml.cs
  src/PcMieru.SensorHost/Adlx.cs               from src/PartsLife.Checks/Recording/Sources.Adlx.cs
  src/PcMieru.Core/Hardware/Storage/*          from src/PartsLife.Checks/Storage/{StorageProtocol,NvmeHealth,AtaSmart}.cs
  src/PcMieru.Core/Hardware/Collectors/StorageHealthReader.cs   SATA SMART request layout
```

## 4. Elevated SATA health: adapter written, one UAC prompt per drive

> **Integrator note (f2b2df1): now one prompt for all drives.** The text below is the state before the merge ("two UAC
> prompts on this PC", "D can add a multi-drive request later"). The Helper has a new operation `disk-health-all`
> (`--drives 0,2,3`: a validated list of small integers, 0..255, ascending, distinct, at most 16, no paths). The Helper
> identifies each drive's bus itself, reads it the way that bus is read, and returns every drive's keys under
> `drive.<n>.` with the drive's own code; the exit code is `Ok`, `PartialSuccess` or the common failure.
> `RealStorageHealthProvider` collects the drives that wait for the Helper and asks once, so
> `ReadAsync(allowElevation: true)` shows **one** UAC prompt however many SATA drives there are. `smart-read` and
> `nvme-health` stay in the table. Tested on bytes with a stand-in client only (the Helper is never started in the tests);
> the batch code was also run read-only on this PC. The elevated path itself was never run for real (no UAC prompt can be answered here).

D's Helper returns the raw sectors as base64 in `HelperResult.Data` (keys `smart.data`, `smart.thresholds`, `nvme.health`).
`Hardware/Collectors/HelperStorageReader.cs` (mine) turns that into the bytes my parsers read: it asks through the frozen
`IHelperClient` with `new HelperRequest(op, "", "") { DriveNumber = n }`, accepts only `Ok` with exactly 512-byte sectors,
drops a damaged threshold sector, and returns null for everything else (the health value is then `Denied` with the reason,
never a zero). `HardwareModule` looks the Helper up lazily (`registry.TryGet<IHelperClient>`) because Management registers
after Hardware. Key names are repeated as text, so after the merge the integrator may swap them for `HelperDataKeys.*`
(there is no behaviour change; the unit tests pin the strings).

Notes for the integrator and front ends:
- `IStorageHealthProvider.ReadAsync(allowElevation: true)` runs the Helper once per drive that needs it: two UAC prompts
  on this PC (the SATA SSD and the HDD). NVMe is read as a standard user and never prompts. A GUI "read with administrator
  rights" button should say how many prompts to expect, or D can add a multi-drive request later (Wire change, not asked here).
- The `predict-failure` fallback of the Helper (`smart.source` = `predict-failure`) returns one vendor sector without
  thresholds; it parses as a table with no thresholds, and the PredictFailure flag is not carried (the byte interface has
  no place for it). Not used on this PC.
- Never run for real in my session: a UAC prompt cannot be answered here. The chain is tested on bytes only
  (fake client -> adapter -> `StorageHealthParsers.FromSmartBlob`). The direct SATA read (this process already elevated) was run for real
  on this PC, as administrator and as a standard user; the standard-user run reports `Denied` for SATA, as designed.

## 5. Privacy: what the redactor needs to know

> **Integrator note (0a79a17).** `SimpleRedactor` now also blanks `volumes[].label`, `audio[].name` and `usb[].name` **by
> their place in the tree** (an array is `[]`; the `value` of a `Value<T>` is not a step), not by the global keys `label` /
> `name`, which would blank unrelated fields. An empty label stays empty, and a name that could not be read keeps its status.
> A reflection test fails when a model field called `Label` appears that no path covers. Not covered: the network adapter's
> own name, `DisplayDetails.MonitorName` and `AudioDetails.Adapter`.

`SimpleRedactor` blanks by JSON key name. Hardware fields that can identify a person or a place, and the key each uses:

| Data | JSON key | Blanked by the existing key list |
|---|---|---|
| disk / board / memory / USB serial numbers | `serialNumber` | yes |
| MAC address | `macAddress` | yes |
| IP and DNS addresses | `ipAddresses`, `dnsServers` | yes |
| Wi-Fi network name | `ssid` | yes |
| volume label (`StorageVolume.Label`) | `label` | NO (see below) |

A reflection test (`HardwareRedactionTests`) fails when someone adds a hardware field whose name looks like serial / mac / ssid /
uuid / guid / ip / dns / hostname / address / owner and the redactor does not blank that key, and a second one pushes sentinels through a
whole `HardwareInfo` JSON and `SimpleRedactor.RedactJson`.

Request: a volume label is free text the owner typed ("Tatsuki's backup") and `label` is too generic a key to add to the
global list. Either blank `label` only under `volumes` (a sensitivity flag on the value would be cleaner: for example
`Value<T>` gets no change, but `SimpleRedactor` takes a set of "path suffixes" such as `volumes[].label`), or accept that only the
text-level user / host name replacement applies to it. I did not change the model.

Other places where personal text may show up and are NOT keyed: the audio endpoint friendly name ("Headphones (Tatsuki's AirPods)")
and the Bluetooth/USB product string. They are covered only by the text-level user-name replacement, which is the same as for
process names.

## 6. Contract notes (no change requested unless the integrator wants one)

- `UsbDevice` has no identifier of its own that a child can name as its parent, so the USB tree is rebuilt from the
  location path only. A stable `Id` on the model would let front ends link a tree; the field is mine to add if A/B ask for it.
- USB port power in watts cannot be read from user mode (the descriptor gives the maximum requested current, which is shown
  as such, not as a measurement). The row says so (`hw.limit.usbPower`).
- `UtcDateTimeJsonConverter` (shared) throws `FormatException`, not `JsonException`, on a malformed date. The SensorHost
  frame parser now catches every non-fatal exception so one bad line cannot kill the read loop. Other readers of untrusted JSON
  (for example the MCP server) should do the same.
- `SensorFrame` and `GpuSensorReading` stay as they were; I only added merge logic on top. Nothing was added to the Wire.

## 7. Behaviour front ends should know

- GPU live values (temperature, power, clocks, fan, memory clock, VBIOS, power limit) come from the SensorHost process; the first
  `GetAsync(Gpu)` after start waits at most about 4 s for its first line (measured here: 1.3 s), later calls read the newest
  frame (0 ms). The host stops itself after 60 s without a GPU read and is started again by the next read (not a failure).
- A frame older than 6 s is never returned. When the host hangs inside a vendor driver the client kills and restarts it
  (measured: a suspended host was replaced about 8 s after it froze, with the right "restarting" text in between), 1 s backoff
  doubling to 30 s after repeated failures.
- If the app exits or crashes without disposing the client, the host notices the closed input pipe and exits by itself (verified:
  no PCMieru.SensorHost.exe left five seconds after a client was abandoned).
- Values carry `Source` and `Status`. Nothing is shown as 0 when it could not be read: unreadable is `Unsupported`, `Denied`,
  `Timeout`, `Error` or `Gone` with a one-line reason in English (`Detail`); front ends should show the localized
  `hw.*` / status text and keep the detail for the report.
- `IHardwareProvider.Invalidate()` clears the cache of the static categories; sensor data is never cached.

## 8. Not verifiable on a development PC

- Wi-Fi connected path and the "location permission denied" path (nothing is connected over Wi-Fi on this PC): unit-tested on synthetic WLAN data only.
- Laptop battery (this PC reports `Unsupported`, "no battery"): the IOCTL path is tested on synthetic structures only.
- The elevated Helper read (needs a UAC prompt, see 4).
- AMD integrated GPU power: ADLX answers a number (about 1 to 60 W within seconds on an idle desktop, moving with the system
  load more than with graphics load). The value is shown with the note "the AMD driver's GPU power value; the board total is not
  reported". Its true meaning for an APU is not confirmed, so the front end should show that note next to it (or hide it).
  **Integrator (82ec41b):** done. `GpuPowerSource.DriverReport` names the `Value.Source`; the Performance page shows such a number
  only in the GPU's own fact list, captioned "Power (driver report)" with the ja/en note as its tip. The left list, the headline
  and the CLI perf summary carry no power from it. NVML readings and the ADLX board total stay plain. The Hardware page has no live power row.
- NVIDIA values were compared with `nvidia-smi` on this PC (temperature, power, limit, fan, clocks, PCIe link, VBIOS, driver):
  they match; instantaneous values differ by what one sample interval changes (27.2 W against 27.7 W).
