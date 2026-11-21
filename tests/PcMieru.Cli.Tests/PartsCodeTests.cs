using System.Text;
using PcMieru.Checks;
using PcMieru.Checks.Build;

namespace PcMieru.Cli.Tests;

/// <summary>
/// <c>pcmi parts --code [-o file]</c>: the configuration code (PCB1) of the provider on stdout, its JSON in a file as the provider made it,
/// and what was left out or not read on stderr. Every test gives its own provider (a synthetic sample or a stub): this PC is never read.
/// </summary>
public class PartsCodeTests
{
    private sealed class StubProvider(BuildCodeResult result) : IBuildCodeProvider
    {
        public int Calls { get; private set; }

        public Task<BuildCodeResult> CreateAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class CancellingProvider : IBuildCodeProvider
    {
        public Task<BuildCodeResult> CreateAsync(CancellationToken ct) => throw new OperationCanceledException(ct);
    }

    // Test files go under DUO_TEST_TMP when the runner sets it (this machine: C:\DuoStudio\test-tmp), else the system temp folder.
    private static string TempFile()
    {
        var dir = Environment.GetEnvironmentVariable("DUO_TEST_TMP");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            dir = Path.GetTempPath();
        }

        return Path.Combine(dir, "pcmi-code-" + Guid.NewGuid().ToString("N") + ".json");
    }

    private static Task<BuildCodeResult> Sample(SampleVariant variant = SampleVariant.Plain) =>
        BuildCodeProviders.Sample(variant).CreateAsync(CancellationToken.None);

    private static BuildCodeResult EmptyConfig(params BuildUnread[] unread) =>
        new("PCB1.", "{}", new BuildConfig(), [], unread);

    // ---- stdout: the code ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_code_is_one_line_on_stdout_and_nothing_is_on_stderr_for_a_machine_read_in_full()
    {
        var sample = await Sample();

        var r = await CliRun.RunAsync(["parts", "--code"], buildCodeProvider: BuildCodeProviders.Sample());

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(sample.Code + "\n", r.Out);
        Assert.StartsWith("PCB1.", r.Out, StringComparison.Ordinal);
        Assert.Equal(string.Empty, r.Err);
        Assert.Single(r.OutLines);
    }

    [Fact]
    public async Task A_stub_that_gives_its_own_code_is_printed_as_it_is_with_one_newline()
    {
        var sample = await Sample();
        var stub = new StubProvider(sample with { Code = "PCB1.stub-code" });

        var r = await CliRun.RunAsync(["parts", "--code"], buildCodeProvider: stub);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("PCB1.stub-code\n", r.Out);
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task Nothing_is_read_before_the_command_runs_and_share_never_asks_the_provider_for_a_code()
    {
        var stub = new StubProvider(await Sample());

        var share = await CliRun.RunAsync(["parts", "--share"], buildCodeProvider: stub);

        Assert.Equal(0, share.ExitCode);
        Assert.Equal(0, stub.Calls);
        Assert.Contains("pcm-parts/1", share.Out, StringComparison.Ordinal);
    }

    // ---- -o: the provider's JSON, byte for byte -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_file_holds_the_provider_json_exactly_in_utf8_without_a_bom_and_without_a_newline()
    {
        var path = TempFile();
        try
        {
            var sample = await Sample();

            var r = await CliRun.RunAsync(["parts", "--code", "-o", path], buildCodeProvider: BuildCodeProviders.Sample());

            Assert.Equal(0, r.ExitCode);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(new UTF8Encoding(false).GetBytes(sample.Json), bytes);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "the file starts with a BOM");
            Assert.NotEqual((byte)'\n', bytes[^1]);
            Assert.NotEqual((byte)'\r', bytes[^1]);
            // The code is still the one line on stdout, whatever goes to the file.
            Assert.Equal(sample.Code + "\n", r.Out);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_file_keeps_characters_outside_ascii_as_utf8_bytes_and_ends_where_the_json_ends()
    {
        var path = TempFile();
        try
        {
            var json = "{\"v\":1,\"n\":\"日本語の部品 \u00e9\"} ";
            var stub = new StubProvider((await Sample()) with { Json = json });

            var r = await CliRun.RunAsync(["parts", "--code", "-o", path], buildCodeProvider: stub);

            Assert.Equal(0, r.ExitCode);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(new UTF8Encoding(false).GetBytes(json), bytes);
            Assert.Equal((byte)' ', bytes[^1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task An_existing_file_is_replaced_not_appended_to()
    {
        var path = TempFile();
        try
        {
            await File.WriteAllTextAsync(path, new string('x', 5000));
            var sample = await Sample();

            var r = await CliRun.RunAsync(["parts", "--code", "-o", path], buildCodeProvider: BuildCodeProviders.Sample());

            Assert.Equal(0, r.ExitCode);
            Assert.Equal(new UTF8Encoding(false).GetBytes(sample.Json), await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_folder_that_is_not_there_for_o_is_exit_1_with_the_command_name_no_file_and_nothing_on_stdout()
    {
        var folder = "pcmi-no-such-" + Guid.NewGuid().ToString("N");
        var missing = Path.Combine(Path.GetTempPath(), folder, "code.json");

        var r = await CliRun.RunAsync(["parts", "--code", "-o", missing], buildCodeProvider: BuildCodeProviders.Sample());

        Assert.Equal(1, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
        var line = Assert.Single(r.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains(line, OutputFailedLines("code.json", Language.En));
        Assert.DoesNotContain(folder, line, StringComparison.Ordinal);
        Assert.False(File.Exists(missing));
    }

    // ---- -o: the file first, then the code (DESIGN-A 8) ------------------------------------------------------------------------------

    /// <summary>The one line the command says when the file cannot be written (the reason is "denied" or "the write failed", by the kind of failure).</summary>
    private static string[] OutputFailedLines(string file, Language language) =>
        new[] { "cli.parts.output.denied", "cli.parts.output.error" }
            .Select(reason => "pcmi parts: " + CliWords.Of("cli.parts.outputFailed", language).Replace("{file}", file, StringComparison.Ordinal).Replace("{reason}", CliWords.Of(reason, language), StringComparison.Ordinal))
            .ToArray();

    /// <summary>A stdout that says what it finds the moment the code is written to it.</summary>
    private sealed class ProbeWriter(Action<string?> onWrite) : StringWriter
    {
        public override void Write(string? value)
        {
            onWrite(value);
            base.Write(value);
        }
    }

    [Fact]
    public async Task The_file_is_complete_on_disk_before_the_first_byte_of_the_code_reaches_stdout()
    {
        var path = TempFile();
        try
        {
            var sample = await Sample();
            var expected = new UTF8Encoding(false).GetBytes(sample.Json);
            var seen = new List<byte[]?>();
            var output = new ProbeWriter(_ => seen.Add(File.Exists(path) ? File.ReadAllBytes(path) : null)) { NewLine = "\n" };
            var env = new CliEnvironment(
                new StringReader(string.Empty),
                output,
                new StringWriter { NewLine = "\n" },
                o => CliRun.Services(o),
                BuildCodeProvider: BuildCodeProviders.Sample());

            var code = await CliApp.RunAsync(["parts", "--code", "-o", path], env, CancellationToken.None);

            Assert.Equal(0, code);
            Assert.Equal(sample.Code + "\n", output.ToString());
            var atWrite = Assert.Single(seen);
            Assert.NotNull(atWrite);
            Assert.Equal(expected, atWrite);
            Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(Language.En)]
    [InlineData(Language.Ja)]
    public async Task A_folder_for_o_gives_exit_1_nothing_on_stdout_and_one_line_that_names_the_file_only(Language language)
    {
        using var folder = new TempFolder(create: true);
        var name = Path.GetFileName(folder.Path);

        var r = await CliRun.RunAsync(["parts", "--code", "-o", folder.Path], language: language, buildCodeProvider: BuildCodeProviders.Sample());

        Assert.Equal(1, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
        var line = Assert.Single(r.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains(line, OutputFailedLines(name, language));
        Assert.DoesNotContain(Path.GetDirectoryName(folder.Path)!, line, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(folder.Path), "the folder is left alone");
    }

    [Fact]
    public async Task A_read_only_file_for_o_gives_exit_1_nothing_on_stdout_one_line_and_the_file_is_not_changed()
    {
        var path = TempFile();
        try
        {
            await File.WriteAllTextAsync(path, "old content");
            File.SetAttributes(path, FileAttributes.ReadOnly);

            var r = await CliRun.RunAsync(["parts", "--code", "-o", path], buildCodeProvider: BuildCodeProviders.Sample());

            Assert.Equal(1, r.ExitCode);
            Assert.Equal(string.Empty, r.Out);
            var line = Assert.Single(r.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains(line, OutputFailedLines(Path.GetFileName(path), Language.En));
            Assert.DoesNotContain(Path.GetDirectoryName(path)!, line, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("old content", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_notes_of_a_trimmed_machine_stay_on_stderr_when_the_file_cannot_be_written_and_no_code_is_printed()
    {
        using var folder = new TempFolder(create: true);

        var r = await CliRun.RunAsync(["parts", "--code", "-o", folder.Path], buildCodeProvider: BuildCodeProviders.Sample(SampleVariant.Trimmed));

        Assert.Equal(1, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
        var lines = r.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2, "the cuts are still said");
        Assert.All(lines, l => Assert.StartsWith("pcmi parts: ", l, StringComparison.Ordinal));
        Assert.Contains(lines[^1], OutputFailedLines(Path.GetFileName(folder.Path), Language.En));
    }

    // ---- argument errors ----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Language.En)]
    [InlineData(Language.Ja)]
    public async Task Share_and_code_together_are_exit_2_and_nothing_is_read(Language language)
    {
        var stub = new StubProvider(await Sample());

        var r = await CliRun.RunAsync(["parts", "--share", "--code"], language: language, buildCodeProvider: stub);

        Assert.Equal(2, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
        Assert.Contains(CliWords.Of("cli.parts.shareAndCode", language), r.Err, StringComparison.Ordinal);
        Assert.Contains("pcmi parts", r.Err, StringComparison.Ordinal);
        Assert.Equal(0, stub.Calls);
    }

    [Theory]
    [InlineData(Language.En)]
    [InlineData(Language.Ja)]
    public async Task Neither_flag_is_exit_2_and_the_message_names_both_options(Language language)
    {
        var stub = new StubProvider(await Sample());

        var r = await CliRun.RunAsync(["parts"], language: language, buildCodeProvider: stub);

        Assert.Equal(2, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
        var text = CliWords.Of("cli.parts.needShare", language);
        Assert.Contains(text, r.Err, StringComparison.Ordinal);
        Assert.Contains("--share", text, StringComparison.Ordinal);
        Assert.Contains("--code", text, StringComparison.Ordinal);
        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task Only_o_is_not_enough_either()
    {
        var r = await CliRun.RunAsync(["parts", "-o", "x.json"], buildCodeProvider: BuildCodeProviders.Sample());

        Assert.Equal(2, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
        Assert.False(File.Exists("x.json"));
    }

    [Fact]
    public async Task Code_with_include_usb_or_a_stray_argument_or_a_value_on_the_flag_is_exit_2()
    {
        foreach (var args in new[]
        {
            new[] { "parts", "--code", "--include-usb" },
            new[] { "parts", "--code", "extra" },
            new[] { "parts", "--code=1" },
            new[] { "parts", "--code", "-o" },
        })
        {
            var r = await CliRun.RunAsync(args, buildCodeProvider: BuildCodeProviders.Sample());

            Assert.True(r.ExitCode == 2, string.Join(' ', args) + " gave exit " + r.ExitCode);
            Assert.Equal(string.Empty, r.Out);
            Assert.StartsWith("pcmi parts: ", r.Err, StringComparison.Ordinal);
        }
    }

    // ---- stderr: what was left out and what was not read ---------------------------------------------------------------------------------

    private static string Number(int value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The lines the notes and the unread categories must give, made from the texts of the command's table, in the order stderr has them.</summary>
    private static List<string> ExpectedLines(BuildCodeResult result, Language language)
    {
        string Word(string category) => CliWords.Of("cli.parts.category." + category.Replace('-', '_'), language);

        var lines = new List<string>();
        var onePartAt = new Dictionary<string, int>();
        var onePartCount = new Dictionary<string, int>();
        foreach (var note in result.Notes)
        {
            if (note.Kind == BuildNoteKind.OnePartLeftOutForLength)
            {
                if (!onePartAt.ContainsKey(note.Category))
                {
                    onePartAt[note.Category] = lines.Count;
                    onePartCount[note.Category] = 0;
                    lines.Add(string.Empty);
                }

                onePartCount[note.Category]++;
                continue;
            }

            var key = note.Kind switch
            {
                BuildNoteKind.KindsLeftOut => "cli.parts.note.kinds_left_out",
                BuildNoteKind.CategoryLeftOutForLength => "cli.parts.note.category_left_out_for_length",
                BuildNoteKind.MemoryFirstKindOnly => "cli.parts.note.memory_first_kind_only",
                _ => throw new InvalidOperationException("a note kind the test does not know: " + note.Kind),
            };
            var max = note.Kind == BuildNoteKind.KindsLeftOut ? BuildCode.MaxPerCategory : BuildCode.MaxLength;
            lines.Add(CliWords.Fill(CliWords.Of(key, language), ("category", Word(note.Category)), ("count", Number(note.Count)), ("max", Number(max))));
        }

        foreach (var (category, at) in onePartAt)
        {
            var count = onePartCount[category];
            lines[at] = count == 1
                ? CliWords.Fill(CliWords.Of("cli.parts.note.one_part_left_out_for_length", language), ("category", Word(category)))
                : CliWords.Fill(CliWords.Of("cli.parts.note.one_part_left_out_for_length_many", language), ("category", Word(category)), ("count", Number(count)));
        }

        foreach (var unread in result.Unread)
        {
            lines.Add(CliWords.Fill(
                CliWords.Of("cli.parts.unread", language),
                ("category", Word(unread.Category)),
                ("reason", CliWords.Of("cli.parts.reason." + unread.Reason.ToString().ToLowerInvariant(), language))));
        }

        return lines;
    }

    [Theory]
    [InlineData(Language.En)]
    [InlineData(Language.Ja)]
    public async Task The_trimmed_machine_says_every_cut_and_every_unread_category_on_stderr_in_the_users_language(Language language)
    {
        var sample = await Sample(SampleVariant.Trimmed);

        var r = await CliRun.RunAsync(["parts", "--code"], language: language, buildCodeProvider: BuildCodeProviders.Sample(SampleVariant.Trimmed));

        Assert.Equal(0, r.ExitCode);
        // The three kinds of cut and the three reasons of the sample are all there to be said.
        Assert.Contains(sample.Notes, n => n.Kind == BuildNoteKind.KindsLeftOut);
        Assert.Contains(sample.Notes, n => n.Kind == BuildNoteKind.CategoryLeftOutForLength);
        Assert.Contains(sample.Notes, n => n.Kind == BuildNoteKind.MemoryFirstKindOnly);
        Assert.Equal(3, sample.Unread.Count);

        var expected = ExpectedLines(sample, language);
        Assert.Equal(expected.Select(l => "pcmi parts: " + l), r.Err.TrimEnd('\n').Split('\n'));
        foreach (var unread in sample.Unread)
        {
            Assert.Contains(
                CliWords.Of("cli.parts.reason." + unread.Reason.ToString().ToLowerInvariant(), language),
                r.Err,
                StringComparison.Ordinal);
        }

        // Stdout is still the code and nothing else.
        Assert.Equal(sample.Code + "\n", r.Out);
        Assert.DoesNotContain("pcmi parts", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_notes_come_before_the_unread_lines_and_every_line_has_the_command_name()
    {
        var sample = await Sample(SampleVariant.Trimmed);

        var r = await CliRun.RunAsync(["parts", "--code"], buildCodeProvider: BuildCodeProviders.Sample(SampleVariant.Trimmed));

        var lines = r.Err.TrimEnd('\n').Split('\n');
        Assert.Equal(sample.Notes.Count + sample.Unread.Count, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("pcmi parts: ", l, StringComparison.Ordinal));
        var firstUnread = Array.FindIndex(lines, l => l.Contains("not read (", StringComparison.Ordinal));
        Assert.True(firstUnread >= sample.Notes.Count, "an unread line came before a note");
    }

    [Fact]
    public async Task The_trimmed_file_is_the_trimmed_json_byte_for_byte_and_stderr_still_says_what_was_cut()
    {
        var path = TempFile();
        try
        {
            var sample = await Sample(SampleVariant.Trimmed);

            var r = await CliRun.RunAsync(["parts", "--code", "-o", path], buildCodeProvider: BuildCodeProviders.Sample(SampleVariant.Trimmed));

            Assert.Equal(0, r.ExitCode);
            Assert.Equal(new UTF8Encoding(false).GetBytes(sample.Json), await File.ReadAllBytesAsync(path));
            Assert.NotEqual(string.Empty, r.Err);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(Language.En)]
    [InlineData(Language.Ja)]
    public async Task The_same_note_for_one_part_again_and_again_is_one_line_with_the_count(Language language)
    {
        var sample = await Sample();
        var notes = new List<BuildNote>
        {
            new(BuildNoteKind.OnePartLeftOutForLength, "ram"),
            new(BuildNoteKind.OnePartLeftOutForLength, "ram"),
            new(BuildNoteKind.OnePartLeftOutForLength, "ram"),
            new(BuildNoteKind.OnePartLeftOutForLength, "gpu"),
            new(BuildNoteKind.KindsLeftOut, "ssd", 2),
        };
        var stub = new StubProvider(sample with { Notes = notes });

        var r = await CliRun.RunAsync(["parts", "--code"], language: language, buildCodeProvider: stub);

        Assert.Equal(0, r.ExitCode);
        var expected = ExpectedLines(sample with { Notes = notes }, language);
        Assert.Equal(3, expected.Count);
        Assert.Equal(expected.Select(l => "pcmi parts: " + l), r.Err.TrimEnd('\n').Split('\n'));
        Assert.Contains(CliWords.Fill(CliWords.Of("cli.parts.note.one_part_left_out_for_length_many", language), ("category", CliWords.Of("cli.parts.category.ram", language)), ("count", "3")), r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_note_kind_this_version_does_not_know_is_still_said_with_its_name()
    {
        var sample = await Sample();
        var future = (BuildNoteKind)999;
        var stub = new StubProvider(sample with { Notes = [new BuildNote(future, "cpu")] });

        var r = await CliRun.RunAsync(["parts", "--code"], buildCodeProvider: stub);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("999", r.Err, StringComparison.Ordinal);
        Assert.Contains("CPU", r.Err, StringComparison.Ordinal);
        Assert.DoesNotContain('[', r.Err);
        Assert.Equal(sample.Code + "\n", r.Out);
    }

    [Fact]
    public async Task A_category_this_version_has_no_word_for_is_said_by_its_own_name()
    {
        var sample = await Sample();
        var stub = new StubProvider(sample with { Unread = [new BuildUnread("sound-card", CheckReason.Denied)] });

        var r = await CliRun.RunAsync(["parts", "--code"], buildCodeProvider: stub);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("sound-card", r.Err, StringComparison.Ordinal);
        Assert.Contains(CliWords.En("cli.parts.reason.denied"), r.Err, StringComparison.Ordinal);
    }

    // ---- an empty configuration -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Language.En)]
    [InlineData(Language.Ja)]
    public async Task When_nothing_could_be_read_there_is_no_code_a_line_on_stderr_saying_why_and_exit_1(Language language)
    {
        var unread = new[] { new BuildUnread("cpu", CheckReason.Timeout), new BuildUnread("gpu", CheckReason.Denied) };
        var stub = new StubProvider(EmptyConfig(unread));

        var r = await CliRun.RunAsync(["parts", "--code"], language: language, buildCodeProvider: stub);

        Assert.Equal(1, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
        Assert.Contains("pcmi parts: " + CliWords.Of("cli.parts.emptyConfig", language), r.Err, StringComparison.Ordinal);
        // And the categories that were not read are still told.
        foreach (var line in ExpectedLines(EmptyConfig(unread), language))
        {
            Assert.Contains("pcmi parts: " + line, r.Err, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_empty_configuration_writes_no_file_and_does_not_leave_an_old_one_changed()
    {
        var path = TempFile();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var fresh = TempFile();

            var r = await CliRun.RunAsync(["parts", "--code", "-o", fresh], buildCodeProvider: new StubProvider(EmptyConfig()));
            var old = await CliRun.RunAsync(["parts", "--code", "-o", path], buildCodeProvider: new StubProvider(EmptyConfig()));

            Assert.Equal(1, r.ExitCode);
            Assert.Equal(1, old.ExitCode);
            Assert.False(File.Exists(fresh));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_provider_that_is_cancelled_is_exit_1_with_nothing_on_stdout()
    {
        var r = await CliRun.RunAsync(["parts", "--code"], buildCodeProvider: new CancellingProvider());

        Assert.Equal(1, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
    }

    // ---- the neighbour is unchanged ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Share_is_the_same_document_with_or_without_a_code_provider_given()
    {
        var plain = await CliRun.RunAsync(["parts", "--share"]);
        var withProvider = await CliRun.RunAsync(["parts", "--share"], buildCodeProvider: BuildCodeProviders.Sample());

        Assert.Equal(0, plain.ExitCode);
        Assert.Equal(plain.Out, withProvider.Out);
        Assert.Equal(plain.Err, withProvider.Err);
    }
}
