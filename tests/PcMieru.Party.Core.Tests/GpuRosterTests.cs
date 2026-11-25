using PcMieru.Party.Hardware;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// 盤に立てる GPU。**顔ぶれはアダプタの一覧で決まり、その秒のカウンタでは揺れない。**
/// 以前は Basic Render（WARP）や RTX 5070 の別名が動いた秒だけ 4 枚目が立ち、冒険がそれに装備を配って
/// 保存に gpu#3 が残った。
/// </summary>
public class GpuRosterTests
{
    private const ulong GB = 1UL << 30;

    /// <summary>開発機で D3DKMT が返した 5 件（--probe の出力から。名前は詰めた後の形、VRAM は GB 表示から戻した値）。</summary>
    private static readonly GpuAdapterInfo[] DevMachine =
    {
        new("0:24F84", "", 12_520_390_656, null, false),                       // RTX 5070 の別名（仮想ディスプレイ）
        new("0:18844", "Radeon Graphics", 508_559_360, 17, false),
        new("0:177E8", "GeForce RTX 3080 Ti", 12_670_992_384, 9, false),
        new("0:16136", "GeForce RTX 5070", 12_520_390_656, 1, false),
        new("0:177A6", "", 0, null, true),                                      // Microsoft Basic Render Driver
    };

    [Fact]
    public void The_dev_machine_gets_exactly_its_three_cards_in_vram_order()
    {
        var real = GpuRoster.Real(DevMachine);
        Assert.Equal(new[] { "0:177E8", "0:16136", "0:18844" }, real.Select(a => a.LuidKey).ToArray());
    }

    [Fact]
    public void A_busy_alias_or_warp_never_adds_a_fourth_gpu()
    {
        var real = GpuRoster.Real(DevMachine);
        var quiet = GpuRoster.Compose(real, true,
            new Dictionary<string, double> { ["0:16136"] = 0.03 },
            new Dictionary<string, double> { ["0:16136"] = 9e9, ["0:177E8"] = 1e10 });
        // 何かが WARP で描き、仮想ディスプレイにも映した秒。知らない LUID も混ざる
        var busy = GpuRoster.Compose(real, true,
            new Dictionary<string, double> { ["0:177A6"] = 0.4, ["0:24F84"] = 0.2, ["0:16136"] = 0.5, ["0:BEEF"] = 0.9 },
            new Dictionary<string, double> { ["0:24F84"] = 3e9 });

        Assert.Equal(quiet.Select(g => g.Key), busy.Select(g => g.Key));
        Assert.Equal(3, busy.Count);
        Assert.Equal(new[] { "GeForce RTX 3080 Ti", "GeForce RTX 5070", "Radeon Graphics" }, busy.Select(g => g.Name).ToArray());
        Assert.Equal(0.5, busy[1].Utilization);
        // カウンタに出ていない秒でも、その GPU は 0% で立っている（体の数が秒ごとに変わらない）
        Assert.Equal(0, busy[0].Utilization);
        Assert.Equal(12_670_992_384UL, busy[0].VramTotalBytes);
    }

    [Fact]
    public void Basic_render_is_dropped_even_if_the_software_flag_could_not_be_read()
    {
        var noFlag = DevMachine.Select(a => a with { Software = false }).ToArray();
        Assert.Equal(3, GpuRoster.Real(noFlag).Count);
    }

    [Fact]
    public void Two_identical_real_cards_are_both_kept_in_bus_order()
    {
        var real = GpuRoster.Real(new GpuAdapterInfo[]
        {
            new("0:9", "GeForce RTX 3080 Ti", 12 * GB, 9, false),
            new("0:1", "GeForce RTX 3080 Ti", 12 * GB, 1, false),
        });
        Assert.Equal(new[] { "0:1", "0:9" }, real.Select(a => a.LuidKey).ToArray());
    }

    [Fact]
    public void An_alias_named_like_its_card_is_dropped_but_a_different_virtual_gpu_is_kept()
    {
        var real = GpuRoster.Real(new GpuAdapterInfo[]
        {
            new("0:1", "GeForce RTX 5070", 12 * GB, 1, false),
            new("0:2", "GeForce RTX 5070", 12 * GB, null, false),          // DXGI 流の別名（名前あり）
            new("0:3", "Remote GPU", 8 * GB, null, false),                  // 番地は無いが、別物
        });
        Assert.Equal(new[] { "0:1", "0:3" }, real.Select(a => a.LuidKey).ToArray());
    }

    [Fact]
    public void Without_an_adapter_list_only_counters_that_are_busy_show_up_as_before()
    {
        var rows = GpuRoster.Compose(Array.Empty<GpuAdapterInfo>(), false,
            new Dictionary<string, double> { ["0:B"] = 0.2, ["0:A"] = 0.0, ["0:C"] = 1.7 },
            new Dictionary<string, double> { ["0:D"] = 1e9 });
        Assert.Equal(new[] { "0:B", "0:C" }, rows.Select(r => r.Key).ToArray());
        Assert.All(rows, r => Assert.Equal("GPU", r.Name));
        Assert.Equal(1.0, rows[1].Utilization);
    }

    [Fact]
    public void An_adapter_list_with_only_software_shows_no_gpu()
    {
        var real = GpuRoster.Real(new[] { new GpuAdapterInfo("0:1", "", 0, null, true) });
        var rows = GpuRoster.Compose(real, true, new Dictionary<string, double> { ["0:1"] = 0.5 }, new Dictionary<string, double>());
        Assert.Empty(rows);
    }
}
