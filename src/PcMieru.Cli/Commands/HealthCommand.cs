using System.Globalization;
using System.Text;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Cli.Commands;

public sealed class HealthCommand : ICliCommand
{
    public string Name => "health";

    public string Usage => "pcmi health [--json] [--elevate]";

    public IReadOnlyList<OptionSpec> Options { get; } = [new("json"), new("elevate")];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.health", "ストレージの健康状態（--elevate で管理者の Helper を使う）", "Storage health (--elevate uses the administrator Helper)"),
        new("cli.health.drive", "ドライブ {number}: {model}（{kind}）", "Drive {number}: {model} ({kind})"),
        new("cli.health.driveUnknownKind", "ドライブ {number}: {model}", "Drive {number}: {model}"),
        new("cli.health.none", "ドライブが見つかりませんでした", "No drives were found"),
        new("cli.health.temperature", "温度", "Temperature"),
        new("cli.health.used", "寿命の使用", "Life used"),
        new("cli.health.spare", "予備領域", "Spare area"),
        new("cli.health.warning", "重大な警告", "Critical warning"),
        new("cli.health.written", "書き込み量", "Data written"),
        new("cli.health.read", "読み出し量", "Data read"),
        new("cli.health.powerOn", "通電時間", "Power-on time"),
        new("cli.health.cycles", "電源を入れた回数", "Power cycles"),
        new("cli.health.unsafe", "電源を急に切られた回数", "Unsafe shutdowns"),
        new("cli.health.media", "メディアのエラー", "Media errors"),
        new("cli.health.spareText", "{value}（下限の目安 {threshold}）", "{value} (threshold {threshold})"),
        new("cli.health.warning.none", "なし", "none"),
        new("cli.health.warning.some", "あり（値 {value}）", "yes (value {value})"),
        new("cli.health.powerOnText", "{hours} 時間（約 {days} 日）", "{hours} h (about {days} days)"),
        new("cli.health.attributes", "SMART 項目（ドライブが報告した値のまま）", "SMART attributes (as the drive reports them)"),
        new("cli.health.col.id", "ID", "ID"),
        new("cli.health.col.name", "項目", "Attribute"),
        new("cli.health.col.current", "現在値", "Current"),
        new("cli.health.col.worst", "最悪値", "Worst"),
        new("cli.health.col.threshold", "しきい値", "Threshold"),
        new("cli.health.col.raw", "生の値", "Raw"),
        new("cli.health.kind.nvme", "NVMe", "NVMe"),
        new("cli.health.kind.sata", "SATA", "SATA"),
    ];

    public async Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(0, 0);
        var elevate = args.Has("elevate");
        var drives = await context.Services().StorageHealth.ReadAsync(elevate, context.Ct).ConfigureAwait(false);
        var report = new StorageHealthReport(StorageHealthReport.CurrentSchemaVersion, drives);
        context.Out.Write(args.Has("json") ? PcMieruJson.Serialize(report) + "\n" : Render(context, report, elevate));
        return ExitCode.Success;
    }

    /// <summary>One block per drive. A drive that could not be read says why, and what to try when more rights would help.</summary>
    internal static string Render(CliContext context, StorageHealthReport report, bool elevated)
    {
        if (report.Drives.Count == 0)
        {
            return context.Text("cli.health.none") + "\n";
        }

        var sb = new StringBuilder();
        for (var i = 0; i < report.Drives.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            Drive(context, sb, report.Drives[i], elevated);
        }

        return sb.ToString();
    }

    private static void Drive(CliContext context, StringBuilder sb, DriveHealth drive, bool elevated)
    {
        var number = drive.PhysicalDriveNumber.ToString(CultureInfo.InvariantCulture);
        if (!drive.Health.TryGet(out var health))
        {
            sb.Append(context.Format("cli.health.driveUnknownKind", ("number", number), ("model", drive.Model))).Append('\n');
            sb.Append("  ").Append(Strings.Status(drive.Health.Status, context.Language)).Append('\n');
            if (drive.Health.Status == ValueStatus.Denied && !elevated)
            {
                sb.Append("  ").Append(context.Text("cli.hint.elevate")).Append('\n');
            }

            return;
        }

        var kind = context.Text("cli.health.kind." + health.Kind.ToString().ToLowerInvariant());
        sb.Append(context.Format("cli.health.drive", ("number", number), ("model", drive.Model), ("kind", kind))).Append('\n');

        var lines = new List<(string, string)>
        {
            (context.Text("cli.health.temperature"), CliFormat.OrStatus(context, health.TemperatureC, t => CliText.Num(t, "0") + " °C")),
            (context.Text("cli.health.used"), CliFormat.OrStatus(context, health.PercentageUsed, CliFormat.Percent)),
            (context.Text("cli.health.spare"), SpareText(context, health)),
            (context.Text("cli.health.warning"), CliFormat.OrStatus(context, health.CriticalWarning, w => w == 0
                ? context.Text("cli.health.warning.none")
                : context.Format("cli.health.warning.some", ("value", w.ToString(CultureInfo.InvariantCulture))))),
            (context.Text("cli.health.written"), CliFormat.OrStatus(context, health.DataWrittenBytes, v => CliFormat.Bytes((double)v.Value))),
            (context.Text("cli.health.read"), CliFormat.OrStatus(context, health.DataReadBytes, v => CliFormat.Bytes((double)v.Value))),
            (context.Text("cli.health.powerOn"), CliFormat.OrStatus(context, health.PowerOnHours, v => PowerOnText(context, v))),
            (context.Text("cli.health.cycles"), CliFormat.OrStatus(context, health.PowerCycles, v => v.ToString())),
            (context.Text("cli.health.unsafe"), CliFormat.OrStatus(context, health.UnsafeShutdowns, v => v.ToString())),
            (context.Text("cli.health.media"), CliFormat.OrStatus(context, health.MediaErrors, v => v.ToString())),
        };

        // A SATA drive has no wear counter or data-written total: those rows would only say "Unsupported" four times.
        // NVMe keeps every row, so a counter that should be there but is not stays visible.
        if (health.Kind == StorageHealthKind.Sata)
        {
            var unsupported = Strings.Status(ValueStatus.Unsupported, context.Language);
            lines.RemoveAll(l => l.Item2 == unsupported);
        }

        sb.Append(CliFormat.LabelColumn(lines, "  "));
        if (health.Attributes.Count > 0)
        {
            sb.Append("  ").Append(context.Text("cli.health.attributes")).Append('\n');
            sb.Append(AttributeTable(context, health.Attributes));
        }
    }

    private static string SpareText(CliContext context, StorageHealth health)
    {
        if (!health.AvailableSparePercent.TryGet(out var spare))
        {
            return Strings.Status(health.AvailableSparePercent.Status, context.Language);
        }

        return health.AvailableSpareThresholdPercent.TryGet(out var threshold)
            ? context.Format("cli.health.spareText", ("value", CliFormat.Percent(spare)), ("threshold", CliFormat.Percent(threshold)))
            : CliFormat.Percent(spare);
    }

    private static string PowerOnText(CliContext context, U128 hours)
    {
        var value = (double)hours.Value;
        return context.Format(
            "cli.health.powerOnText",
            ("hours", hours.ToString()),
            ("days", CliText.Num(Math.Round(value / 24), "0")));
    }

    /// <summary>
    /// The raw column. Temperature attributes (190, 194) are shown as the temperature: many drives pack the current value in
    /// the low byte and the minimum and maximum in the bytes above, so the 48-bit number would read as 184685035549 for a
    /// drive at 29 degrees. This is the same byte <c>StorageHealth.TemperatureC</c> uses, and the JSON keeps the full raw value.
    /// </summary>
    internal static string RawText(SmartAttribute attribute) =>
        attribute.Id is 190 or 194
            ? CliText.Num((double)(ulong)(attribute.Raw.Value & 0xFF), "0") + " °C"
            : attribute.Raw.ToString();

    private static string AttributeTable(CliContext context, IReadOnlyList<SmartAttribute> attributes)
    {
        string[] headers =
        [
            context.Text("cli.health.col.id"),
            context.Text("cli.health.col.name"),
            context.Text("cli.health.col.current"),
            context.Text("cli.health.col.worst"),
            context.Text("cli.health.col.threshold"),
            context.Text("cli.health.col.raw"),
        ];
        var rows = attributes.Select(a => (IReadOnlyList<string>)new[]
        {
            a.Id.ToString(CultureInfo.InvariantCulture),
            a.Name,
            a.Current.ToString(CultureInfo.InvariantCulture),
            a.Worst.ToString(CultureInfo.InvariantCulture),
            a.Threshold.ToString(CultureInfo.InvariantCulture),
            RawText(a),
        }).ToList();
        var table = CliTable.Render(headers, rows, new HashSet<int> { 0, 2, 3, 4, 5 }, flexColumn: 1, consoleWidth: Math.Max(0, context.ConsoleWidth - 2));
        return string.Concat(table.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => "  " + l + "\n"));
    }
}
