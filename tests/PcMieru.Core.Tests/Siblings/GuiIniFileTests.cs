using PcMieru.Core.Siblings;

namespace PcMieru.Core.Tests.Siblings;

/// <summary>
/// DESIGN-C 3 item 4: the Parts Life window reads ONE key of gui.ini and never writes it. Every test here uses a temp file of its own: the real
/// %LOCALAPPDATA%\PCMieru\gui.ini is never opened.
/// </summary>
public sealed class GuiIniFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pcmieru-guiini-" + Guid.NewGuid().ToString("N"));

    public GuiIniFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string text)
    {
        var path = Path.Combine(_dir, "gui.ini");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Ja_and_en_are_read_from_the_language_line_whatever_else_the_file_holds()
    {
        Assert.Equal(Language.Ja, GuiIniFile.ReadLanguage(Write("language=ja\ntheme=dark\ninterval=1000\n")));
        Assert.Equal(Language.En, GuiIniFile.ReadLanguage(Write("theme=dark\r\nlanguage=en\r\ninterval=1000\r\n")));
        Assert.Equal(Language.En, GuiIniFile.ReadLanguage(Write("  language = EN  \n")));
    }

    [Fact]
    public void The_last_valid_language_line_wins_and_a_bad_one_after_it_does_not_unseat_it()
    {
        Assert.Equal(Language.En, GuiIniFile.ReadLanguage(Write("language=ja\nlanguage=en\n")));
        Assert.Equal(Language.Ja, GuiIniFile.ReadLanguage(Write("language=ja\nlanguage=klingon\n")));
    }

    [Fact]
    public void A_missing_file_changes_nothing()
    {
        Assert.Null(GuiIniFile.ReadLanguage(Path.Combine(_dir, "no-such-gui.ini")));
        Assert.Null(GuiIniFile.ReadLanguage(Path.Combine(_dir, "no-such-folder", "gui.ini")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("language=\n")]
    [InlineData("language=fr\n")]
    [InlineData("language=0\n")]
    [InlineData("language\n")]
    [InlineData("=ja\n")]
    [InlineData("xlanguage=ja\n")]
    [InlineData("language2=ja\n")]
    [InlineData("theme=ja\n")]
    [InlineData("\0\0\u0001garbage ÿþ=== \n\n")]
    public void A_file_with_no_readable_language_changes_nothing(string text)
    {
        Assert.Null(GuiIniFile.ReadLanguage(Write(text)));
    }

    [Fact]
    public void A_folder_where_the_file_should_be_is_unreadable_and_changes_nothing()
    {
        Assert.Null(GuiIniFile.ReadLanguage(_dir));
    }

    [Fact]
    public void Reading_does_not_write_or_touch_the_file()
    {
        var path = Write("language=ja\ntheme=dark\n");
        var stamp = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        _ = GuiIniFile.ReadLanguage(path);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_file_the_gui_has_open_for_writing_can_still_be_read()
    {
        var path = Write("language=en\n");
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        Assert.Equal(Language.En, GuiIniFile.ReadLanguage(path));
    }

    [Fact]
    public void The_default_path_is_the_gui_ini_of_the_local_app_data_pcmieru_folder()
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMieru", "gui.ini"),
            GuiIniFile.DefaultPath);
    }
}
