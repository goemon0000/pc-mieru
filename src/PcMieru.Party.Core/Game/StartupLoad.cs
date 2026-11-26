using PcMieru.Party.Import;

namespace PcMieru.Party.Game;

/// <summary>
/// The first start followed by the load of the save (codex review of C22, items 1 and 3). WPF-free: the app gives the prompts, the tests a script.
/// <list type="bullet">
/// <item>An unreadable new save is never loaded for writing. A user who goes on anyway gets <see cref="PartyState.LoadReadOnly"/>: the file stays where it is,
/// nothing is moved aside, nothing is created, and the state does not save this run.</item>
/// <item>The flow may have said "a save is there" and the file be gone when it is loaded (somebody removed it in between). The load says so
/// (<c>missing</c>) and the flow runs again, instead of an empty writable state that the save writer would then turn into the first file.</item>
/// </list>
/// </summary>
public static class StartupLoad
{
    /// <summary>The flow is run again this many times when the save disappears under it; after that the state is opened read-only (no empty writable state).</summary>
    public const int MaxRounds = 3;

    /// <summary>The state to run with, or null when the user chose Later or Close (nothing was written). <paramref name="dir"/> is the folder of <c>state.json</c> (the engine's destination folder).</summary>
    public static PartyState? Run(ImportEngine engine, IFirstStartPrompts prompts, string dir) => Run(engine, prompts, dir, null);

    /// <summary>Tests: <paramref name="afterFlow"/> runs after each round of the flow (1-based), just before the load.</summary>
    internal static PartyState? Run(ImportEngine engine, IFirstStartPrompts prompts, string dir, Action<int>? afterFlow)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentException.ThrowIfNullOrEmpty(dir);

        for (int round = 1; round <= MaxRounds; round++)
        {
            var end = FirstStartFlow.Run(engine, prompts);
            afterFlow?.Invoke(round);
            switch (end)
            {
                case FirstStartEnd.Exit:
                    return null;
                case FirstStartEnd.ProceedReadOnly:
                    return PartyState.LoadReadOnly(dir);
            }

            var state = PartyState.LoadExisting(dir, out bool missing);
            if (!missing) return state;
        }

        // It keeps going away. Nothing that could write over whatever comes back: read-only.
        return PartyState.LoadReadOnly(dir);
    }
}
