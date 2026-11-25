using System.IO;
using System.Windows;

namespace PcMieru.Party.Game;

/// <summary>
/// exe に埋め込んだ本文を読む。**WPF の資源に触るのはここだけ。**
/// 決まりの側（PcMieru.Party.Core）は文字列を受け取るだけにしてあり、
/// 画面の無い機械でも試験が回せる。
/// </summary>
public static class GameAssets
{
    public static Story LoadStory() => Story.Parse(ReadResource("story.json"));

    /// <summary>
    /// 冒険の本文。**読めなければ null（冒険を回さないだけで、物語とレベルは動く）。**
    /// 飾りの層が壊れていても、常駐物ごと落ちてはいけない。
    /// </summary>
    public static Adventure.AdventureBook? LoadAdventure()
    {
        try { return Adventure.AdventureBook.Parse(ReadResource("adventure.json")); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return null;
        }
    }

    private static string ReadResource(string name)
    {
        var uri = new Uri($"pack://application:,,,/Assets/{name}", UriKind.Absolute);
        using var s = Application.GetResourceStream(uri)?.Stream
                      ?? throw new FileNotFoundException($"{name} が見つかりません");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
