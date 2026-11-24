using System.Globalization;
using PcMieru.Update.Bundle;
using PcMieru.Update.Feed;
using PcMieru.Update.Staging;
using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Launch;

/// <summary>
/// What the runner has of the program that handed an update over (<see cref="SwapApply"/>): the pipe to talk to it, and a way to wait for THAT process to end (a handle to it, copied down by the program itself, not a
/// process id that could be reused by another program by the time it is looked at). Tests make one from a fake channel.
/// </summary>
public sealed class ApplyEndpoint : IDisposable
{
    private readonly Func<TimeSpan, bool> _waitForGuiEnd;
    private readonly Action _release;
    private bool _disposed;

    public ApplyEndpoint(IMessageChannel channel, Func<TimeSpan, bool> waitForGuiEnd, Action? release = null)
    {
        Channel = channel;
        _waitForGuiEnd = waitForGuiEnd;
        _release = release ?? (static () => { });
    }

    public IMessageChannel Channel { get; }

    /// <summary>True when the program has ended (or the handle cannot be waited on); false when the time ran out.</summary>
    public bool WaitForGuiEnd(TimeSpan timeout) => _waitForGuiEnd(timeout);

    /// <summary>Closes the pipe and the process handle. Called before anything is started from the runner: nothing that was handed down may reach a program it starts.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Channel.Dispose();
        _release();
    }

    /// <summary>The endpoint of the two handles the runner was started with. Null when the first is not a pipe or the second is not a process. Both are made non-inheritable at once.</summary>
    public static ApplyEndpoint? FromInherited(nint pipe, nint process)
    {
        if (!LaunchNative.IsPipe(pipe) || LaunchNative.ProcessIdOf(process) == 0)
        {
            return null;
        }

        LaunchNative.DisableInheritance(pipe);
        LaunchNative.DisableInheritance(process);
        var channel = PipeChannel.FromInheritedHandle(pipe);
        return channel is null ? null : new ApplyEndpoint(channel, timeout => LaunchNative.WaitForProcessHandle(process, timeout), () => LaunchNative.CloseHandle(process));
    }
}

/// <summary>
/// <c>recover.exe --apply &lt;pipe handle&gt; &lt;process handle&gt;</c> (DESIGN-B 3.3 and 5): the unattended swap a running PC Mieru asks for. There is no way to start it from files that were left behind: it needs the
/// two handles the program passed down and ONE valid message on the pipe, and with anything less it changes nothing (<see cref="SwapExit.BadCommandLine"/>).
///
/// In order:
/// <list type="number">
/// <item>An elevated token: answer <c>refused Elevated</c>, exit 32, nothing else.</item>
/// <item>Read the message (10 s). Not understood: <c>refused BadMessage</c>, exit 2.</item>
/// <item>Look at the disk (<see cref="Check"/>), trusting nothing the message says but where to look: the stage is a stage of THIS install (under <c>L\update\&lt;id&gt;\cache</c>, no link at or below
///   <c>L\update</c>), it is the folder whose manifest has the hash that was sent (and its inner manifest is the file that manifest lists), the version is not blocked and is newer than the installed one, and gui.ini says
///   <c>update.mode=auto</c>. A refusal is answered (<c>refused &lt;why&gt;</c>, exit 12) and the program stays open; nothing was changed.</item>
/// <item>Answer <c>accepted</c>. Only now does the program save its settings, stop its helpers and close.</item>
/// <item>Wait for THAT process (its handle) to end, 30 s. Still there: exit 30, nothing changed, nothing started.</item>
/// <item>Let go of the pipe and the process handle, then take the start gate EXCLUSIVELY (<see cref="SwapHost.SwapGateWait"/>). Busy: no journal, the old program is started again, exit 30.</item>
/// <item>Look at the disk again, now that nobody can start (the first look was while the program was still open). A change: an abort that is written to last-result.json, the old program is started again, exit 12.</item>
/// <item>The swap itself (<see cref="SwapTransaction.Run"/>) with the production launch check (<see cref="ProductionLaunchVerifier"/>): it keeps the gate exclusive and starts the new version in verify mode.</item>
/// <item>Let go of the gate, then start <c>I\PCMieru.exe</c> (the new version after a commit, the old one after a rollback or an abort). After a failed recovery nothing is started: the next normal start shows the gate window.</item>
/// </list>
/// The swap's preparation checks the staged payload against the inner manifest, which <see cref="StageCache.ReadBoundManifest"/> ties to the manifest hash sent on the pipe.
/// </summary>
public static class SwapApply
{
    public const string Option = "--apply";

    public static int Run(SwapHost host, string install, nint pipeHandle, nint processHandle)
    {
        var endpoint = host.OpenApplyEndpoint(pipeHandle, processHandle);
        if (endpoint is null)
        {
            host.Error.WriteLine("swap: --apply needs a pipe and a process handle that the starting program passed down");
            host.Error.WriteLine(SwapCli.Usage);
            return SwapExit.BadCommandLine;
        }

        try
        {
            return Serve(host, install, endpoint);
        }
        finally
        {
            endpoint.Dispose();
        }
    }

    private static int Serve(SwapHost host, string install, ApplyEndpoint endpoint)
    {
        if (SwapCli.RefuseIfElevated(host) is { } elevated)
        {
            endpoint.Channel.Send(ApplyAnswer.Refuse(ApplyRefusal.Elevated));
            return elevated;
        }

        if (!endpoint.Channel.TryReceive(host.ApplyMessageWait, out var line) || !ApplyMessage.TryParse(line, out var message))
        {
            endpoint.Channel.Send(ApplyAnswer.Refuse(ApplyRefusal.BadMessage));
            host.Out.WriteLine("result Refused BadMessage exit=" + SwapExit.BadCommandLine.ToString(CultureInfo.InvariantCulture));
            host.Error.WriteLine("swap: no valid message on the pipe, so nothing was changed");
            return SwapExit.BadCommandLine;
        }

        var local = host.LocalFolder ?? DefaultLocalFolder();
        var exe = Path.Combine(install, "PCMieru.exe");
        var state = InstallId.StateFolder(local, exe); // asked now: once the gate is held exclusively, even this process cannot open the gate file to read its id
        var staged = new StagedUpdate { Version = message.Version, BuildId = message.BuildId, Tag = message.Tag, Folder = message.Stage, ManifestSha256 = message.ManifestSha256 };

        if (Check(install, local, state, staged, out _) is { } first)
        {
            endpoint.Channel.Send(ApplyAnswer.Refuse(first.Refusal));
            host.Out.WriteLine($"result Refused {first.Refusal} exit={SwapExit.Aborted.ToString(CultureInfo.InvariantCulture)}");
            host.Error.WriteLine("swap: " + first.Detail);
            SwapCli.Say(host, ApplyAnswer.Ja(first.Refusal), ApplyAnswer.En(first.Refusal));
            return SwapExit.Aborted;
        }

        if (!endpoint.Channel.Send(ApplyAnswer.Accepted))
        {
            host.Out.WriteLine("result Refused NoAnswer exit=" + SwapExit.Aborted.ToString(CultureInfo.InvariantCulture));
            host.Error.WriteLine("swap: the program that asked is gone, so nothing was changed");
            return SwapExit.Aborted;
        }

        // The program closes after "accepted". Its pipe stays open until it has gone (what was written is read before it ends), then the handles are let go of.
        if (!endpoint.WaitForGuiEnd(host.AfterPidWait))
        {
            host.Out.WriteLine("result AfterPid Busy exit=" + SwapExit.Busy.ToString(CultureInfo.InvariantCulture));
            SwapCli.Say(host, "PCミエルがまだ終了していません。PCミエルをすべて終了してから、もう一度実行してください。", "PC Mieru has not closed yet. Close PC Mieru completely, then run this again.");
            return SwapExit.Busy;
        }

        endpoint.Dispose(); // nothing handed down reaches a program that is started from here

        int code;
        bool startProgram;
        using (var gate = SwapCli.EnterGate(host, install, host.SwapGateWait))
        {
            (code, startProgram) = gate.IsOpen ? Locked(host, install, local, state, staged, gate) : NotEntered(host, gate);
        }

        if (startProgram)
        {
            SwapCli.StartInstalledProgram(host, install);
        }

        return code;
    }

    private static (int Code, bool Start) NotEntered(SwapHost host, GateEntry gate)
    {
        if (gate.Outcome == GateOutcome.Busy)
        {
            host.Out.WriteLine("result Gate Busy exit=" + SwapExit.Busy.ToString(CultureInfo.InvariantCulture));
            SwapCli.Say(host, "PCミエルのファイルが使用中です。PCミエルをすべて終了してから、もう一度実行してください。", "PC Mieru's files are in use. Close PC Mieru completely, then run this again.");
            return (SwapExit.Busy, true); // no journal was written: the old program is simply started again
        }

        host.Out.WriteLine($"result Gate {gate.Outcome} exit={SwapExit.GateRefused.ToString(CultureInfo.InvariantCulture)}");
        host.Error.WriteLine("swap: " + gate.Reason + " (" + gate.Detail + ")");
        SwapCli.Say(host, "PCミエルの起動の門を開けませんでした。ZIP を新しいフォルダーに展開して使ってください。", "The start gate could not be opened. Unzip the ZIP into a new folder and use that copy. (" + gate.Reason + ")");
        return (SwapExit.GateRefused, false);
    }

    private static (int Code, bool Start) Locked(SwapHost host, string install, string local, string? state, StagedUpdate staged, GateEntry gate)
    {
        TxResult result;
        if (Check(install, local, state, staged, out var boundManifest) is { } changed)
        {
            var reason = changed.Refusal is ApplyRefusal.StageNotFound or ApplyRefusal.StageChanged ? TxReason.StagedMismatch : TxReason.UpdateNotAllowed;
            result = SwapTransaction.Refuse(gate, host.Seams, reason, "after the program closed: " + changed.Detail);
        }
        else
        {
            var request = new SwapRequest(staged.FilesFolder()) { BoundManifest = boundManifest };
            result = SwapTransaction.Run(gate, request, host.Seams.With(launcher: host.LauncherFor(gate)));
        }

        var exit = SwapExit.For(result.Outcome);
        host.Out.WriteLine($"result {result.Outcome} {result.Reason} exit={exit.ToString(CultureInfo.InvariantCulture)}");
        SwapCli.Say(host, SwapSentences.Ja(result), SwapSentences.En(result));
        return (exit, SwapExit.InstallUsable(exit));
    }

    // ------------------------------------------------------------------------------------------------------------ what is looked at on disk

    internal sealed record Finding(ApplyRefusal Refusal, string Detail);

    /// <summary>Null when the update may go on; otherwise why not. Reads only; trusts nothing of <paramref name="staged"/> but where to look.</summary>
    private static Finding? Check(string install, string local, string? state, StagedUpdate staged, out byte[]? boundManifest)
    {
        boundManifest = null;
        if (state is null)
        {
            return new Finding(ApplyRefusal.StageNotFound, "this install has no state folder (the gate file has no id)");
        }

        if (!staged.IsWellFormedFor(state))
        {
            return new Finding(ApplyRefusal.StageNotFound, "the folder is not a stage of this install");
        }

        boundManifest = StageCache.ReadBoundManifest(state, staged);
        if (boundManifest is null)
        {
            return new Finding(ApplyRefusal.StageChanged, "the stage on disk is not the one the manifest hash names (or a link is in the way)");
        }

        var paths = new TxPaths(install);
        if (BlockedTags.Read(paths.Blocked).Any(tag => tag.Version == staged.Version))
        {
            return new Finding(ApplyRefusal.Blocked, "this version is in blocked.json");
        }

        if (InstalledVersion(paths) is not { } installed)
        {
            return new Finding(ApplyRefusal.NotNewer, "the installed version cannot be read");
        }

        if (!AppVersion.TryParse(staged.Version, out var offered) || offered <= installed)
        {
            return new Finding(ApplyRefusal.NotNewer, $"{staged.Version} is not newer than {installed}");
        }

        if (UpdateModes.ReadFromFile(Path.Combine(local, "gui.ini")) != UpdateMode.Automatic)
        {
            return new Finding(ApplyRefusal.NotAutomatic, "gui.ini does not say update.mode=auto");
        }

        return null;
    }

    /// <summary>The installed version: installed.json, or (a folder that was unzipped by hand and has no record yet) its own inner manifest. Null when it cannot be read.</summary>
    internal static AppVersion? InstalledVersion(TxPaths paths)
    {
        string? text = null;
        if (ManagedSet.Exists(paths.Installed))
        {
            var bytes = TxEngine.ReadSmall(paths.Installed, TxJson.MaxBytes);
            if (bytes is not null && InstalledRecord.TryParse(bytes, out var record, out _))
            {
                text = record.Version;
            }
        }
        else
        {
            var bytes = TxEngine.ReadSmall(paths.InInstall(BundleFormat.InnerManifestName), BundleManifest.MaxBytes);
            if (bytes is not null && BundleManifest.TryParse(bytes, out var inner, out _))
            {
                text = inner.Version;
            }
        }

        return text is not null && AppVersion.TryParse(text, out var version) ? version : null;
    }

    /// <summary><c>%LOCALAPPDATA%\PCMieru</c>: L, where gui.ini and the state folders are.</summary>
    internal static string DefaultLocalFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMieru");
}
