using PcMieru.Core.Siblings;

namespace PcMieru.Core.Tests.Siblings;

/// <summary>
/// DESIGN-C 4.1 items 1-2, the main window's side: a hint that old progress is waiting, from the existence of three files. Every test uses a temporary roaming folder of its own: the real
/// %APPDATA% is never named (RealRoamingRoot is not called).
/// </summary>
public sealed class PartyImportHintTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcmieru-partyhint-" + Guid.NewGuid().ToString("N"));

    public PartyImportHintTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Save(params string[] folders)
    {
        foreach (var folder in folders)
        {
            var dir = Path.Combine(_root, folder);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "state.json"), "{}");
        }
    }

    [Fact]
    public void Nothing_anywhere_says_nothing()
    {
        Assert.Equal(PartyImportHintState.None, PartyImportHint.Check(_root));
    }

    [Fact]
    public void An_old_save_of_Parts_Life_is_noticed()
    {
        Save("PartsLife");
        Assert.Equal(PartyImportHintState.FromPartsLife, PartyImportHint.Check(_root));
    }

    [Fact]
    public void Only_the_former_name_is_noticed_as_that()
    {
        Save("PartsParty");
        Assert.Equal(PartyImportHintState.FromPartsParty, PartyImportHint.Check(_root));
    }

    [Fact]
    public void Parts_Life_wins_over_its_former_name_when_both_are_there()
    {
        Save("PartsLife", "PartsParty");
        Assert.Equal(PartyImportHintState.FromPartsLife, PartyImportHint.Check(_root));
    }

    [Fact]
    public void A_save_of_the_new_window_means_there_is_nothing_to_say()
    {
        Save("PartsLife", "PartsParty", Path.Combine("PCMieru", "party"));
        Assert.Equal(PartyImportHintState.None, PartyImportHint.Check(_root));
    }

    [Fact]
    public void A_folder_without_the_save_file_does_not_count()
    {
        Directory.CreateDirectory(Path.Combine(_root, "PartsLife"));
        File.WriteAllText(Path.Combine(_root, "PartsLife", "other.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_root, "PartsParty", "state.json")); // a folder of that name is not a save
        Assert.Equal(PartyImportHintState.None, PartyImportHint.Check(_root));
    }

    [Fact]
    public void A_root_that_cannot_be_looked_at_is_none_and_never_throws()
    {
        Assert.Equal(PartyImportHintState.None, PartyImportHint.Check(string.Empty));
        Assert.Equal(PartyImportHintState.None, PartyImportHint.Check("   "));
        Assert.Equal(PartyImportHintState.None, PartyImportHint.Check(Path.Combine(_root, "no-such-folder")));
        Assert.Equal(PartyImportHintState.None, PartyImportHint.Check("bad\0root"));
    }

    [Fact]
    public void It_does_not_open_the_save_for_reading_or_writing()
    {
        // An unreadable file (held open with no sharing) is still "there": the hint is existence only and never opens it.
        Save("PartsLife");
        using var held = new FileStream(Path.Combine(_root, "PartsLife", "state.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(PartyImportHintState.FromPartsLife, PartyImportHint.Check(_root));
        var before = File.GetLastWriteTimeUtc(Path.Combine(_root, "PartsLife", "state.json"));
        Assert.Equal(PartyImportHintState.FromPartsLife, PartyImportHint.Check(_root));
        Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(_root, "PartsLife", "state.json")));
    }
}
