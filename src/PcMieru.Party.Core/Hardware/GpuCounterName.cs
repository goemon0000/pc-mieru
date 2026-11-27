using System.Globalization;

namespace PcMieru.Party.Hardware;

/// <summary>
/// 「GPU Engine」「GPU Adapter Memory」カウンタのインスタンス名を読む。
/// 名前は `pid_123_luid_0x00000000_0x0000E123_phys_0_eng_0_engtype_3D` の形。
///
/// **正規表現を使わない。** この読み取りは計測のたびにインスタンスの数（数百〜千を超える）だけ回る。
/// もとは Regex（Compiled）で読んでいたが、1 件ごとに Match と部分文字列と数への変換が出て、
/// 常駐の計測で一番数の多い割り当てになっていた。読み方は元の正規表現と同じ
/// （`luid_0x([0-9A-Fa-f]+)_0x([0-9A-Fa-f]+)` の最初の一致、`engtype_(\w+)$`）。試験で突き合わせてある。
/// </summary>
public static class GpuCounterName
{
    /// <summary>LUID の鍵。D3DKMT の側（GpuIdentity）と**必ず同じ形**にする（突き合わせに使うため）。</summary>
    public static string KeyOf(long high, ulong low) =>
        high.ToString("X", CultureInfo.InvariantCulture) + ":" + low.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>インスタンス名から LUID の鍵。読めない・桁あふれなら null。</summary>
    public static string? LuidKey(string instance)
    {
        const string Head = "luid_0x";
        for (int at = instance.IndexOf(Head, StringComparison.Ordinal); at >= 0;
             at = instance.IndexOf(Head, at + 1, StringComparison.Ordinal))
        {
            int p = at + Head.Length;
            int highEnd = HexRun(instance, p);
            if (highEnd == p) continue;
            if (string.CompareOrdinal(instance, highEnd, "_0x", 0, 3) != 0) continue;
            int lowStart = highEnd + 3;
            int lowEnd = HexRun(instance, lowStart);
            if (lowEnd == lowStart) continue;
            // 正規表現で一致したら、元はここで数に直していた（あふれたら null）。一致した後は次を探さない
            if (!TryHex(instance.AsSpan(p, highEnd - p), out ulong high)
                || !TryHex(instance.AsSpan(lowStart, lowEnd - lowStart), out ulong low))
                return null;
            return KeyOf(unchecked((long)high), low);
        }
        return null;
    }

    /// <summary>エンジンの種類（"3D" / "Copy" / "VideoDecode" …）。無ければ "other"。</summary>
    public static string EngineType(string instance)
    {
        const string Head = "engtype_";
        // `$` は末尾か、末尾の改行 1 つの手前で一致する
        int end = instance.Length > 0 && instance[^1] == '\n' ? instance.Length - 1 : instance.Length;
        for (int at = instance.IndexOf(Head, StringComparison.Ordinal); at >= 0;
             at = instance.IndexOf(Head, at + 1, StringComparison.Ordinal))
        {
            int p = at + Head.Length;
            if (p >= end) continue;
            bool word = true;
            for (int i = p; i < end && word; i++) word = IsWordChar(instance[i]);
            if (word) return instance[p..end];
        }
        return "other";
    }

    private static int HexRun(string s, int from)
    {
        int i = from;
        while (i < s.Length && char.IsAsciiHexDigit(s[i])) i++;
        return i;
    }

    /// <summary>16 進を 64 bit に。Convert.ToInt64(s, 16) と同じく、64 bit に収まらなければ偽。</summary>
    private static bool TryHex(ReadOnlySpan<char> digits, out ulong value)
    {
        value = 0;
        foreach (char c in digits)
        {
            if (value > ulong.MaxValue >> 4) return false;
            value = (value << 4) | (uint)HexValue(c);
        }
        return true;
    }

    private static int HexValue(char c) =>
        c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    /// <summary>正規表現の `\w` と同じ（文字・結合記号・数字・連結の記号）。</summary>
    private static bool IsWordChar(char c) => CharUnicodeInfo.GetUnicodeCategory(c) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation => true,
        _ => false,
    };
}
