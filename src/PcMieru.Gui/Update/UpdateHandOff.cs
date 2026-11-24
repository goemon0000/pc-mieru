using System.Runtime.InteropServices;
using PcMieru.Update.Feed;
using PcMieru.Update.Launch;

namespace PcMieru.Gui;

internal enum RecordingActivity { Idle, Preparing, Recording, Stopping }

internal readonly record struct UpdateIdleFacts(
    uint? LastInput, bool Dialog, bool Menu, bool Drag, bool Check, RecordingActivity Recording, bool PendingUac, bool Elevated);

internal enum GateHolders { Clear, Pcmi, Other, Unknown, ReadOnly }

internal readonly record struct UpdateDeferral(GateHolders Holders, ApplyRefusal? Refusal = null)
{
    public string Text => Refusal is { } refusal
        ? Strings.Current == Language.Ja ? ApplyAnswer.Ja(refusal) : ApplyAnswer.En(refusal)
        : Strings.Get("gui.update.defer." + Holders.ToString().ToLowerInvariant());
}

/// <summary>Checks the gate on a worker, then checks the live facts again on the window's thread before handing over.</summary>
internal sealed class UpdateHandOff(
    Func<(UpdateMode Mode, StagedUpdate? Stage)> candidate,
    Func<UpdateIdleFacts> facts,
    Func<GateHolders> holders,
    Func<StagedUpdate, string?, ApplyHandOffResult> handOff,
    Action<UpdateDeferral?> defer,
    Action close,
    Func<uint>? tick = null,
    IUpdateService? attempts = null,
    Action<HandOffDecision>? failure = null)
{
    private bool _trying;

    public UpdateHandOff(Func<(UpdateMode, StagedUpdate?)> candidate, Func<UpdateIdleFacts> facts, Func<GateHolders> holders,
        Func<StagedUpdate, ApplyHandOffResult> handOff, Action<UpdateDeferral?> defer, Action close, Func<uint>? tick = null)
        : this(candidate, facts, holders, (stage, _) => handOff(stage), defer, close, tick)
    {
    }

    public async Task TryAsync(bool reconcileOnly = false)
    {
        if (_trying)
        {
            return;
        }

        _trying = true;
        try
        {
            var (mode, staged) = candidate();
            if (attempts is not null && UpdateModes.MayUseNetwork(mode))
            {
                var decision = await Task.Run(attempts.InspectHandOff).ConfigureAwait(true);
                failure?.Invoke(decision);
                if (!decision.MayStart)
                {
                    defer(decision.Refusal is { } why ? new UpdateDeferral(GateHolders.Clear, why) : null);
                    return;
                }

                (mode, staged) = candidate();
            }

            if (reconcileOnly || mode != UpdateMode.Automatic || staged is null)
            {
                defer(null);
                return;
            }

            if (!Idle())
            {
                return;
            }

            var held = await Task.Run(holders).ConfigureAwait(true);
            if (candidate() != (mode, staged) || !Idle())
            {
                return;
            }

            if (held != GateHolders.Clear)
            {
                defer(new UpdateDeferral(held));
                return;
            }

            using var attempt = attempts?.BeginHandOff(staged);
            if (attempts is not null && attempt is null)
            {
                return;
            }

            // The pipe may take seconds to answer. Once it accepts, the very next action on the window's thread is the close.
            var reply = await Task.Run(() => handOff(staged, attempt?.Id)).ConfigureAwait(true);
            if (reply.Accepted)
            {
                close();
                return;
            }

            attempt?.Refused(reply.Refusal);
            defer(new UpdateDeferral(GateHolders.Clear, reply.Refusal));
        }
        finally
        {
            _trying = false;
        }
    }

    private bool Idle()
    {
        var state = facts();
        return state.LastInput is { } last && unchecked((tick?.Invoke() ?? (uint)Environment.TickCount) - last) >= 60_000
            && !state.Dialog && !state.Menu && !state.Drag && !state.Check
            && state.Recording == RecordingActivity.Idle && !state.PendingUac && !state.Elevated;
    }

    /// <summary>
    /// What the hand-off sees of the window's recording: only <see cref="RecordingPhase.Idle"/> is idle, and a phase this map does not know is never idle either
    /// (an update must not hand off while a recording is being prepared, running or being saved).
    /// </summary>
    public static RecordingActivity ActivityOf(RecordingPhase phase) => phase switch
    {
        RecordingPhase.Idle => RecordingActivity.Idle,
        RecordingPhase.Starting => RecordingActivity.Preparing,
        RecordingPhase.Stopping => RecordingActivity.Stopping,
        _ => RecordingActivity.Recording,
    };

    public static UpdateIdleFacts ReadFacts(Form form, bool menu, bool check, RecordingActivity recording, bool pendingUac, bool elevated)
    {
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        var gui = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        var threadKnown = GetGUIThreadInfo(GetCurrentThreadId(), ref gui);
        return new UpdateIdleFacts(
            threadKnown && GetLastInputInfo(ref input) ? input.Tick : null,
            form.IsDisposed || !IsWindowEnabled(form.Handle) || Application.OpenForms.Cast<Form>().Any(window => window.Modal),
            menu || (gui.Flags & 0x1C) != 0, // menu mode, system menu, popup menu
            gui.Capture != 0 || (gui.Flags & 0x02) != 0, // mouse capture or a move/resize
            check, recording, pendingUac, elevated);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Tick;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rectangle CaretRect;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(nint window);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
