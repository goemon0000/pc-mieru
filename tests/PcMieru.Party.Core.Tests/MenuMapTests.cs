using PcMieru.Party.Ui;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// 右クリックの献立と、窓の上のタブの対応表。**どちらからでも同じ設定に辿り着ける**ことを確かめる。
/// </summary>
public class MenuMapTests
{
    [Fact]
    public void Every_command_appears_exactly_once()
    {
        var commands = MenuMap.Entries.Select(e => e.Command).ToList();
        Assert.Equal(commands.Count, commands.Distinct().Count());
        Assert.Equal(Enum.GetValues<MenuCommand>().OrderBy(c => c), commands.OrderBy(c => c));
    }

    [Fact]
    public void The_right_click_order_stays_as_in_v073()
    {
        // v0.7.3 の献立: 前面 / 起動時 / 言語 / 名札▸ / 最初から▸ / 更新▸ / ― / 点検 / ― / 終了
        Assert.Equal(new[]
        {
            MenuCommand.Topmost, MenuCommand.RunAtLogin, MenuCommand.Language,
            MenuCommand.LabelsLevelOnly, MenuCommand.LabelsNameAndLevel, MenuCommand.LabelsNone,
            MenuCommand.ResetStory, MenuCommand.ResetLevels, MenuCommand.ResetAll,
            MenuCommand.Updates, MenuCommand.Checks, MenuCommand.Quit,
        }, MenuMap.Entries.Select(e => e.Command).ToArray());
        Assert.Equal(new[] { MenuCommand.Checks, MenuCommand.Quit },
                     MenuMap.Entries.Where(e => e.SeparatorBefore).Select(e => e.Command).ToArray());
    }

    [Fact]
    public void Only_quit_stays_off_the_tabs()
    {
        // 終了は窓の ✕ にある。タブに置くと設定のつもりで押して閉じてしまう
        Assert.Equal(new[] { MenuCommand.Quit },
                     MenuMap.Entries.Where(e => e.Tab == SettingsTab.None).Select(e => e.Command).ToArray());
    }

    [Fact]
    public void Every_tab_except_the_board_has_something_in_it()
    {
        foreach (var tab in MenuMap.Tabs.Where(t => t != SettingsTab.None))
            Assert.NotEmpty(MenuMap.InTab(tab));
        Assert.Equal(Enum.GetValues<SettingsTab>().OrderBy(t => t), MenuMap.Tabs.OrderBy(t => t));
        Assert.Equal(SettingsTab.None, MenuMap.Tabs[0]);
    }

    [Fact]
    public void Destructive_items_are_only_the_resets_and_live_together()
    {
        var destructive = MenuMap.Entries.Where(e => e.Destructive).ToList();
        Assert.Equal(new[] { MenuCommand.ResetStory, MenuCommand.ResetLevels, MenuCommand.ResetAll },
                     destructive.Select(e => e.Command).ToArray());
        // 献立では一段深い所、タブでは「データ」だけ。押し間違いで数ヶ月ぶんが消えないように
        Assert.All(destructive, e => Assert.Equal(MenuGroup.Reset, e.Group));
        Assert.All(destructive, e => Assert.Equal(SettingsTab.Data, e.Tab));
        Assert.All(MenuMap.InTab(SettingsTab.Data), e => Assert.True(e.Destructive));
    }

    [Fact]
    public void Labels_choices_are_grouped_in_the_view_tab()
    {
        var labels = MenuMap.Entries.Where(e => e.Group == MenuGroup.Labels).ToList();
        Assert.Equal(3, labels.Count);
        Assert.All(labels, e => Assert.Equal(SettingsTab.View, e.Tab));
    }

    [Theory]
    [InlineData(SettingsTab.None, SettingsTab.View, SettingsTab.View)]
    [InlineData(SettingsTab.View, SettingsTab.View, SettingsTab.None)]       // もう一度押すと畳む
    [InlineData(SettingsTab.View, SettingsTab.Data, SettingsTab.Data)]
    [InlineData(SettingsTab.Data, SettingsTab.None, SettingsTab.None)]       // 「ようす」で畳む
    [InlineData(SettingsTab.None, SettingsTab.None, SettingsTab.None)]
    public void Pressing_a_tab_opens_it_and_pressing_it_again_closes_it(SettingsTab current, SettingsTab pressed, SettingsTab next)
    {
        Assert.Equal(next, MenuMap.Toggle(current, pressed));
    }

    [Fact]
    public void Shortcut_digits_follow_the_tab_order()
    {
        Assert.Equal(SettingsTab.None, MenuMap.FromShortcut(1));
        Assert.Equal(SettingsTab.View, MenuMap.FromShortcut(2));
        Assert.Equal(SettingsTab.Data, MenuMap.FromShortcut(MenuMap.Tabs.Count));
        Assert.Null(MenuMap.FromShortcut(0));
        Assert.Null(MenuMap.FromShortcut(MenuMap.Tabs.Count + 1));
    }
}
