using PcMieru.Party.Game;

namespace PcMieru.Party.Core.Tests;

/// <summary>保存の書き手（DESIGN-C 3 の 6）と、新しい保存先（DESIGN-C 4.1）。</summary>
public sealed class PartySaveWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "party-writer-test-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_dir, "state.json");

    public PartySaveWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Default_directory_is_the_new_home_and_never_the_old_products()
    {
        var dir = PartyState.Dir;
        Assert.EndsWith(Path.Combine("PCMieru", "party"), dir);
        Assert.DoesNotContain("PartsLife", dir);
        Assert.DoesNotContain("PartsParty", dir);
    }

    [Fact]
    public void A_state_gets_its_save_path_from_the_folder_it_was_loaded_from()
    {
        var state = PartyState.Load(_dir);
        Assert.Equal(StatePath, state.SavePath);
    }

    [Fact]
    public void Save_path_is_not_written_into_the_save()
    {
        Assert.DoesNotContain("SavePath", PartyState.Load(_dir).ToJson());
    }

    [Fact]
    public void Latest_wins_while_a_write_is_in_progress()
    {
        var written = new List<string>();
        using var release = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var writer = new PartySaveWriter(false, (path, text) =>
        {
            lock (written) written.Add(text);
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        });

        writer.Submit("A", StatePath);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        writer.Submit("B", StatePath);
        writer.Submit("C", StatePath);
        release.Set();

        Assert.True(writer.Flush(TimeSpan.FromSeconds(10)).Ok);
        Assert.Equal(new[] { "A", "C" }, written);
        Assert.NotNull(writer.LastSavedUtc);
    }

    [Fact]
    public void A_real_write_lands_in_the_file_and_marks_saved()
    {
        var writer = new PartySaveWriter(false);
        Assert.Null(writer.LastSavedUtc);

        writer.Submit("{\"x\":1}", StatePath);

        Assert.True(writer.Flush(TimeSpan.FromSeconds(10)).Ok);
        Assert.Equal("{\"x\":1}", File.ReadAllText(StatePath));
        Assert.False(File.Exists(StatePath + ".tmp"));
        Assert.NotNull(writer.LastSavedUtc);
    }

    [Fact]
    public void A_failed_write_is_reported_and_not_marked_saved()
    {
        // The parent of the target is a file, so the folder cannot be made.
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        var writer = new PartySaveWriter(false);

        writer.Submit("{}", Path.Combine(blocker, "state.json"));
        var result = writer.Flush(TimeSpan.FromSeconds(10));

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrEmpty(result.Message));
        Assert.False(writer.Last.Ok);
        Assert.Null(writer.LastSavedUtc);

        // The next good write clears the failure and only then counts as saved.
        writer.Submit("{}", StatePath);
        Assert.True(writer.Flush(TimeSpan.FromSeconds(10)).Ok);
        Assert.NotNull(writer.LastSavedUtc);
    }

    [Fact]
    public void A_blocked_state_writes_nothing()
    {
        var writer = new PartySaveWriter(saveBlocked: true);

        writer.Submit("{}", StatePath);

        Assert.True(writer.Flush(TimeSpan.FromSeconds(10)).Ok);
        Assert.False(File.Exists(StatePath));
        Assert.False(File.Exists(StatePath + ".tmp"));
        Assert.Null(writer.LastSavedUtc);
    }

    [Fact]
    public void Flush_with_nothing_submitted_is_a_success()
    {
        Assert.True(new PartySaveWriter(false).Flush(TimeSpan.FromMilliseconds(100)).Ok);
    }
}
