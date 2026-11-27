using PcMieru.Party.Adventure;
using PcMieru.Party.Game;

namespace PcMieru.Party.Core.Tests;

public class EquipmentTests
{
    private static readonly AdventureBook Book = TestData.Book;

    private static Member[] Party() => Equipment.SortMembers(
        Rig.Desktop.Select(d => new Member(d.Key, d.Id)));

    /// <summary>乱数で、持ち物と装備がばらばらの状態を作る。</summary>
    private static AdventureState RandomState(Random rng, Member[] members)
    {
        var s = new AdventureState();
        var items = Book.Items;
        foreach (var m in members)
        {
            var eq = new[] { "", "", "" };
            for (int slot = 0; slot < 3; slot++)
            {
                if (rng.NextDouble() < 0.3) continue;   // 何も着けていない枠もある
                var pool = items.Where(i => (int)i.SlotValue == slot).ToList();
                eq[slot] = pool[rng.Next(pool.Count)].Id;
            }
            s.Equipped[m.Key] = eq;
        }
        int n = rng.Next(0, AdventureEngine.InventoryCap + 1);
        for (int i = 0; i < n; i++) s.Inventory.Add(items[rng.Next(items.Count)].Id);
        return s;
    }

    private static Dictionary<string, int> AllItems(AdventureState s)
    {
        var bag = new Dictionary<string, int>();
        foreach (var id in s.Inventory.Concat(s.Equipped.Values.SelectMany(e => e)).Where(x => x.Length > 0))
            bag[id] = bag.GetValueOrDefault(id) + 1;
        return bag;
    }

    [Fact]
    public void AutoEquip_never_lowers_any_members_score()
    {
        var members = Party();
        for (int seed = 0; seed < 300; seed++)
        {
            var rng = new Random(seed);
            var s = RandomState(rng, members);
            var stance = (Stance)(seed % 3);
            // 着け替えを決める点（構えに依らない）で見る
            var before = members.Select(m => Equipment.MemberScore(Book, s, m, Stance.Balanced)).ToArray();

            Equipment.AutoEquip(Book, s, members, stance, out _, out _);

            for (int i = 0; i < members.Length; i++)
                Assert.True(Equipment.MemberScore(Book, s, members[i], Stance.Balanced) >= before[i],
                    $"seed {seed}: {members[i].Key} の点が下がった");
        }
    }

    [Fact]
    public void AutoEquip_reaches_a_fixed_point()
    {
        var members = Party();
        for (int seed = 0; seed < 200; seed++)
        {
            var s = RandomState(new Random(seed), members);
            var stance = (Stance)(seed % 3);
            Equipment.AutoEquip(Book, s, members, stance, out _, out _);
            // 二度目は何も替えない（替えるたびに真に良くなるので、いずれ止まる）
            Assert.Equal(0, Equipment.AutoEquip(Book, s, members, stance, out _, out _));
        }
    }

    [Fact]
    public void AutoEquip_neither_creates_nor_loses_items()
    {
        var members = Party();
        for (int seed = 0; seed < 200; seed++)
        {
            var s = RandomState(new Random(seed), members);
            var before = AllItems(s);
            Equipment.AutoEquip(Book, s, members, Stance.Balanced, out _, out _);
            Assert.Equal(before.OrderBy(k => k.Key), AllItems(s).OrderBy(k => k.Key));
        }
    }

    [Fact]
    public void AutoEquip_is_deterministic_and_ignores_inventory_order()
    {
        var members = Party();
        for (int seed = 0; seed < 200; seed++)
        {
            var a = RandomState(new Random(seed), members);
            var b = RandomState(new Random(seed), members);
            // b の持ち物だけ並びを変える
            var shuffled = b.Inventory.OrderBy(_ => new Random(seed + 99).Next()).ToList();
            b.Inventory = shuffled.AsEnumerable().Reverse().ToList();

            var stance = (Stance)(seed % 3);
            Equipment.AutoEquip(Book, a, members, stance, out _, out _);
            Equipment.AutoEquip(Book, b, members, stance, out _, out _);

            foreach (var m in members)
                Assert.Equal(a.Equipped[m.Key], b.Equipped[m.Key]);
            Assert.Equal(a.Inventory.OrderBy(x => x, StringComparer.Ordinal),
                         b.Inventory.OrderBy(x => x, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Each_role_takes_the_item_that_fits_its_part()
    {
        var s = new AdventureState();
        var members = Equipment.SortMembers(new[] { new Member("cpu#0", "cpu"), new Member("gpu#0", "gpu") });
        s.Inventory.AddRange(new[] { "copper-spatula", "glowing-stone" });   // 同じ格の、攻めの道具と術の道具

        Equipment.AutoEquip(Book, s, members, Stance.Balanced, out _, out _);

        Assert.Equal("copper-spatula", s.Equipped["cpu#0"][(int)EquipSlot.Hand]);
        Assert.Equal("glowing-stone", s.Equipped["gpu#0"][(int)EquipSlot.Hand]);
    }

    [Fact]
    public void Restricted_items_go_only_to_the_part_they_fit()
    {
        var blade = Book.Item("wide-blade")!;
        Assert.True(Equipment.ItemScore(blade, "cpu-cooler", Stance.Balanced) > 0);
        Assert.Equal(0, Equipment.ItemScore(blade, "cpu", Stance.Balanced));

        var s = new AdventureState();
        s.Inventory.Add("wide-blade");
        var members = Equipment.SortMembers(new[] { new Member("cpu#0", "cpu"), new Member("cpu-cooler#0", "cpu-cooler") });
        Equipment.AutoEquip(Book, s, members, Stance.Balanced, out _, out _);
        Assert.Equal("wide-blade", s.Equipped["cpu-cooler#0"][(int)EquipSlot.Hand]);
        Assert.Equal("", s.Equipped["cpu#0"][(int)EquipSlot.Hand]);
    }

    [Fact]
    public void Ties_keep_the_current_item()
    {
        // 今の物と同じ点の物があっても替えない（替えて点が変わらないなら、替えない）
        var s = new AdventureState();
        var m = new Member("cpu#0", "cpu");
        s.Equipped[m.Key] = new[] { "copper-spatula", "", "" };
        s.Inventory.Add("copper-spatula");
        Assert.Equal(0, Equipment.AutoEquip(Book, s, new[] { m }, Stance.Balanced, out _, out _));
    }

    [Fact]
    public void Stance_changes_what_counts_as_best()
    {
        // 大胆なら攻め、慎重なら粘りが重くなる
        var spatula = Book.Item("copper-spatula")!;   // atk/spd
        var wrap = Book.Item("cotton-wrap")!;         // end/spd
        Assert.True(Equipment.ItemScore(spatula, "psu", Stance.Bold) > Equipment.ItemScore(spatula, "psu", Stance.Careful));
        Assert.True(Equipment.ItemScore(wrap, "psu", Stance.Careful) > Equipment.ItemScore(wrap, "psu", Stance.Bold));
    }

    [Fact]
    public void Alternating_stances_do_not_flip_the_hdd_charm()
    {
        // 構えの重みで比べていた頃は、大胆と慎重が替わるたびに電池と帳面を往復した
        var hdd = new Member("hdd", "hdd");
        Assert.True(Equipment.ItemScore(Book.Item("catalog-notebook"), "hdd", Stance.Bold)
                    > Equipment.ItemScore(Book.Item("spare-battery"), "hdd", Stance.Bold));
        Assert.True(Equipment.ItemScore(Book.Item("spare-battery"), "hdd", Stance.Careful)
                    > Equipment.ItemScore(Book.Item("catalog-notebook"), "hdd", Stance.Careful));

        var s = new AdventureState();
        s.Equipped[hdd.Key] = new[] { "", "", "spare-battery" };
        s.Inventory.Add("catalog-notebook");
        int swaps = 0;
        for (int i = 0; i < 100; i++)
            swaps += Equipment.AutoEquip(Book, s, new[] { hdd }, i % 2 == 0 ? Stance.Bold : Stance.Careful, out _, out _);
        Assert.InRange(swaps, 0, 1);
        // 外した方は、誰の役にも立たないので売れる
        var spare = s.Inventory.Single();
        Assert.False(Equipment.UsefulToAnyone(Book, s, new[] { hdd }, Book.Item(spare)!));
    }

    [Fact]
    public void In_town_alternating_stances_give_a_bounded_number_of_swaps_and_lines()
    {
        var sim = new Sim(seed: 13);
        for (int t = 0; t < 120; t++) sim.Tick(Profile.Idle);   // 一行が並ぶまで
        var engine = sim.Director.Adventure!;
        var a = sim.Adv;
        a.Equipped["hdd"] = new[] { "", "", "spare-battery" };
        a.Inventory.Clear();
        a.Inventory.Add("catalog-notebook");
        var world = new WorldState(new Dictionary<string, int>(), 0, 2, 1,
                                   new HashSet<string>(Rig.Desktop.Select(d => d.Id)), 1, 1);

        int swaps = 0, lines = 0;
        string[] Charm() => a.Equipped.OrderBy(k => k.Key).Select(k => k.Value[(int)EquipSlot.Charm]).ToArray();
        for (int i = 0; i < 100; i++)
        {
            a.Mode = AdventureMode.Town;
            a.Phase = TownPhase.Equip;
            a.Stance = i % 2 == 0 ? Stance.Bold : Stance.Careful;
            var before = Charm();
            string? line = engine.Step(world, "ja");
            if (!before.SequenceEqual(Charm())) swaps++;
            if (line is not null && (line.Contains("予備の電池") || line.Contains("目録の帳面"))) lines++;
        }
        Assert.InRange(swaps, 0, 2);
        Assert.InRange(lines, 0, 2);
    }

    [Fact]
    public void Starters_are_given_once_to_new_members()
    {
        var s = new AdventureState();
        var m = new Member("gpu#0", "gpu");
        Assert.True(Equipment.GiveStarters(Book, s, m));
        Assert.Equal("glowing-chip", s.Equipped[m.Key][(int)EquipSlot.Hand]);
        Assert.False(Equipment.GiveStarters(Book, s, m));
    }
}
