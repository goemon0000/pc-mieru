using PcMieru.Party.Ui;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// C38: the window places the name tags only when <see cref="BoardLabel.Fingerprint"/> changes. These tests fix what counts as "changed": anything the tags say or where they sit,
/// and nothing else (an animation frame is not an input at all, so a redraw for a new frame costs no text measuring and no layout).
/// </summary>
public sealed class BoardLabelFingerprintTests
{
    private static readonly (string Title, int Level)[] Seats = [("CPU", 3), ("MEM", 2), ("GPU0", 5)];

    private static int Key(int mode = 1, double scale = 2.0, double dip = 1.0, int width = 300, string lang = "ja", (string Title, int Level)[]? seats = null)
        => BoardLabel.Fingerprint(mode, scale, dip, width, lang, seats ?? Seats);

    [Fact]
    public void The_same_inputs_give_the_same_key_however_often_it_is_asked()
    {
        int first = Key();
        for (int i = 0; i < 5; i++) Assert.Equal(first, Key());
        // a copy of the seats (a new array on every redraw) is the same thing
        Assert.Equal(first, Key(seats: [("CPU", 3), ("MEM", 2), ("GPU0", 5)]));
    }

    [Fact]
    public void Everything_the_tags_say_or_where_they_sit_changes_the_key()
    {
        int baseKey = Key();
        Assert.NotEqual(baseKey, Key(mode: 0));
        Assert.NotEqual(baseKey, Key(scale: 2.5));
        Assert.NotEqual(baseKey, Key(dip: 1.25));
        Assert.NotEqual(baseKey, Key(width: 340));
        Assert.NotEqual(baseKey, Key(lang: "en"));
        Assert.NotEqual(baseKey, Key(seats: [("CPU", 4), ("MEM", 2), ("GPU0", 5)]));            // a level went up
        Assert.NotEqual(baseKey, Key(seats: [("CPU", 3), ("MEM", 2), ("GPU1", 5)]));            // a title changed
        Assert.NotEqual(baseKey, Key(seats: [("MEM", 2), ("CPU", 3), ("GPU0", 5)]));            // the order changed
        Assert.NotEqual(baseKey, Key(seats: [("CPU", 3), ("MEM", 2)]));                         // a seat left
        Assert.NotEqual(baseKey, Key(seats: [("CPU", 3), ("MEM", 2), ("GPU0", 5), ("M.2", 1)])); // a seat came
    }

    [Fact]
    public void No_seats_is_a_key_too_and_null_is_refused()
    {
        _ = Key(seats: []);
        Assert.Throws<ArgumentNullException>(() => BoardLabel.Fingerprint(1, 2.0, 1.0, 300, "ja", null!));
    }
}
