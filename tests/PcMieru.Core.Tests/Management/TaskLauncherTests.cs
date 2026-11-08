using System.Diagnostics;
using PcMieru.Core.Management;
using PcMieru.Wire;

namespace PcMieru.Core.Tests.Management;

internal sealed class FakeExecutor(Func<string, string, bool, int>? script = null) : IShellExecutor
{
    public List<(string File, string Arguments, bool Elevated)> Calls { get; } = [];

    public int Start(string file, string arguments, bool elevated)
    {
        Calls.Add((file, arguments, elevated));
        return script?.Invoke(file, arguments, elevated) ?? 0;
    }
}

public class TaskLauncherTests
{
    private static Func<string, bool> Exists(params string[] paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    [Fact]
    public void A_quoted_program_is_taken_whole_and_the_rest_is_its_arguments()
    {
        Assert.Equal((@"C:\Program Files\x\y.exe", "/a /b"), TaskLauncher.Split("\"C:\\Program Files\\x\\y.exe\" /a /b", Exists()));
        Assert.Equal((@"C:\a b.exe", ""), TaskLauncher.Split("\"C:\\a b.exe\"", Exists()));
        Assert.Equal((@"C:\broken path", ""), TaskLauncher.Split("\"C:\\broken path", Exists()));
        Assert.Equal(("x.exe", "\"quoted arg\" two"), TaskLauncher.Split("\"x.exe\" \"quoted arg\" two", Exists()));
    }

    [Fact]
    public void An_unquoted_text_that_is_itself_an_existing_path_is_not_split()
    {
        Assert.Equal((@"C:\Program Files\Tool\tool.exe", ""), TaskLauncher.Split(@"C:\Program Files\Tool\tool.exe", Exists(@"C:\Program Files\Tool\tool.exe")));
        Assert.Equal((@"C:\My Documents", ""), TaskLauncher.Split(@"C:\My Documents", Exists(@"C:\My Documents")));
    }

    [Fact]
    public void An_unquoted_program_with_spaces_is_found_by_the_shortest_existing_head()
    {
        var exists = Exists(@"C:\Program Files\x\y.exe");
        Assert.Equal((@"C:\Program Files\x\y.exe", "/a"), TaskLauncher.Split(@"C:\Program Files\x\y.exe /a", exists));

        // Also when the file is named without ".exe" in the text.
        var withExe = Exists(@"C:\Program Files\x\y.exe");
        Assert.Equal((@"C:\Program Files\x\y", "/a"), TaskLauncher.Split(@"C:\Program Files\x\y /a", withExe));

        // The SHORTEST head wins, so "C:\Program" cannot swallow the rest if it were a file.
        var shortest = Exists(@"C:\Program", @"C:\Program Files\x\y.exe");
        Assert.Equal((@"C:\Program", @"Files\x\y.exe /a"), TaskLauncher.Split(@"C:\Program Files\x\y.exe /a", shortest));
    }

    [Theory]
    [InlineData("notepad", "notepad", "")]
    [InlineData("notepad  C:\\a.txt", "notepad", "C:\\a.txt")]
    [InlineData("  cmd /c dir  ", "cmd", "/c dir")]
    [InlineData("https://example.com/x y", "https://example.com/x", "y")]
    [InlineData("ms-settings:", "ms-settings:", "")]
    public void With_nothing_found_the_first_word_is_the_program_for_the_shell_to_find(string text, string file, string arguments) =>
        Assert.Equal((file, arguments), TaskLauncher.Split(text, Exists()));

    [Fact]
    public void An_empty_text_is_refused_without_starting_anything()
    {
        Strings.Register(ManagementStrings.All);
        var executor = new FakeExecutor();
        var launcher = new TaskLauncher(executor, Exists());

        foreach (var text in new[] { "", "   ", "\t\r\n", null! })
        {
            var result = launcher.Run(text, elevated: false);
            Assert.Equal(OperationCode.InvalidArguments, result.Code);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
        }

        Assert.Empty(executor.Calls);
    }

    [Fact]
    public void A_start_reports_the_command_and_passes_the_elevated_flag_on()
    {
        Strings.Register(ManagementStrings.All);
        var executor = new FakeExecutor();
        var launcher = new TaskLauncher(executor, Exists());

        var plain = launcher.Run("notepad C:\\a.txt", elevated: false);
        var admin = launcher.Run("notepad", elevated: true);

        Assert.Equal(OperationCode.Ok, plain.Code);
        Assert.Contains("notepad", plain.Message);
        Assert.Equal(OperationCode.Ok, admin.Code);
        Assert.Equal([("notepad", "C:\\a.txt", false), ("notepad", "", true)], executor.Calls);
    }

    [Fact]
    public void Environment_variables_are_expanded_like_the_Run_box_does()
    {
        var executor = new FakeExecutor();
        new TaskLauncher(executor, Exists()).Run("%SystemRoot%\\notepad.exe %TEMP%", elevated: false);

        var call = Assert.Single(executor.Calls);
        Assert.Equal(Environment.ExpandEnvironmentVariables("%SystemRoot%\\notepad.exe"), call.File);
        Assert.Equal(Environment.ExpandEnvironmentVariables("%TEMP%"), call.Arguments);
    }

    [Fact]
    public void A_long_command_is_shortened_in_the_message()
    {
        Strings.Register(ManagementStrings.All);
        var text = "prog " + new string('x', 400);
        var result = new TaskLauncher(new FakeExecutor(), Exists()).Run(text, elevated: false);
        Assert.Equal(OperationCode.Ok, result.Code);
        Assert.DoesNotContain(text, result.Message);
        Assert.Contains("...", result.Message);
    }

    [Theory]
    [InlineData(2, OperationCode.NotFound)]
    [InlineData(3, OperationCode.NotFound)]
    [InlineData(5, OperationCode.Denied)]
    [InlineData(87, OperationCode.InvalidArguments)]
    [InlineData(1223, OperationCode.Cancelled)]
    [InlineData(193, OperationCode.Error)]
    [InlineData(1155, OperationCode.Error)]
    public void The_win32_error_decides_the_code(int error, OperationCode expected)
    {
        Strings.Register(ManagementStrings.All);
        var launcher = new TaskLauncher(new FakeExecutor((_, _, _) => error), Exists());

        var result = launcher.Run("something", elevated: false);

        Assert.Equal(expected, result.Code);
        if (expected != OperationCode.Cancelled)
        {
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
        }

        if (expected is OperationCode.Error or OperationCode.Denied or OperationCode.InvalidArguments)
        {
            Assert.Contains(error.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Message);
        }
    }

    [Fact]
    public void A_program_that_does_not_exist_is_not_found_through_the_real_shell_and_nothing_is_run()
    {
        Strings.Register(ManagementStrings.All);
        var result = new TaskLauncher().Run(Path.Combine(Path.GetTempPath(), "pcmieru-no-such-program-" + Guid.NewGuid().ToString("N") + ".exe"), elevated: false);
        Assert.Equal(OperationCode.NotFound, result.Code);
    }

    [Fact]
    public void One_real_hidden_launch_of_a_short_ping_through_the_real_executor()
    {
        Strings.Register(ManagementStrings.All);
        var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        var before = Process.GetProcessesByName("ping").Select(p => p.Id).ToHashSet();

        // ShellExecute with a console program shows a window for the 2 seconds ping runs: kept as short as the program allows.
        var result = new TaskLauncher().Run($"\"{ping}\" -n 2 127.0.0.1", elevated: false);

        Assert.Equal(OperationCode.Ok, result.Code);
        var started = Process.GetProcessesByName("ping").Where(p => !before.Contains(p.Id)).ToArray();
        foreach (var process in started)
        {
            // Only the process we just caused (new pid, a ping we asked for) is waited for, never killed.
            process.WaitForExit(10_000);
            process.Dispose();
        }
    }
}
