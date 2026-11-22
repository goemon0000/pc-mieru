using System.Text;

namespace PcMieru.Update.Transaction;

/// <summary>
/// The text of <c>I\update\RECOVER.txt</c> (DESIGN-B 13 item 3): written by the runner BEFORE the first change, never shipped in a bundle. Plain Japanese and English, what happened, the one thing to run
/// (recover.exe in this folder) and the fallback (unzip the downloaded ZIP into a new folder). No URL: a page address in a note that is written by a program is something a copy of this note could be turned into
/// a link with. UTF-8 with a byte order mark (so Notepad of every Windows version reads the Japanese) and CRLF line ends.
/// </summary>
public static class RecoverNote
{
    public static string Text(string fromVersion, string? toVersion)
    {
        var what = toVersion is null ? fromVersion : $"{fromVersion} -> {toVersion}";
        return string.Join(
            "\r\n",
            "PCミエル 回復のしかた / PC Mieru: how to recover",
            "========================================",
            string.Empty,
            "[日本語]",
            $"PCミエルの更新（{what}）が、途中で止まったかもしれません。",
            "次の順に進めてください。",
            "  1. PCミエルをすべて終了します。",
            "  2. このフォルダー（update）の recover.exe をダブルクリックします。黒い画面がしばらく出て、自動で閉じます。",
            "  3. PCミエルを、いつもどおり起動します。",
            "recover.exe でも直らないときは、ダウンロードした ZIP を新しいフォルダーに展開して、そちらを使ってください。",
            string.Empty,
            "[English]",
            $"An update of PC Mieru ({what}) may have stopped part-way.",
            "Please do this, in order:",
            "  1. Close PC Mieru completely.",
            "  2. In this folder (the \"update\" folder), double-click recover.exe. A black window stays open for a while and closes by itself.",
            "  3. Start PC Mieru as usual.",
            "If recover.exe does not fix it, unzip the ZIP file you downloaded into a new folder and use that copy.",
            string.Empty);
    }

    /// <summary>The bytes of the note: the BOM, then the text.</summary>
    public static byte[] Bytes(string fromVersion, string? toVersion)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(Text(fromVersion, toVersion))];
    }
}

/// <summary>
/// The real runner placement: <c>I\update\recover.exe</c> is made a verified copy of the current <c>I\PCMieru.Swap.exe</c> (DESIGN-B 13 item 3) and <c>RECOVER.txt</c> is written. The source is checked
/// against the installed record first (a damaged Swap.exe is never copied), the copy goes to "recover.exe.new", is flushed and read back, and is renamed over recover.exe, so a cut leaves the old recover.exe
/// or the new one and never half of one. When recover.exe already has the recorded hash (the common case: the runner IS recover.exe) nothing is copied.
/// </summary>
internal sealed class RealRunnerPlacement : IRunnerPlacement
{
    public const string SwapName = "PCMieru.Swap.exe";

    public static RealRunnerPlacement Instance { get; } = new();

    public void Place(TxIo io, PlacementContext context)
    {
        var paths = context.Paths;
        io.Reach(TxSteps.RunnerPlace, TxEdge.Before);
        if (!context.Current.TryGet(SwapName, out var line))
        {
            throw new RunnerUnavailableException($"the installed record has no {SwapName}");
        }

        var target = paths.Recover;
        if (!IsRecorded(target, line))
        {
            var source = paths.InInstall(SwapName);
            if (!IsRecorded(source, line))
            {
                throw new RunnerUnavailableException($"{SwapName} is missing or is not the recorded file");
            }

            try
            {
                var temporary = target + ".new";
                io.CopyNew(TxSteps.RunnerCopy, source, temporary, line.Bytes, line.Sha256, overwrite: true);
                io.Move(TxSteps.RunnerCopy, temporary, target, replace: true);
            }
            catch (TxSourceMismatchException ex)
            {
                throw new RunnerUnavailableException(ex.Message);
            }
        }

        if (!IsRecorded(target, line))
        {
            throw new RunnerUnavailableException("recover.exe is not the recorded file after it was placed");
        }

        io.WriteReplace(TxSteps.RecoverNote, paths.Note, RecoverNote.Bytes(context.Current.Version, context.Next?.Version));
        io.Reach(TxSteps.RunnerPlace, TxEdge.After);
    }

    /// <summary>Whether the file at <paramref name="path"/> is the recorded one (length and SHA-256 of its real bytes).</summary>
    internal static bool IsRecorded(string path, InstalledFile line) =>
        ManagedSet.Probe(path, out var length, out var sha) == Presence.File && length == line.Bytes && string.Equals(sha, line.Sha256, StringComparison.Ordinal);
}

/// <summary>Which role a process of PCMieru.Swap.exe plays, decided from its own path (DESIGN-B 13 item 3).</summary>
public enum RunnerRole
{
    /// <summary>It is <c>I\update\recover.exe</c>: it may swap and recover.</summary>
    Runner,

    /// <summary>It is <c>I\PCMieru.Swap.exe</c>: it must NOT swap (the swap replaces this very file, and a running exe cannot be replaced). It only places recover.exe and hands over to it.</summary>
    Launcher,

    /// <summary>It is some other file: it does nothing to an install.</summary>
    Refused,
}

/// <summary>The role rule. Kept apart so a test can pin it: the installed Swap.exe never runs a swap.</summary>
public static class RunnerRules
{
    public const string RecoverFileName = "recover.exe";

    public static RunnerRole Decide(string selfPath, string installFolder)
    {
        var self = Path.GetFullPath(selfPath);
        var paths = new TxPaths(installFolder);
        if (string.Equals(self, paths.Recover, StringComparison.OrdinalIgnoreCase))
        {
            return RunnerRole.Runner;
        }

        return string.Equals(self, paths.InInstall(RealRunnerPlacement.SwapName), StringComparison.OrdinalIgnoreCase) ? RunnerRole.Launcher : RunnerRole.Refused;
    }
}
