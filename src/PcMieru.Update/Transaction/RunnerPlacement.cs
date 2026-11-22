using System.Text;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Transaction;

/// <summary>
/// The text of <c>I\update\RECOVER.txt</c> (DESIGN-B 13 item 3): written by the runner BEFORE the first change, never shipped in a bundle. Plain Japanese and English: what happened, what recover.exe does and
/// what it leaves alone (the same two sentences the start gate window says, so the note and the window never tell two stories), the one thing to run (recover.exe in this folder, never "as administrator") and the
/// fallback (unzip the downloaded ZIP into a new folder, and the settings and records are still there). No URL: a page address in a note that is written by a program is something a copy of this note could be
/// turned into a link with. UTF-8 with a byte order mark (so Notepad of every Windows version reads the Japanese) and CRLF line ends.
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
            "recover.exe を使うと、PCミエルのファイルを、更新前または更新後の、そろった状態に戻します。写真や文書など、PCミエルが入れたもの以外のファイルは変わりません。設定や記録はこのフォルダーの外にあるので、そのまま残ります。",
            "次の順に進めてください。",
            "  1. PCミエルをすべて終了します。",
            "  2. このフォルダー（update）の recover.exe をダブルクリックします。黒い画面がしばらく出て、自動で閉じます。「管理者として実行」は使わないでください。",
            "  3. PCミエルを、いつもの方法（デスクトップやスタートメニューの PCミエル）で開きます。",
            "うまくいかないときは、ダウンロードした ZIP を新しいフォルダーに展開して、そちらを使ってください。PCミエルの ZIP は、ふつうは「ダウンロード」フォルダーにあります。見つからなければ、もう一度ダウンロードしてください。設定や記録は、新しいフォルダーで開いても残っています。",
            string.Empty,
            "[English]",
            $"An update of PC Mieru ({what}) may have stopped part-way.",
            "recover.exe puts PC Mieru's files back into one complete version, either the one from before the update or the new one. Files that PC Mieru did not install, such as photos and documents, are not changed. Your settings and records are kept outside this folder, so they stay as they are.",
            "Please do this, in order:",
            "  1. Close PC Mieru completely.",
            "  2. In this folder (the \"update\" folder), double-click recover.exe. A black window stays open for a while and closes by itself. Do not use \"Run as administrator\".",
            "  3. Open PC Mieru the usual way (from the desktop or the Start menu).",
            "If that does not work, unzip the ZIP file you downloaded into a new folder and use that copy. The PC Mieru ZIP is usually in your Downloads folder. If you cannot find it, download it again. Your settings and records are still there when you open PC Mieru from a new folder.",
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

        EnsureRecoverExe(io, paths, line);
        io.WriteReplace(TxSteps.RecoverNote, paths.Note, RecoverNote.Bytes(context.Current.Version, context.Next?.Version));
        io.Reach(TxSteps.RunnerPlace, TxEdge.After);
    }

    /// <summary>
    /// <c>I\update\recover.exe</c> is the recorded Swap.exe, byte for byte: nothing is done when it already is; otherwise the installed <c>I\PCMieru.Swap.exe</c> (which must be the recorded file itself) is copied to
    /// "recover.exe.new", read back and renamed over it. Shared by the transaction (before its first change) and by <see cref="RunnerHandOff.Prepare"/> (the hand-off of the installed Swap.exe).
    /// Throws <see cref="RunnerUnavailableException"/> when the source is not the recorded file or the result does not read back as recorded.
    /// </summary>
    internal static void EnsureRecoverExe(TxIo io, TxPaths paths, InstalledFile line)
    {
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

    /// <summary>
    /// The role of the process whose exe is at <paramref name="selfFinalPath"/> (its FINAL path: <c>InstallGate.InstallFolderOf</c> of the process path, plus the file name) and the install folder it would work on:
    /// the parent of <c>update</c> for a file called recover.exe in a folder called update, the folder itself for anything else. A file anywhere else is <see cref="RunnerRole.Refused"/>.
    /// </summary>
    public static RunnerSite Locate(string selfFinalPath)
    {
        var self = Path.GetFullPath(selfFinalPath);
        var folder = Path.GetDirectoryName(self) ?? string.Empty;
        if (string.Equals(Path.GetFileName(self), RecoverFileName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(folder), GateFile.UpdateFolderName, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(folder) is { Length: > 0 } install)
        {
            return new RunnerSite(Decide(self, install), install);
        }

        return folder.Length == 0 ? new RunnerSite(RunnerRole.Refused, string.Empty) : new RunnerSite(Decide(self, folder), folder);
    }
}

/// <summary>Where a Swap.exe process is: its role and the install folder (empty when it is nowhere it may be).</summary>
public readonly record struct RunnerSite(RunnerRole Role, string InstallFolder);
