using PcMieru.Party.Game;
using PcMieru.Party.Ui;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// 盤の名札（キャラの下の字）。「Lv1」だけでは「どの子か」が読めない、という指摘への直し。
/// 幅は窓の側が実フォントで測る。ここでは 1 字 6px の物差しで、決め方だけを確かめる。
/// </summary>
public class BoardLabelTests
{
    private static double SixPerChar(string s) => s.Length * 6.0;

    [Theory]
    [InlineData("CPU", 1, "CPU Lv1")]
    [InlineData("MEM", 12, "MEM Lv12")]
    [InlineData("GPU0", 3, "GPU0 Lv3")]
    [InlineData("M.2", 5, "M.2 Lv5")]
    [InlineData("HDD", 20, "HDD Lv20")]
    public void The_part_name_comes_before_the_level_when_it_fits(string title, int level, string expected)
        => Assert.Equal(expected, BoardLabel.Text(title, "Lv", level, 94 - 2, SixPerChar));

    [Fact]
    public void A_name_that_does_not_fit_is_dropped_but_Lv_stays()
    {
        // 倍率 0.8 の幅: (32 + 15) × 0.8 - 2 = 35.6px。「GPU0 Lv12」は 9 字 = 54px で入らない
        double narrow = (32 + 15) * 0.8 - 2;
        Assert.Equal("Lv12", BoardLabel.Text("GPU0", "Lv", 12, narrow, SixPerChar));
        // 「M.2 Lv1」も 7 字 = 42px でこの幅には入らない。ちょうど入る幅（42px）なら残る（境目の確認）
        Assert.Equal("Lv1", BoardLabel.Text("M.2", "Lv", 1, narrow, SixPerChar));
        Assert.Equal("CPU Lv1", BoardLabel.Text("CPU", "Lv", 1, 7 * 6.0, SixPerChar));
    }

    [Fact]
    public void An_empty_name_gives_just_the_level()
    {
        Assert.Equal("Lv4", BoardLabel.Text("", "Lv", 4, 100, SixPerChar));
        Assert.Equal("Lv4", BoardLabel.Text("  ", "Lv", 4, 100, SixPerChar));
    }

    [Fact]
    public void The_default_is_name_and_level_so_a_new_user_can_tell_the_parts_apart()
        => Assert.Equal("name", new PartyState().Labels);
}
