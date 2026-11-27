namespace PcMieru.Gui.Tests;

/// <summary>
/// C38 source pins for Party's idle CPU (DESIGN-C 7: no heavier than v0.7.4). A WPF window cannot be built in a unit test, so these read the sources. The profile (C38) found the cost in a redraw for each new
/// animation frame: every name tag measured with FormattedText, its width written twice, and the board's transform renewed and its measure invalidated although neither had changed.
/// </summary>
public sealed class PartyIdleCostTests
{
    private static string Party(string name) => File.ReadAllText(Path.Combine(Harness.Root, "src", "PcMieru.Party", name.Replace('/', Path.DirectorySeparatorChar)));

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start > 0, "not found: " + signature);
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }

        throw new InvalidOperationException("unbalanced braces after " + signature);
    }

    [Fact]
    public void The_name_tags_are_placed_only_when_their_fingerprint_changed_and_before_any_text_is_measured()
    {
        var body = Method(Party("PartyWindow.xaml.cs"), "private void PlaceLevelLabels()");
        int key = body.IndexOf("BoardLabel.Fingerprint(", StringComparison.Ordinal);
        int skip = body.IndexOf("if (_labelKeySet && labelKey == _lastLabelKey) return;", StringComparison.Ordinal);
        int measure = body.IndexOf("new FormattedText(", StringComparison.Ordinal);
        int place = body.IndexOf("Canvas.SetLeft(", StringComparison.Ordinal);
        Assert.True(key > 0 && skip > key && measure > skip && place > skip, $"fingerprint < skip < measure/place: {key} {skip} {measure} {place}");
    }

    [Fact]
    public void A_tag_width_is_written_once_per_placement_and_a_reset_forces_a_new_placement()
    {
        var window = Party("PartyWindow.xaml.cs");
        var body = Method(window, "private void PlaceLevelLabels()");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, @"\bt\.Width\s*="));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, @"new FormattedText\("));
        // the reset-progress path resets both signatures together
        Assert.Contains("_lastSignature = 0;\n        _labelKeySet = false;", window.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_board_surface_renews_its_transform_and_invalidates_its_measure_only_when_the_scale_or_size_changed()
    {
        var body = Method(Party("Rendering/BoardSurface.cs"), "public void Redraw(");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, @"new ScaleTransform\("));
        int guardT = body.IndexOf("if (!_transformSet || _scale != scaleBefore)", StringComparison.Ordinal);
        int newT = body.IndexOf("new ScaleTransform(", StringComparison.Ordinal);
        int guardM = body.IndexOf("if (_art != artBefore || _scale != scaleBefore) InvalidateMeasure();", StringComparison.Ordinal);
        Assert.True(guardT > 0 && newT > guardT && guardM > newT, $"guards in place: {guardT} {newT} {guardM}");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, @"InvalidateMeasure\(\)"));
    }
}
