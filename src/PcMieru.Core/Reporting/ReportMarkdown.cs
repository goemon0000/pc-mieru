using System.Globalization;
using System.Text;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Reporting;

/// <summary>
/// The diagnostic report as Markdown, written to be pasted into an AI chat or sent to a person: short sections, one
/// fact per line, units on every number, and a plain "not available (reason)" wherever a value could not be read. It
/// prints no serial number, MAC, IP, SSID or command line at all; the redactor still runs over the final text.
/// Process and file names are data read from this PC, so they are always inside code spans.
/// </summary>
public static class ReportMarkdown
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Render(ReportDocument doc, Language language) => Render(doc, language, null);

    /// <param name="appVersion">Shown in the environment section when given (the CLI passes its version).</param>
    public static string Render(ReportDocument doc, Language language, string? appVersion)
    {
        var w = new Writer(language);
        w.Line("# " + w.T("report.title"));
        w.Line("- " + w.F("report.header.generated", ("time", doc.GeneratedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", Inv))));
        w.Line("- " + w.F("report.header.duration", ("seconds", doc.DurationSec.ToString(Inv))));
        w.Line("- " + w.T(doc.Redacted ? "report.header.redactedYes" : "report.header.redactedNo"));
        w.Line("> " + w.T("report.header.note"));
        w.Blank();

        Summary(w, doc);
        Observations(w, doc);
        Processes(w, doc);
        if (doc.Hardware is not null)
        {
            HardwareSection(w, doc.Hardware);
        }

        if (doc.Storage is not null)
        {
            StorageSection(w, doc.Storage);
        }

        Environment(w, doc, appVersion);
        return w.ToString();
    }

    // ---- sections ----------------------------------------------------------------------------------------------

    private static void Summary(Writer w, ReportDocument doc)
    {
        var system = doc.Summary.System;
        w.Line("## " + w.T("report.section.summary"));

        var cpu = system.Cpu;
        if (cpu.TotalPercent.TryGet(out var total))
        {
            var busiest = cpu.PerLogicalPercent.Where(v => v.IsOk).Select(v => v.Data).DefaultIfEmpty(-1).Max();
            w.Line("- " + w.F(
                busiest >= 0 ? "report.summary.cpu" : "report.summary.cpuNoCore",
                ("total", Pct(total)),
                ("logical", cpu.LogicalProcessors.ToString(Inv)),
                ("busiest", Pct(busiest))));
        }
        else
        {
            w.Line("- " + w.F("report.summary.cpuMissing", ("status", w.Status(cpu.TotalPercent.Status))));
        }

        var memory = system.Memory;
        if (memory.TotalPhysicalBytes > 0)
        {
            var used = memory.TotalPhysicalBytes - memory.AvailablePhysicalBytes;
            w.Line("- " + w.F(
                "report.summary.memory",
                ("used", Gb(used)),
                ("total", Gb(memory.TotalPhysicalBytes)),
                ("percent", Pct(100.0 * used / memory.TotalPhysicalBytes))));
        }

        if (memory.CommitTotalBytes.TryGet(out var commit) && memory.CommitLimitBytes.TryGet(out var limit) && limit > 0)
        {
            w.Line("- " + w.F("report.summary.commit", ("used", Gb(commit)), ("limit", Gb(limit)), ("percent", Pct(100.0 * commit / limit))));
        }

        ListNotice(w, "report.summary.list.gpu", system.Gpus.Count, system.GpusState);
        ListNotice(w, "report.summary.list.disks", system.Disks.Count, system.DisksState);
        ListNotice(w, "report.summary.list.nics", system.Nics.Count, system.NicsState);

        foreach (var gpu in system.Gpus)
        {
            var parts = new List<string>
            {
                gpu.UtilizationPercent.TryGet(out var util)
                    ? w.F("report.summary.gpuUtil", ("percent", Pct(util)))
                    : w.F("report.summary.gpuUtilMissing", ("status", w.Status(gpu.UtilizationPercent.Status))),
            };
            if (gpu.DedicatedUsedBytes.TryGet(out var vramUsed) && gpu.DedicatedTotalBytes.TryGet(out var vramTotal))
            {
                parts.Add(w.F("report.summary.gpuMemory", ("used", Gb(vramUsed)), ("total", Gb(vramTotal))));
            }

            if (gpu.TemperatureC.TryGet(out var temperature))
            {
                parts.Add(w.F("report.summary.gpuTemp", ("temp", temperature.ToString("0", Inv))));
            }

            w.Line("- " + w.F("report.summary.gpu", ("name", Code(gpu.Name)), ("details", string.Join(w.T("report.sep"), parts))));
        }

        foreach (var disk in system.Disks)
        {
            var parts = new List<string>();
            AddIfOk(parts, disk.ActivePercent, v => w.F("report.summary.diskActive", ("percent", Pct(v))));
            AddIfOk(parts, disk.ReadBytesPerSec, v => w.F("report.summary.diskRead", ("rate", Mb(v))));
            AddIfOk(parts, disk.WriteBytesPerSec, v => w.F("report.summary.diskWrite", ("rate", Mb(v))));
            AddIfOk(parts, disk.ResponseTimeMs, v => w.F("report.summary.diskResponse", ("ms", v.ToString("0.0", Inv))));
            if (parts.Count == 0)
            {
                parts.Add(w.T("report.summary.noRate"));
            }

            w.Line("- " + w.F("report.summary.disk", ("name", Code(string.IsNullOrWhiteSpace(disk.Name) ? disk.Id : disk.Name)), ("details", string.Join(w.T("report.sep"), parts))));
        }

        foreach (var nic in system.Nics.Where(n => n.IsUp))
        {
            var parts = new List<string>();
            AddIfOk(parts, nic.SendBytesPerSec, v => w.F("report.summary.nicSend", ("rate", Kb(v))));
            AddIfOk(parts, nic.ReceiveBytesPerSec, v => w.F("report.summary.nicReceive", ("rate", Kb(v))));
            if (parts.Count == 0)
            {
                parts.Add(w.T("report.summary.noRate"));
            }

            w.Line("- " + w.F("report.summary.nic", ("name", Code(string.IsNullOrWhiteSpace(nic.Name) ? nic.Id : nic.Name)), ("details", string.Join(w.T("report.sep"), parts))));
        }

        w.Line("- " + w.F("report.summary.uptime", ("time", Uptime(system.UptimeSec, w))));
        w.Blank();
    }

    private static void Observations(Writer w, ReportDocument doc)
    {
        w.Line("## " + w.T("report.section.observations"));
        if (doc.Summary.Observations.Count == 0)
        {
            w.Line("- " + w.T("report.observations.none"));
            w.Blank();
            return;
        }

        foreach (var o in doc.Summary.Observations)
        {
            var kind = w.T(o.Certainty == ObservationCertainty.Candidate ? "report.observations.candidate" : "report.observations.fact");
            var level = w.T(o.Severity switch
            {
                ObservationSeverity.Warning => "report.observations.warning",
                ObservationSeverity.Notice => "report.observations.notice",
                _ => "report.observations.info",
            });
            w.Line($"- [{level} / {kind}] " + OneLine(Strings.Render(o, w.Language)));
            if (o.Evidence.Count > 0)
            {
                var evidence = string.Join(", ", o.Evidence.Select(e => OneLine(e.Metric) + "=" + OneLine(e.Value) + (e.Unit.Length > 0 ? " " + OneLine(e.Unit) : string.Empty)));
                w.Line("  - " + w.T("report.observations.evidence") + ": " + evidence);
            }
        }

        w.Blank();
    }

    private static void Processes(Writer w, ReportDocument doc)
    {
        w.Line("## " + w.T("report.section.processes"));
        if (doc.Summary.TopProcesses.Count == 0)
        {
            w.Line("- " + w.T("report.processes.none"));
            w.Blank();
            return;
        }

        w.Line($"| {w.T("report.processes.name")} | PID | CPU % | {w.T("report.processes.memory")} | {w.T("report.processes.io")} |");
        w.Line("|---|---:|---:|---:|---:|");
        foreach (var p in doc.Summary.TopProcesses)
        {
            var cpu = p.CpuPercent.TryGet(out var c) ? c.ToString("0.0", Inv) : w.Status(p.CpuPercent.Status);
            var mem = (p.PrivateWorkingSetBytes / 1024.0 / 1024.0).ToString("0", Inv);
            var io = p.IoReadBytesPerSec.TryGet(out var r) && p.IoWriteBytesPerSec.TryGet(out var wr) ? Mb(r + wr) : w.Status(p.IoReadBytesPerSec.Status);
            w.Line($"| {Cell(p.Name)} | {p.Key.Pid.ToString(Inv)} | {cpu} | {mem} | {io} |");
        }

        w.Blank();
    }

    private static void HardwareSection(Writer w, HardwareInfo hw)
    {
        w.Line("## " + w.T("report.section.hardware"));

        Group(w, "report.hardware.cpu", hw.Cpu, cpu =>
        {
            var parts = new List<string>();
            Text(parts, cpu.Name, v => Code(v));
            if (cpu.PhysicalCores.TryGet(out var cores) && cpu.LogicalProcessors.TryGet(out var logical))
            {
                parts.Add(w.F("report.hardware.cores", ("cores", cores.ToString(Inv)), ("threads", logical.ToString(Inv))));
            }

            if (cpu.BaseClockMhz.TryGet(out var mhz))
            {
                parts.Add(w.F("report.hardware.baseClock", ("mhz", mhz.ToString(Inv))));
            }

            return parts;
        });

        Group(w, "report.hardware.memory", hw.Memory, memory =>
        {
            var parts = new List<string>();
            if (memory.TotalBytes.TryGet(out var total))
            {
                parts.Add(Gb(total) + " GB");
            }

            if (memory.SlotsUsed.TryGet(out var used) && memory.SlotsTotal.TryGet(out var slots))
            {
                parts.Add(w.F("report.hardware.slots", ("used", used.ToString(Inv)), ("total", slots.ToString(Inv))));
            }

            var first = memory.Modules.FirstOrDefault();
            if (first is not null && first.Type.TryGet(out var type) && type != MemoryType.Unknown)
            {
                var speed = first.ConfiguredSpeedMtPerSec.TryGet(out var s) ? s : first.SpeedMtPerSec.TryGet(out var s2) ? s2 : 0;
                parts.Add(type.ToString().ToUpperInvariant() + (speed > 0 ? " " + speed.ToString(Inv) + " MT/s" : string.Empty));
            }

            return parts;
        });

        Group(w, "report.hardware.board", hw.Board, board =>
        {
            var parts = new List<string>();
            var name = string.Join(' ', new[] { board.Manufacturer, board.Product }.Where(v => v.IsOk).Select(v => v.Data));
            if (name.Length > 0)
            {
                parts.Add(Code(name));
            }

            if (board.BiosVersion.TryGet(out var bios))
            {
                parts.Add(w.F("report.hardware.bios", ("version", Code(bios))));
            }

            if (board.SecureBoot.TryGet(out var secure))
            {
                parts.Add(w.F(secure ? "report.hardware.secureBootOn" : "report.hardware.secureBootOff"));
            }

            return parts;
        });

        if (hw.Gpus.TryGet(out var gpus))
        {
            foreach (var gpu in gpus)
            {
                var parts = new List<string>();
                Text(parts, gpu.Name, v => Code(v));
                if (gpu.DriverVersion.TryGet(out var driver))
                {
                    parts.Add(w.F("report.hardware.driver", ("version", Code(driver))));
                }

                if (gpu.DedicatedBytes.TryGet(out var vram))
                {
                    parts.Add(w.F("report.hardware.vram", ("size", Gb(vram))));
                }

                w.Line($"- {w.T("report.hardware.gpu")}: {string.Join(w.T("report.sep"), parts)}");
            }
        }
        else
        {
            w.Line($"- {w.T("report.hardware.gpu")}: {w.Status(hw.Gpus.Status)}");
        }

        if (hw.Storage.TryGet(out var storage))
        {
            foreach (var d in storage)
            {
                var parts = new List<string>();
                Text(parts, d.Model, v => Code(v));
                if (d.Bus.TryGet(out var bus) && bus != StorageBus.Unknown)
                {
                    parts.Add(BusName(bus.ToString()));
                }

                if (d.CapacityBytes.TryGet(out var cap))
                {
                    parts.Add(Gb(cap) + " GB");
                }

                w.Line($"- {w.T("report.hardware.storage")}: {string.Join(w.T("report.sep"), parts)}");
            }
        }
        else
        {
            w.Line($"- {w.T("report.hardware.storage")}: {w.Status(hw.Storage.Status)}");
        }

        if (hw.Displays.TryGet(out var displays))
        {
            foreach (var d in displays)
            {
                var parts = new List<string>();
                Text(parts, d.Model, v => Code(v));
                if (d.CurrentResolution.TryGet(out var res))
                {
                    parts.Add(res + (d.RefreshHz.TryGet(out var hz) ? " @ " + hz.ToString("0", Inv) + " Hz" : string.Empty));
                }

                w.Line($"- {w.T("report.hardware.display")}: {string.Join(w.T("report.sep"), parts)}");
            }
        }

        w.Blank();
    }

    private static void StorageSection(Writer w, StorageHealthReport storage)
    {
        w.Line("## " + w.T("report.section.storage"));
        if (storage.Drives.Count == 0)
        {
            w.Line("- " + w.T("report.storage.none"));
            w.Blank();
            return;
        }

        w.Line($"| {w.T("report.storage.drive")} | {w.T("report.storage.kind")} | {w.T("report.storage.temp")} | {w.T("report.storage.used")} | {w.T("report.storage.spare")} | {w.T("report.storage.warning")} |");
        w.Line("|---|---|---:|---:|---:|---|");
        foreach (var drive in storage.Drives)
        {
            var name = Cell(drive.PhysicalDriveNumber.ToString(Inv) + ": " + drive.Model);
            if (!drive.Health.TryGet(out var h))
            {
                w.Line($"| {name} | {w.Status(drive.Health.Status)} | - | - | - | - |");
                continue;
            }

            var warning = h.CriticalWarning.TryGet(out var cw) ? (cw == 0 ? w.T("report.storage.noWarning") : "0x" + cw.ToString("X2", Inv)) : "-";
            w.Line($"| {name} | {BusName(h.Kind.ToString())} | {NumOrDash(h.TemperatureC, "0")} °C | {NumOrDash(h.PercentageUsed, "0")} % | {NumOrDash(h.AvailableSparePercent, "0")} % | {warning} |");
        }

        w.Blank();
    }

    private static void Environment(Writer w, ReportDocument doc, string? appVersion)
    {
        w.Line("## " + w.T("report.section.environment"));
        if (doc.Hardware?.Os.TryGet(out var os) == true)
        {
            var parts = new List<string>();
            Text(parts, os.Name, v => Code(v));
            if (os.Version.TryGet(out var version))
            {
                parts.Add(w.F("report.environment.version", ("version", Code(version))));
            }

            if (os.Build.TryGet(out var build))
            {
                parts.Add(w.F("report.environment.build", ("build", Code(build))));
            }

            if (os.Architecture.TryGet(out var arch))
            {
                parts.Add(Code(arch));
            }

            w.Line($"- OS: {string.Join(w.T("report.sep"), parts)}");
            if (os.PowerPlan.TryGet(out var plan))
            {
                w.Line($"- {w.T("report.environment.powerPlan")}: {Code(plan)}");
            }
        }

        if (appVersion is not null)
        {
            w.Line($"- {w.T("report.environment.app")}: {Code(appVersion)}");
        }

        w.Line("- " + w.T("report.environment.measured"));
        w.Line("- " + w.T("report.environment.limits"));
    }

    // ---- small helpers -----------------------------------------------------------------------------------------

    private static void Group<T>(Writer w, string labelKey, Value<T> value, Func<T, List<string>> parts)
    {
        if (value.TryGet(out var data))
        {
            w.Line($"- {w.T(labelKey)}: {string.Join(w.T("report.sep"), parts(data))}");
        }
        else
        {
            w.Line($"- {w.T(labelKey)}: {w.Status(value.Status)}");
        }
    }

    private static void Text(List<string> parts, Value<string> value, Func<string, string> format)
    {
        if (value.TryGet(out var text) && text.Length > 0)
        {
            parts.Add(format(text));
        }
    }

    /// <summary>A list that is empty because it could not be read gets a line saying so; a list that is empty and read fine ("none") gets nothing.</summary>
    private static void ListNotice(Writer w, string listKey, int count, Value<int> state)
    {
        if (ListStatus.ExplainsEmptyList(count, state))
        {
            w.Line("- " + w.F("report.summary.listUnread", ("list", w.T(listKey)), ("status", ListStatus.Describe(state, w.Language))));
        }
    }

    private static void AddIfOk(List<string> parts, Value<double> value, Func<double, string> format)
    {
        if (value.TryGet(out var v))
        {
            parts.Add(format(v));
        }
    }

    private static string NumOrDash(Value<double> value, string format) => value.TryGet(out var v) ? v.ToString(format, Inv) : "-";

    private static string Pct(double value) => value.ToString("0.#", Inv);

    private static string Gb(long bytes) => (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.0", Inv);

    private static string Mb(double bytesPerSec) => (bytesPerSec / 1024.0 / 1024.0).ToString("0.0", Inv);

    private static string Kb(double bytesPerSec) => (bytesPerSec / 1024.0).ToString("0", Inv);

    private static string Uptime(long seconds, Writer w)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalDays >= 1
            ? w.F("report.uptime.days", ("days", ((int)span.TotalDays).ToString(Inv)), ("hours", span.Hours.ToString(Inv)))
            : w.F("report.uptime.hours", ("hours", span.Hours.ToString(Inv)), ("minutes", span.Minutes.ToString(Inv)));
    }

    /// <summary>One line, no control characters: text from the machine must not be able to start a new Markdown line.</summary>
    /// <summary>"Nvme" reads as the product name NVMe; other bus names are acronyms (SATA, USB, SCSI).</summary>
    private static string BusName(string name) =>
        string.Equals(name, "Nvme", StringComparison.OrdinalIgnoreCase) ? "NVMe" : name.ToUpperInvariant();

    public static string OneLine(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(char.IsControl(c) ? ' ' : c);
        }

        return sb.ToString();
    }

    /// <summary>A name from the machine as an inline code span (backticks and newlines cannot break out of it).</summary>
    public static string Code(string text) => "`" + OneLine(text).Replace('`', '\'').Trim() + "`";

    /// <summary>A table cell: a code span with the column separator escaped.</summary>
    private static string Cell(string text) => Code(text).Replace("|", "\\|", StringComparison.Ordinal);

    private sealed class Writer(Language language)
    {
        private readonly StringBuilder _sb = new();

        public Language Language { get; } = language;

        public string T(string key) => Strings.Get(key, Language);

        public string F(string key, params (string Key, string Value)[] args) =>
            Strings.Format(key, args.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal), Language);

        public string Status(ValueStatus status) => Strings.Status(status, Language);

        public void Line(string text) => _sb.Append(text).Append('\n');

        public void Blank() => _sb.Append('\n');

        public override string ToString() => _sb.ToString();
    }
}
