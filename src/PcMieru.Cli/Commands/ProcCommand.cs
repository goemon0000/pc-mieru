using System.Globalization;
using PcMieru.Core;

namespace PcMieru.Cli.Commands;

public sealed class ProcCommand : ICliCommand
{
    public string Name => "proc";

    public string Usage => "pcmi proc <pid> [--json]";

    public IReadOnlyList<OptionSpec> Options { get; } = [new("json")];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.proc", "1 つのプロセスの詳細（パス・署名・コマンドライン）", "Details of one process (path, signature, command line)"),
        new("cli.proc.label.name", "名前", "Name"),
        new("cli.proc.label.pid", "PID", "PID"),
        new("cli.proc.label.started", "開始", "Started"),
        new("cli.proc.label.state", "状態", "State"),
        new("cli.proc.label.cpu", "CPU", "CPU"),
        new("cli.proc.label.memory", "メモリ", "Memory"),
        new("cli.proc.label.io", "読み書き", "Disk I/O"),
        new("cli.proc.label.gpu", "GPU", "GPU"),
        new("cli.proc.label.counts", "スレッド等", "Threads etc."),
        new("cli.proc.label.path", "ファイルの場所", "Path"),
        new("cli.proc.label.commandLine", "コマンドライン", "Command line"),
        new("cli.proc.label.owner", "実行ユーザー", "Owner"),
        new("cli.proc.label.product", "製品名", "Product"),
        new("cli.proc.label.description", "説明", "Description"),
        new("cli.proc.label.company", "会社", "Company"),
        new("cli.proc.label.architecture", "ビット数", "Architecture"),
        new("cli.proc.label.efficiency", "省電力モード", "Efficiency mode"),
        new("cli.proc.label.signature", "署名", "Signature"),
        new("cli.proc.label.services", "関連するサービス", "Services"),
        new("cli.proc.label.known", "これは何？", "What is this?"),
        new("cli.proc.pid", "{pid}（親 {parent}）", "{pid} (parent {parent})"),
        new("cli.proc.started", "{time} UTC", "{time} UTC"),
        new("cli.proc.memory", "{private} が専用（ワーキングセット {working}・コミット {commit}）", "{private} private (working set {working}, commit {commit})"),
        new("cli.proc.io", "読み取り {read}、書き込み {write}（起動後の合計: 読み取り {readTotal}、書き込み {writeTotal}）", "read {read}, write {write} (total since start: read {readTotal}, written {writeTotal})"),
        new("cli.proc.counts", "スレッド {threads}・ハンドル {handles}・基本優先度 {priority}・セッション {session}", "{threads} threads, {handles} handles, base priority {priority}, session {session}"),
        new("cli.proc.signature.unsigned", "署名なし", "Not signed"),
        new("cli.proc.signature.embedded", "署名あり（ファイルに埋め込み）", "Signed (embedded in the file)"),
        new("cli.proc.signature.catalog", "署名あり（カタログ）", "Signed (catalog)"),
        new("cli.proc.signature.invalid", "署名が正しくありません", "Signature is not valid"),
        new("cli.proc.signature.signer", "{state}: {signer}", "{state}: {signer}"),
        new("cli.proc.signature.offline", "オフラインでの確認です（失効は調べていません）", "Checked offline (revocation was not checked)"),
        new("cli.proc.state.running", "動作中", "Running"),
        new("cli.proc.state.notResponding", "応答なし", "Not responding"),
        new("cli.proc.state.suspended", "一時停止中", "Suspended"),
        new("cli.proc.on", "オン", "on"),
        new("cli.proc.off", "オフ", "off"),
        new("cli.proc.noDetails", "詳細（パス・署名など）はまだ取得していません", "Details (path, signature ...) have not been collected"),
        new("cli.proc.noServices", "なし", "none"),
    ];

    public async Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(1, 1);
        if (!uint.TryParse(args.Positionals[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
        {
            throw new ArgException("the PID must be a whole number");
        }

        var detail = await context.Services().Insights.GetProcessAsync(pid, context.Ct).ConfigureAwait(false);
        if (detail is null)
        {
            context.Err.WriteLine("pcmi proc: " + context.Text("cli.notFound.pid"));
            return ExitCode.NotFound;
        }

        context.Out.Write(args.Has("json") ? PcMieruJson.Serialize(detail) + "\n" : Render(context, detail));
        return ExitCode.Success;
    }

    internal static string Render(CliContext context, ProcessDetail detail)
    {
        var p = detail.Process;
        var inv = CultureInfo.InvariantCulture;
        var lines = new List<(string Label, string Value)>
        {
            (context.Text("cli.proc.label.name"), p.Name),
            (
                context.Text("cli.proc.label.pid"),
                p.ParentPid == 0
                    ? p.Key.Pid.ToString(inv)
                    : context.Format("cli.proc.pid", ("pid", p.Key.Pid.ToString(inv)), ("parent", p.ParentPid.ToString(inv)))),
        };

        if (p.Key.CreateTime > 0)
        {
            lines.Add((
                context.Text("cli.proc.label.started"),
                context.Format("cli.proc.started", ("time", p.Key.StartedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", inv)))));
        }

        lines.Add((context.Text("cli.proc.label.state"), CliFormat.OrStatus(context, p.State, s => StateText(context, s))));
        lines.Add((context.Text("cli.proc.label.cpu"), CliFormat.OrStatus(context, p.CpuPercent, CliFormat.Percent)));
        lines.Add((
            context.Text("cli.proc.label.memory"),
            context.Format(
                "cli.proc.memory",
                ("private", CliFormat.Bytes(p.PrivateWorkingSetBytes)),
                ("working", CliFormat.Bytes(p.WorkingSetBytes)),
                ("commit", CliFormat.Bytes(p.CommitBytes)))));
        lines.Add((
            context.Text("cli.proc.label.io"),
            context.Format(
                "cli.proc.io",
                ("read", CliFormat.OrDash(p.IoReadBytesPerSec, CliFormat.Rate)),
                ("write", CliFormat.OrDash(p.IoWriteBytesPerSec, CliFormat.Rate)),
                ("readTotal", CliFormat.Bytes(p.IoReadBytesTotal)),
                ("writeTotal", CliFormat.Bytes(p.IoWriteBytesTotal)))));
        lines.Add((context.Text("cli.proc.label.gpu"), CliFormat.OrStatus(context, p.GpuPercent, CliFormat.Percent)));
        lines.Add((
            context.Text("cli.proc.label.counts"),
            context.Format(
                "cli.proc.counts",
                ("threads", p.ThreadCount.ToString(inv)),
                ("handles", p.HandleCount.ToString(inv)),
                ("priority", p.BasePriority.ToString(inv)),
                ("session", p.SessionId.ToString(inv)))));

        if (detail.Details is null)
        {
            lines.Add((string.Empty, context.Text("cli.proc.noDetails")));
            return CliFormat.LabelColumn(lines);
        }

        var d = detail.Details;
        lines.Add((context.Text("cli.proc.label.path"), CliFormat.OrStatus(context, d.Path, s => s)));
        lines.Add((context.Text("cli.proc.label.commandLine"), CliFormat.OrStatus(context, d.CommandLine, s => s)));
        lines.Add((context.Text("cli.proc.label.owner"), CliFormat.OrStatus(context, d.Owner, s => s)));
        lines.Add((context.Text("cli.proc.label.product"), CliFormat.OrStatus(context, d.Product, s => s)));
        lines.Add((context.Text("cli.proc.label.description"), CliFormat.OrStatus(context, d.Description, s => s)));
        lines.Add((context.Text("cli.proc.label.company"), CliFormat.OrStatus(context, d.Company, s => s)));
        lines.Add((context.Text("cli.proc.label.architecture"), CliFormat.OrStatus(context, d.Architecture, s => s)));
        lines.Add((context.Text("cli.proc.label.efficiency"), CliFormat.OrStatus(context, d.EfficiencyMode, on => context.Text(on ? "cli.proc.on" : "cli.proc.off"))));
        lines.Add((context.Text("cli.proc.label.signature"), CliFormat.OrStatus(context, d.Signature, s => SignatureText(context, s))));
        lines.Add((context.Text("cli.proc.label.services"), d.ServiceNames.Count == 0 ? context.Text("cli.proc.noServices") : string.Join(", ", d.ServiceNames)));
        if (d.Known is { } known)
        {
            lines.Add((context.Text("cli.proc.label.known"), context.Language == Language.Ja ? known.DescriptionJa : known.DescriptionEn));
        }

        return CliFormat.LabelColumn(lines);
    }

    private static string StateText(CliContext context, ProcessState state) =>
        context.Text("cli.proc.state." + char.ToLowerInvariant(state.ToString()[0]) + state.ToString()[1..]);

    private static string SignatureText(CliContext context, SignatureInfo signature)
    {
        var state = context.Text("cli.proc.signature." + signature.State switch
        {
            SignatureState.Unsigned => "unsigned",
            SignatureState.SignedEmbedded => "embedded",
            SignatureState.SignedCatalog => "catalog",
            _ => "invalid",
        });
        var text = string.IsNullOrEmpty(signature.Signer)
            ? state
            : context.Format("cli.proc.signature.signer", ("state", state), ("signer", signature.Signer));
        return text + (signature.State is SignatureState.SignedEmbedded or SignatureState.SignedCatalog
            ? "  [" + context.Text("cli.proc.signature.offline") + "]"
            : string.Empty);
    }
}
