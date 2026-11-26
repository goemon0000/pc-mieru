using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PcMieru.Party.Game;
using PcMieru.Party.I18n;
using PcMieru.Party.Ui;

namespace PcMieru.Party;

/// <summary>
/// 窓の上のタブと、右クリックの献立。**どちらも <see cref="MenuMap"/> の表から組み立て、押したら <see cref="Execute"/> を通る。**
///
/// 献立は右クリックしないと出ないので、在ることに気づかれにくかった。設定画面を別の窓にすると
/// 「常駐の玩具に画面がもう一枚」になるので、窓の上に細いタブを並べ、選んだときだけ下に欄を開く。
/// 何も選んでいない（「ようす」）間は今までどおり盤だけの姿。右クリックの献立も残す（慣れた人のため）。
/// </summary>
internal sealed partial class PartyWindow
{
    private SettingsTab _tab = SettingsTab.None;
    private readonly Dictionary<SettingsTab, RadioButton> _tabButtons = new();

    // -----------------------------------------------------------------------
    // 項目ごとの中身（献立とタブで共通）
    // -----------------------------------------------------------------------

    private void Execute(MenuCommand command)
    {
        switch (command)
        {
            case MenuCommand.Topmost: ToggleTopmost(); break;
            case MenuCommand.RunAtLogin: break;   // step 5: 「ログイン時に起動」は PCMieru 本体の登録に統合する
            case MenuCommand.Language: ToggleLanguage(); break;
            case MenuCommand.LabelsLevelOnly: SetLabels(Labels.LevelOnly); break;
            case MenuCommand.LabelsNameAndLevel: SetLabels(Labels.NameAndLevel); break;
            case MenuCommand.LabelsNone: SetLabels(Labels.None); break;
            case MenuCommand.ResetStory: ConfirmReset(Strings.ResetStoryAsk, _state.ResetStory); break;
            case MenuCommand.ResetLevels: ConfirmReset(Strings.ResetLevelsAsk, _state.ResetLevels); break;
            case MenuCommand.ResetAll: ConfirmReset(Strings.ResetAllAsk, _state.ResetAll); break;
            case MenuCommand.Checks: OpenChecks(); break;
            case MenuCommand.Quit: Close(); return;
            case MenuCommand.Updates: break;      // 更新は PCMieru の更新係が持つ（この窓には無い）
        }
        RefreshSettings();
    }

    /// <summary>この窓で扱わない項目（献立にもタブにも出さない）。</summary>
    private static bool IsOutOfScope(MenuCommand command) =>
        command is MenuCommand.RunAtLogin or MenuCommand.Updates;   // step 5 / PCMieru の更新係

    /// <summary>選ばれている印を付けるか。付けない項目は null。</summary>
    private bool? IsChecked(MenuCommand command) => command switch
    {
        MenuCommand.Topmost => Topmost,
        MenuCommand.LabelsLevelOnly => _labels == Labels.LevelOnly,
        MenuCommand.LabelsNameAndLevel => _labels == Labels.NameAndLevel,
        MenuCommand.LabelsNone => _labels == Labels.None,
        _ => null,
    };

    private static string HeaderOf(MenuCommand command) => command switch
    {
        MenuCommand.Topmost => Strings.TopmostTip,
        MenuCommand.RunAtLogin => Strings.RunAtLogin,
        MenuCommand.Language => Strings.Lang == "ja" ? "English" : "日本語",
        MenuCommand.LabelsLevelOnly => Strings.LabelLevelOnly,
        MenuCommand.LabelsNameAndLevel => Strings.LabelNameAndLevel,
        MenuCommand.LabelsNone => Strings.LabelNone,
        MenuCommand.ResetStory => Strings.ResetStory,
        MenuCommand.ResetLevels => Strings.ResetLevels,
        MenuCommand.ResetAll => Strings.ResetAll,
        MenuCommand.Checks => Strings.ChecksMenu,
        MenuCommand.Quit => Strings.Close,
        _ => command.ToString(),
    };

    private static string GroupHeader(MenuGroup group) => group switch
    {
        MenuGroup.Labels => Strings.LabelMenu,
        MenuGroup.Reset => Strings.ResetMenu,
        _ => "",
    };

    private static string TabTitle(SettingsTab tab) => tab switch
    {
        SettingsTab.View => Strings.TabView,
        SettingsTab.Startup => Strings.TabStartup,
        SettingsTab.Checks => Strings.TabChecks,
        SettingsTab.Data => Strings.TabData,
        _ => Strings.TabBoard,
    };

    private void ToggleTopmost()
    {
        Topmost = !Topmost;
        _state.Topmost = Topmost;
        TopmostButton.Opacity = Topmost ? 1.0 : 0.45;
        RefreshSettings();
        SaveState();
    }

    private void ToggleLanguage() => SetWindowLanguage(Strings.Lang == "ja" ? "en" : "ja", persist: true);

    /// <param name="persist">切り替えボタンだけ true（保存の Lang に書く）。--lang と、PCMieru の言語に追う外からの切り替えは false（この回の見た目だけで、保存には書かない）。</param>
    private void SetWindowLanguage(string lang, bool persist)
    {
        Strings.Lang = lang;
        if (persist) _state.Lang = lang;
        Title = PartyStrings.Title(lang == "ja" ? PartyLanguage.Ja : PartyLanguage.En);
        _slots = _frame is { } f ? PartyComposition.Build(f, _gpuFrame, Strings.Lang) : _slots;
        ApplyLanguage();
        _lastSignature = 0;
        if (persist) SaveState();
    }

    private void SetLabels(Labels mode)
    {
        _labels = mode;
        _state.Labels = mode switch
        {
            Labels.NameAndLevel => "name",
            Labels.None => "none",
            _ => "level",
        };
        PlaceLevelLabels();
        SaveState();
    }

    // -----------------------------------------------------------------------
    // 右クリックの献立（v0.7.3 と同じ並び・同じ文言）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 右クリックの献立。**設定のための窓は作らない**（タブは同じ窓の中に開く）。
    /// 取り返しのつかない「最初から」は一段深い所に置いて必ず確認する（押し間違いで数ヶ月ぶんが消えないように）。
    /// </summary>
    private void ShowMenu()
    {
        var menu = new ContextMenu();
        var groups = new Dictionary<MenuGroup, MenuItem>();

        foreach (var entry in MenuMap.Entries)
        {
            if (IsOutOfScope(entry.Command)) continue;   // step 5（起動時登録）・PCMieru の更新係
            if (entry.SeparatorBefore) menu.Items.Add(new Separator());

            bool? check = IsChecked(entry.Command);
            var item = new MenuItem { Header = HeaderOf(entry.Command), IsCheckable = check is not null, IsChecked = check == true };
            var command = entry.Command;
            item.Click += (_, _) => Execute(command);

            if (entry.Group == MenuGroup.None)
            {
                menu.Items.Add(item);
                continue;
            }
            if (!groups.TryGetValue(entry.Group, out var parent))
            {
                parent = new MenuItem { Header = GroupHeader(entry.Group) };
                groups[entry.Group] = parent;
                menu.Items.Add(parent);
            }
            parent.Items.Add(item);
        }

        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

    // -----------------------------------------------------------------------
    // タブ
    // -----------------------------------------------------------------------

    /// <summary>
    /// タブを並べ直す（起動時と言語を切り替えたとき）。
    /// 「起動」タブは step 5 まで出さない。**Ctrl+n の n は <see cref="MenuMap.Tabs"/> の並びのまま**（出さないタブの番号は空く）。
    /// </summary>
    private void BuildTabs()
    {
        TabStrip.Children.Clear();
        _tabButtons.Clear();
        AutomationProperties.SetName(TabStrip, Strings.TabsTip);
        for (int i = 0; i < MenuMap.Tabs.Count; i++)
        {
            var tab = MenuMap.Tabs[i];
            if (tab == SettingsTab.Startup) continue;   // step 5
            var b = new RadioButton
            {
                Content = TabTitle(tab),
                GroupName = "PartyTabs",
                Style = (Style)FindResource("TabButton"),
                IsChecked = tab == _tab,
                ToolTip = $"{TabTitle(tab)}  (Ctrl+{i + 1})\n{Strings.TabsTip}",
            };
            AutomationProperties.SetName(b, TabTitle(tab));
            b.Click += (_, _) => SelectTab(MenuMap.Toggle(_tab, tab));
            _tabButtons[tab] = b;
            TabStrip.Children.Add(b);
        }
    }

    /// <summary>smoke の撮影（<c>--shot</c> ＋ <c>--tab</c>）だけが使う。撮る前にそのタブを開く。</summary>
    internal void OpenTabForShot(SettingsTab tab) => SelectTab(tab);

    /// <summary>そのタブを開く（None なら畳む）。</summary>
    private void SelectTab(SettingsTab tab)
    {
        _tab = tab;
        foreach (var (t, b) in _tabButtons) b.IsChecked = t == tab;
        RefreshSettings();
        // 欄の高さぶん窓が伸びる。下の端が画面の外へ出ないように
        Dispatcher.BeginInvoke(KeepOnScreen, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Ctrl+1〜5 でタブ、Esc で畳む。</summary>
    private void OnTabKeys(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _tab != SettingsTab.None)
        {
            SelectTab(SettingsTab.None);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        int digit = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D0
                  : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad0 : 0;
        if (MenuMap.FromShortcut(digit) is not { } tab) return;
        if (!_tabButtons.TryGetValue(tab, out var b)) return;   // 出していないタブ（起動）は何もしない
        SelectTab(tab);
        b.Focus();
        e.Handled = true;
    }

    /// <summary>
    /// 開いている欄を作り直す。**欄が開いているときだけ**作る（畳んでいる間は何もしない＝常駐の負担は増えない）。
    /// 項目は数個なので、差分を取らずに丸ごと作り直す。
    /// </summary>
    private void RefreshSettings()
    {
        // 作り直すと、押したばかりの項目から手元（キーボードの焦点）が外れる。同じ番目の項目へ戻す
        int focused = Focusables().ToList().FindIndex(c => c.IsKeyboardFocusWithin);
        SettingsStack.Children.Clear();
        if (_tab == SettingsTab.None)
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            return;
        }
        SettingsPanel.Visibility = Visibility.Visible;

        switch (_tab)
        {
            case SettingsTab.View: BuildViewTab(); break;
            case SettingsTab.Checks: BuildChecksTab(); break;
            case SettingsTab.Data: BuildDataTab(); break;
            // SettingsTab.Startup: step 5
        }
        if (focused >= 0 && Focusables().ElementAtOrDefault(focused) is { } again)
            Dispatcher.BeginInvoke(() => again.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>欄の中の押せる物（並んでいる順）。</summary>
    private IEnumerable<Control> Focusables() =>
        SettingsStack.Children.OfType<Panel>().SelectMany(p => p.Children.OfType<Control>());

    private void BuildViewTab()
    {
        var row = Row();
        row.Children.Add(Option(MenuCommand.Topmost));
        SettingsStack.Children.Add(row);

        SettingsStack.Children.Add(Heading(Strings.LanguageHeader));
        var lang = Row();
        // 言語は 2 択の切り替えだが、欄では「いまどちらか」が見える形にする
        foreach (var (code, name) in new[] { ("ja", "日本語"), ("en", "English") })
        {
            var b = new RadioButton { Content = name, Style = (Style)FindResource("OptionToggle"), IsChecked = Strings.Lang == code };
            b.Click += (_, _) => { if (Strings.Lang != code) Execute(MenuCommand.Language); else RefreshSettings(); };
            lang.Children.Add(b);
        }
        SettingsStack.Children.Add(lang);

        SettingsStack.Children.Add(Heading(Strings.LabelMenu));
        var labels = Row();
        foreach (var e in MenuMap.InTab(SettingsTab.View).Where(e => e.Group == MenuGroup.Labels))
            labels.Children.Add(Option(e.Command, radio: true));
        SettingsStack.Children.Add(labels);
    }

    private void BuildChecksTab()
    {
        SettingsStack.Children.Add(Note(Strings.ChecksAbout));
        var row = Row();
        row.Children.Add(ActionButton(Strings.ChecksOpen, () => Execute(MenuCommand.Checks)));
        SettingsStack.Children.Add(row);
    }

    private void BuildDataTab()
    {
        SettingsStack.Children.Add(Heading(Strings.ResetMenu));
        SettingsStack.Children.Add(Note(Strings.DataAbout));
        var row = Row();
        foreach (var e in MenuMap.InTab(SettingsTab.Data))
        {
            var command = e.Command;
            // 確認は Execute → ConfirmReset が必ず出す（献立と同じ道を通る）
            row.Children.Add(ActionButton(HeaderOf(command), () => Execute(command)));
        }
        SettingsStack.Children.Add(row);
        BuildGpuSlots();
    }

    /// <summary>
    /// GPU の番号の一覧。番号・名前・メモリ・状態だけ（シリアルやバス番号は出さない）。
    /// 取り置いていない番号には「手放す」、取り置いた番号には「リセット」（確認 → 経験値を消し、装備は持ち物へ → 保存）。
    /// </summary>
    private void BuildGpuSlots()
    {
        SettingsStack.Children.Add(Heading(Strings.GpuSlotsHeader));
        SettingsStack.Children.Add(Note(Strings.GpuSlotsAbout));
        if (_slotMap.Records.Count == 0)
        {
            SettingsStack.Children.Add(Note(Strings.GpuSlotsNone));
            return;
        }

        var present = _gpuFrame.Adapters.Where(a => a.Slot is not null).Select(a => a.Slot!.Value).ToHashSet();
        foreach (var r in _slotMap.Records.OrderBy(r => r.Slot))
        {
            int slot = r.Slot;
            string state = r.Retired ? Strings.GpuStateRetired : present.Contains(slot) ? Strings.GpuStateInUse : Strings.GpuStateAbsent;
            string memory = r.VramBytes > 0 ? $"{r.VramBytes / 1073741824.0:0.#} GB" : "—";
            string name = string.IsNullOrEmpty(r.Name) ? "—" : r.Name;
            var row = Row();
            row.Children.Add(new TextBlock
            {
                Text = $"GPU{slot}  {name}  {memory}  {state}", FontSize = 11, Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center, Foreground = (System.Windows.Media.Brush)FindResource("Accent"),
            });
            if (r.Retired)
                row.Children.Add(ActionButton(Strings.GpuReset, () => ConfirmReset(Strings.GpuResetAsk(slot), () =>
                {
                    _slotMap.ResetSlotKeepingGear(_state, _director.Adventure, slot);
                    RefreshSettings();
                }, Strings.GpuResetTitle)));
            else if (!present.Contains(slot))
                // 使用中のカードには出さない。手放すと、そのカードは次の採取で新しい番号へ移ってしまう（terra 4b の 1）
                row.Children.Add(ActionButton(Strings.GpuForget, () => ConfirmReset(Strings.GpuForgetAsk(slot), () =>
                {
                    _slotMap.ForgetSlot(_state, slot);
                    RefreshSettings();
                }, Strings.GpuForgetTitle)));
            SettingsStack.Children.Add(row);
        }
    }

    // --- 欄の部品 ---

    private static WrapPanel Row() => new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };

    private TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 10, Margin = new Thickness(0, 4, 0, 1),
        Foreground = (System.Windows.Media.Brush)FindResource("Accent"),
    };

    private TextBlock Note(string text) => new()
    {
        Text = text, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3),
        Foreground = (System.Windows.Media.Brush)FindResource("Sub"),
    };

    private ToggleButton Option(MenuCommand command, bool radio = false)
    {
        ToggleButton b = radio ? new RadioButton() : new CheckBox();
        b.Content = HeaderOf(command);
        b.Style = (Style)FindResource("OptionToggle");
        b.IsChecked = IsChecked(command) == true;
        // 押した時点で印は先に変わる。中身の処理のあと欄を作り直すので、実際の状態に戻る
        b.Click += (_, _) => Execute(command);
        return b;
    }

    private Button ActionButton(string text, Action onClick)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("ActionButton") };
        b.Click += (_, _) => onClick();
        return b;
    }
}
