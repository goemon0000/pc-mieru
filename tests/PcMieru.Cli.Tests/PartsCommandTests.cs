using System.Text.Json;
using PcMieru.Cli.Commands;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Cli.Tests;

/// <summary><c>pcmi parts --share</c>: the "pcm-parts/1" model-number document on stdout or in a file, built from the fake hardware.</summary>
public class PartsCommandTests
{
    /// <summary>Remembers which hardware categories the command asked for.</summary>
    private sealed class RecordingInsights(IReadOnlyInsights inner) : DelegatingInsights(inner)
    {
        private readonly object _lock = new();

        public List<HardwareCategory> Asked { get; } = [];

        public override Task<HardwareInfo> GetHardwareAsync(HardwareCategory category, CancellationToken ct = default)
        {
            lock (_lock)
            {
                Asked.Add(category);
            }

            return base.GetHardwareAsync(category, ct);
        }
    }

    // Test files go under DUO_TEST_TMP when the runner sets it (this machine: C:\DuoStudio\test-tmp), else the system temp folder.
    private static string TempFile()
    {
        var dir = Environment.GetEnvironmentVariable("DUO_TEST_TMP");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            dir = Path.GetTempPath();
        }

        return Path.Combine(dir, "pcmi-parts-" + Guid.NewGuid().ToString("N") + ".json");
    }

    [Fact]
    public async Task Share_prints_the_document_as_json_on_stdout()
    {
        var r = await CliRun.RunAsync(["parts", "--share"]);

        Assert.Equal(0, r.ExitCode);
        // stderr carries only the "sample data" notice of the Fake provider; the document is on stdout alone.
        Assert.DoesNotContain("pcm-parts", r.Err, StringComparison.Ordinal);
        var root = JsonDocument.Parse(r.Out).RootElement;
        Assert.Equal("pcm-parts/1", root.GetProperty("format").GetString());
        Assert.Equal("PCMieru " + VersionCommand.CurrentVersion, root.GetProperty("app").GetString());
        Assert.Equal("AMD Ryzen 9 7950X 16-Core Processor", root.GetProperty("cpu")[0].GetProperty("name").GetString());
        Assert.Equal("FAKE-X670 PRO", root.GetProperty("board").GetProperty("product").GetString());
        Assert.False(root.TryGetProperty("usb", out _));
        Assert.EndsWith("}\n", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_leaves_out_every_serial_address_and_name_of_the_fake_machine()
    {
        var r = await CliRun.RunAsync(["parts", "--share", "--include-usb"]);

        Assert.Equal(0, r.ExitCode);
        string[] banned =
        [
            "SN-FAKE-0000", "S6B0NG0R000000X", "ZFN00000", "00-00-5E", "192.0.2", "fe80", "luid_", "999.99", "Fake BIOS Vendor", "ROOT_HUB",
            "Speakers", "Microphone", "10.0.26300", "Windows 11", "Balanced", "NTFS", "Fake Peripherals", "USB Composite Device",
        ];
        var leaked = banned.Where(text => r.Out.Contains(text, StringComparison.Ordinal)).ToList();
        Assert.True(leaked.Count == 0, "texts in the output: " + string.Join(", ", leaked));
    }

    [Fact]
    public async Task Share_has_no_board_revision_and_lists_the_same_text_every_time()
    {
        var first = await CliRun.RunAsync(["parts", "--share", "--include-usb"]);
        var second = await CliRun.RunAsync(["parts", "--share", "--include-usb"]);

        Assert.DoesNotContain("revision", first.Out, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["manufacturer", "product"], JsonDocument.Parse(first.Out).RootElement.GetProperty("board").EnumerateObject().Select(p => p.Name));
        Assert.Equal(first.Out, second.Out);
    }

    [Fact]
    public async Task Include_usb_adds_the_vendor_and_product_ids_only()
    {
        var r = await CliRun.RunAsync(["parts", "--share", "--include-usb"]);

        var usb = JsonDocument.Parse(r.Out).RootElement.GetProperty("usb");
        var only = Assert.Single(usb.EnumerateArray());
        Assert.Equal(["vid", "pid"], only.EnumerateObject().Select(p => p.Name));
        Assert.Equal("046D", only.GetProperty("vid").GetString());
        Assert.Equal("C52B", only.GetProperty("pid").GetString());
    }

    [Fact]
    public async Task Output_writes_the_same_text_to_a_file_as_utf8_without_a_bom()
    {
        var path = TempFile();
        try
        {
            var toFile = await CliRun.RunAsync(["parts", "--share", "-o", path]);
            var toStdout = await CliRun.RunAsync(["parts", "--share"]);

            Assert.Equal(0, toFile.ExitCode);
            Assert.Equal(string.Empty, toFile.Out);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..], "the file is UTF-8 without a BOM");
            Assert.Equal(toStdout.Out, System.Text.Encoding.UTF8.GetString(bytes));
            Assert.Equal("pcm-parts/1", JsonDocument.Parse(bytes).RootElement.GetProperty("format").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_folder_for_o_is_exit_1_not_4_nothing_on_stdout_and_one_line_that_names_the_file_only()
    {
        var folder = TempFile();
        Directory.CreateDirectory(folder);
        try
        {
            var r = await CliRun.RunAsync(["parts", "--share", "-o", folder]);

            Assert.Equal(1, r.ExitCode);
            Assert.Equal(string.Empty, r.Out);
            var name = Path.GetFileName(folder);
            var expected = new[] { "cli.parts.output.denied", "cli.parts.output.error" }
                .Select(reason => "pcmi parts: " + CliWords.Of("cli.parts.shareOutputFailed", Language.En)
                    .Replace("{file}", name, StringComparison.Ordinal)
                    .Replace("{reason}", CliWords.Of(reason, Language.En), StringComparison.Ordinal))
                .ToArray();
            Assert.Contains(r.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => expected.Contains(line.TrimEnd('\r')));
            Assert.DoesNotContain(Path.GetDirectoryName(folder)!, r.Err, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    [Fact]
    public async Task The_long_option_name_writes_a_file_too()
    {
        var path = TempFile();
        try
        {
            var r = await CliRun.RunAsync(["parts", "--share", "--output", path]);
            Assert.Equal(0, r.ExitCode);
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Without_share_the_command_explains_and_exits_with_a_usage_error()
    {
        var en = await CliRun.RunAsync(["parts"]);
        Assert.Equal(2, en.ExitCode);
        Assert.Equal(string.Empty, en.Out);
        Assert.Contains("--share", en.Err, StringComparison.Ordinal);

        var ja = await CliRun.RunAsync(["parts", "--include-usb", "--lang", "ja"]);
        Assert.Equal(2, ja.ExitCode);
        Assert.Contains("--share", ja.Err, StringComparison.Ordinal);
        Assert.Contains("型番", ja.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stray_argument_is_a_usage_error_and_prints_nothing()
    {
        var r = await CliRun.RunAsync(["parts", "--share", "extra"]);
        Assert.Equal(2, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
    }

    [Fact]
    public async Task The_url_option_is_not_offered()
    {
        var r = await CliRun.RunAsync(["parts", "--share", "--url"]);
        Assert.Equal(2, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_the_model_categories_are_requested_from_the_hardware_service(bool includeUsb)
    {
        RecordingInsights? spy = null;
        string[] args = includeUsb ? ["parts", "--share", "--include-usb"] : ["parts", "--share"];
        var r = await CliRun.RunAsync(
            args,
            configure: registry =>
            {
                registry.TryGet<IReadOnlyInsights>(out var inner);
                spy = new RecordingInsights(inner!);
                registry.Set<IReadOnlyInsights>(spy);
            });

        Assert.Equal(0, r.ExitCode);
        Assert.NotNull(spy);
        Assert.DoesNotContain(HardwareCategory.All, spy.Asked);
        Assert.DoesNotContain(HardwareCategory.Network, spy.Asked);
        Assert.DoesNotContain(HardwareCategory.Audio, spy.Asked);
        Assert.DoesNotContain(HardwareCategory.Os, spy.Asked);
        Assert.Equal(includeUsb, spy.Asked.Contains(HardwareCategory.Usb));
        Assert.Contains(HardwareCategory.Cpu, spy.Asked);
        Assert.Contains(HardwareCategory.Display, spy.Asked);
    }

    [Fact]
    public async Task Help_lists_the_command_in_both_languages()
    {
        var en = await CliRun.RunAsync(["help"]);
        Assert.Contains("parts", en.Out, StringComparison.Ordinal);

        var ja = await CliRun.RunAsync(["help", "--lang", "ja"]);
        Assert.Contains("型番だけの一覧", ja.Out, StringComparison.Ordinal);
    }
}
