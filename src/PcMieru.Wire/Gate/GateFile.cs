using System.Text;

namespace PcMieru.Wire.Gate;

/// <summary>
/// The gate file of an install and the names around it (docs/unify/DESIGN-B.md 4). <c>I\PCMieru.gate.txt</c> is a plain file that exists from the moment the
/// release is unzipped; every program of the app opens it for as long as it works (<see cref="InstallGate"/>), and the swapper of an update takes it exclusively,
/// so "nobody of this install is running" is something the file system answers and no name, port or window has to.
///
/// The content is fixed here, in the client, and has no version in it, so it never changes from one release to the next: the same bytes in every release is what lets
/// the update transaction leave the file out of swap and rollback (<see cref="IsExcludedFromSwap"/>) and what lets the validator check one SHA-256 for it
/// (<see cref="Sha256Hex"/>). assets/PCMieru.gate.txt is the same bytes as a file (what a dev build copies next to the exes and what tools/publish.ps1 puts in the
/// publish folder); a test keeps the two, and the hash, in step.
/// </summary>
public static class GateFile
{
    /// <summary>The file name, at the top of the install folder.</summary>
    public const string FileName = "PCMieru.gate.txt";

    /// <summary>The folder of the transaction records, <c>I\update</c>. Nothing in it may be a reparse point.</summary>
    public const string UpdateFolderName = "update";

    /// <summary>The persistent state of a swap or a recovery, in <c>I\update</c>. Present means "a transaction was left open".</summary>
    public const string JournalName = "journal.json";

    /// <summary>The temporary of a journal write. Alone (no <see cref="JournalName"/>) it means a write was cut off.</summary>
    public const string JournalTempName = "journal.json.tmp";

    /// <summary>
    /// The last result of the swapper, in <c>I\update</c>. STEP 4 writes it as a record. The start gate does NOT read it any more (a record that is read for a word in its first bytes can be
    /// made to say "failed" or "fine" by what else it contains): "the recovery failed" is the sentinel file <see cref="RecoveryFailedName"/>. The tests keep this name as a decoy.
    /// </summary>
    public const string LastResultName = "last-result.json";

    /// <summary>
    /// The sentinel that says "the recovery was tried and failed", in <c>I\update</c>: a file whose PRESENCE is the whole message (nothing in it is read, so there is no parser, no size limit and no word to
    /// find). The start gate looks at it with the same attribute read as the journal (DESIGN-B 4). STEP 4 writes it when a recovery fails, and ONLY recover.exe's success removes it: a swapper
    /// or an ordinary start never deletes it, so a failed recovery cannot be forgotten by a later update that happens to work.
    /// </summary>
    public const string RecoveryFailedName = "recovery-failed";

    /// <summary>The recovery note of the install, in <c>I\update</c>. STEP 4 writes it (and puts the recovery executable next to it); step 3 only names where to look.</summary>
    public const string RecoverNoteName = "RECOVER.txt";

    /// <summary>The SHA-256 of <see cref="Bytes"/>, 64 lower-case hex digits. The validator (BundleChecker) and publish.ps1 compare against this.</summary>
    public const string Sha256Hex = "43f8f509e9f3f84d09acfffc48f1f04347ddb437c384fef0d43ac57ca2f94432";

    /// <summary>
    /// The text of the file: what it is and what not to do with it, in English and Japanese (a person who opens it should not have to guess), with no version and no date.
    /// UTF-8 without a byte order mark, LF line ends, one final LF.
    /// </summary>
    public static string Text { get; } = string.Join(
        "\n",
        "PC Mieru: start gate file. Please leave it where it is.",
        string.Empty,
        "This file holds no settings and no personal data. Every PC Mieru program keeps it open while it runs, and an update waits until all of them have closed it. If you delete, move or edit it, PC Mieru will not start and cannot update itself. To repair it, download PC Mieru's ZIP file again and unzip it into a new folder.",
        string.Empty,
        "PCミエルの起動の門のファイルです。そのままにしておいてください。",
        string.Empty,
        "このファイルには設定も個人の情報も入っていません。PCミエルのプログラムは、動いている間このファイルを開いたままにし、更新はそのすべてが閉じるまで待ちます。削除・移動・編集するとPCミエルは起動せず、自分で更新もできなくなります。直すには、PCミエルの ZIP をもう一度ダウンロードして、新しいフォルダーに展開してください。",
        string.Empty);

    /// <summary>The exact bytes of the file (a new array each time).</summary>
    public static byte[] Bytes() => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Text);

    /// <summary>
    /// STEP 4 uses this: the gate file is the same bytes in every release and the swapper holds it open itself, so a transaction neither replaces it nor takes it back
    /// ("門ファイルは一貫して同じ実体を使う", DESIGN-B 4/5). Compared without regard to case, as the names of a bundle are.
    /// </summary>
    public static bool IsExcludedFromSwap(string bundleRelativeName) =>
        string.Equals(bundleRelativeName, FileName, StringComparison.OrdinalIgnoreCase);
}
