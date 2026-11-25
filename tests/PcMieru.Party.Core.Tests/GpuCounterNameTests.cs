using System.Text.RegularExpressions;
using PcMieru.Party.Hardware;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// GPU のカウンタのインスタンス名の読み方。**v0.7.3 までの正規表現と同じ結果になる**ことを確かめる
/// （読み方が変わると、GPU の使用率が別のカードに付く・0% になる）。
/// </summary>
public class GpuCounterNameTests
{
    // v0.7.3 の PdhSampler の読み方そのまま
    private static readonly Regex LuidRe = new(@"luid_0x([0-9A-Fa-f]+)_0x([0-9A-Fa-f]+)");
    private static readonly Regex EngTypeRe = new(@"engtype_(\w+)$");

    private static string? OldLuidKey(string instance)
    {
        var m = LuidRe.Match(instance);
        if (!m.Success) return null;
        try
        {
            long high = Convert.ToInt64(m.Groups[1].Value, 16);
            ulong low = Convert.ToUInt64(m.Groups[2].Value, 16);
            return $"{high:X}:{low:X}";
        }
        catch (OverflowException) { return null; }
        catch (FormatException) { return null; }
    }

    private static string OldEngineType(string instance)
    {
        var m = EngTypeRe.Match(instance);
        return m.Success ? m.Groups[1].Value : "other";
    }

    public static IEnumerable<object[]> Names() => new[]
    {
        "pid_1234_luid_0x00000000_0x0000E123_phys_0_eng_0_engtype_3D",
        "pid_4_luid_0x00000000_0x00018844_phys_0_eng_12_engtype_VideoDecode",
        "pid_99_luid_0x00000001_0x000177E8_phys_0_eng_3_engtype_Copy",
        "pid_99_luid_0x00000000_0x000177e8_phys_0_eng_3_engtype_Compute_0",   // 小文字・下線入り
        "luid_0x00000000_0x00016136_phys_0",                                   // GPU Adapter Memory の形（engtype 無し）
        "luid_0x00000000_0x00016136_phys_0_part_0",
        "pid_1_luid_0xFFFFFFFF_0xFFFFFFFF_phys_0_eng_0_engtype_3D",
        "pid_1_luid_0xFFFFFFFFFFFFFFFF_0x1_phys_0_eng_0_engtype_3D",           // 上位が負になる値
        "pid_1_luid_0x1FFFFFFFFFFFFFFFF_0x1_phys_0_eng_0_engtype_3D",          // 64 bit を超える → 読めない
        "pid_1_luid_0x0000000000000000000000001_0x2_engtype_3D",               // 先頭の 0 が長い
        "pid_1_luid_0x_0x2_engtype_3D",                                        // 上位の数字が無い
        "pid_1_luid_0xZZ_0x2_luid_0x3_0x4_engtype_3D",                         // 1 つ目が崩れていて 2 つ目で一致
        "pid_1_luid_0x3_0x_engtype_3D",                                        // 下位の数字が無い
        "pid_1_luid_0x3_0x4_engtype_",                                         // 種類が空
        "pid_1_luid_0x3_0x4_engtype_3D extra",                                 // 末尾に \w でない字
        "pid_1_luid_0x3_0x4_engtype_a_engtype_Copy",                           // 2 回出る
        "pid_1_luid_0x3_0x4_engtype_3D\n",                                     // 末尾の改行（$ の決まり）
        "pid_1_luid_0x3_0x4_engtype_ビデオ",                                   // \w は和文の字も含む
        "_Total",
        "",
    }.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Names))]
    public void Reads_the_same_as_the_old_regex(string instance)
    {
        Assert.Equal(OldLuidKey(instance), GpuCounterName.LuidKey(instance));
        Assert.Equal(OldEngineType(instance), GpuCounterName.EngineType(instance));
    }

    [Fact]
    public void Random_names_read_the_same_as_the_old_regex()
    {
        // 部品を混ぜて作った名前で突き合わせる（崩れた名前も混ぜる）
        var parts = new[] { "pid_", "12", "_luid_0x", "0x", "_0x", "00000000", "0000E123", "FFFF", "ffff", "g",
                            "_phys_0", "_eng_", "engtype_", "3D", "Copy", "_", " ", "\n", "-", "luid_" };
        var rnd = new Random(7);
        for (int n = 0; n < 20_000; n++)
        {
            var name = string.Concat(Enumerable.Range(0, rnd.Next(1, 10)).Select(_ => parts[rnd.Next(parts.Length)]));
            Assert.True(OldLuidKey(name) == GpuCounterName.LuidKey(name), $"luid: {name}");
            Assert.True(OldEngineType(name) == GpuCounterName.EngineType(name), $"engtype: {name}");
        }
    }

    [Fact]
    public void The_key_matches_the_d3dkmt_side()
    {
        // GpuIdentity（D3DKMT）は KeyOf(HighPart, LowPart) で鍵を作る。同じ形でないと名前と VRAM が付かない
        Assert.Equal("0:E123", GpuCounterName.KeyOf(0, 0xE123));
        Assert.Equal("FFFFFFFFFFFFFFFF:1", GpuCounterName.KeyOf(-1, 1));
        Assert.Equal($"{-5L:X}:{7UL:X}", GpuCounterName.KeyOf(-5, 7));
    }
}
