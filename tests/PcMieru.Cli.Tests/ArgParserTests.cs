namespace PcMieru.Cli.Tests;

public class ArgParserTests
{
    private static readonly OptionSpec[] Specs =
    [
        new("sort", TakesValue: true),
        new("top", TakesValue: true),
        new("json"),
        new("output", 'o', TakesValue: true),
    ];

    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("1s", 1000)]
    [InlineData("2m", 120_000)]
    [InlineData("1.5", 1500)]
    [InlineData("0", 0)]
    public void ParseDuration_accepts_ms_s_m_and_bare_seconds(string text, double milliseconds) =>
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), ParsedArgs.ParseDuration(text));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1e3")]
    [InlineData("s")]
    [InlineData("NaN")]
    [InlineData("1h")]
    public void ParseDuration_refuses_everything_else(string text) => Assert.Null(ParsedArgs.ParseDuration(text));

    [Fact]
    public void Options_come_in_long_inline_and_short_form()
    {
        var parsed = ParsedArgs.Parse(["--sort", "mem", "--top=5", "--json", "-o", "x.txt", "a", "b"], Specs);
        Assert.Equal("mem", parsed.Get("sort"));
        Assert.Equal("5", parsed.Get("top"));
        Assert.True(parsed.Has("json"));
        Assert.Equal("x.txt", parsed.Get("output"));
        Assert.Equal(["a", "b"], parsed.Positionals);
        Assert.False(parsed.Has("nothing"));
    }

    [Fact]
    public void Double_dash_ends_option_parsing()
    {
        var parsed = ParsedArgs.Parse(["--json", "--", "--sort", "-o"], Specs);
        Assert.True(parsed.Has("json"));
        Assert.False(parsed.Has("sort"));
        Assert.Equal(["--sort", "-o"], parsed.Positionals);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("-x")]
    [InlineData("--sort")]
    [InlineData("--json=1")]
    public void Bad_options_throw_ArgException(string arg) => Assert.Throws<ArgException>(() => ParsedArgs.Parse([arg], Specs));

    [Fact]
    public void A_value_may_start_with_a_dash()
    {
        var parsed = ParsedArgs.Parse(["--sort", "-1"], Specs);
        Assert.Equal("-1", parsed.Get("sort"));
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("1", 1)]
    [InlineData("100", 100)]
    public void GetInt_reads_whole_numbers_in_range(string text, int expected) =>
        Assert.Equal(expected, ParsedArgs.Parse(["--top", text], Specs).GetInt("top", 1, 100));

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("-3")]
    [InlineData("1.5")]
    [InlineData("x")]
    [InlineData("+3")]
    [InlineData("99999999999")]
    public void GetInt_refuses_out_of_range_and_non_numbers(string text) =>
        Assert.Throws<ArgException>(() => ParsedArgs.Parse(["--top", text], Specs).GetInt("top", 1, 100));

    [Fact]
    public void GetInt_without_the_option_is_null() => Assert.Null(ParsedArgs.Parse([], Specs).GetInt("top", 1, 100));

    [Theory]
    [InlineData("cpu", ProcessSort.Cpu)]
    [InlineData("MEM", ProcessSort.Mem)]
    [InlineData("name", ProcessSort.Name)]
    public void GetEnum_is_case_insensitive(string text, ProcessSort expected) =>
        Assert.Equal(expected, ParsedArgs.Parse(["--sort", text], Specs).GetEnum<ProcessSort>("sort"));

    [Theory]
    [InlineData("1")]
    [InlineData("9")]
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData("cpu,mem")]
    public void GetEnum_refuses_numbers_and_unknown_names(string text) =>
        Assert.Throws<ArgException>(() => ParsedArgs.Parse(["--sort", text], Specs).GetEnum<ProcessSort>("sort"));

    [Fact]
    public void GetDuration_enforces_its_range()
    {
        var parsed = ParsedArgs.Parse(["--top", "45s"], Specs);
        Assert.Throws<ArgException>(() => parsed.GetDuration("top", TimeSpan.Zero, TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(45), parsed.GetDuration("top", TimeSpan.Zero, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void RequirePositionals_counts_both_ways()
    {
        var none = ParsedArgs.Parse([], Specs);
        var two = ParsedArgs.Parse(["a", "b"], Specs);
        none.RequirePositionals(0, 0);
        Assert.Throws<ArgException>(() => none.RequirePositionals(1, 1));
        Assert.Throws<ArgException>(() => two.RequirePositionals(0, 1));
        two.RequirePositionals(1, 2);
    }

    [Theory]
    [InlineData(PcMieru.Wire.OperationCode.Ok, ExitCode.Success)]
    [InlineData(PcMieru.Wire.OperationCode.InvalidArguments, ExitCode.BadArguments)]
    [InlineData(PcMieru.Wire.OperationCode.NotFound, ExitCode.NotFound)]
    [InlineData(PcMieru.Wire.OperationCode.Denied, ExitCode.Denied)]
    [InlineData(PcMieru.Wire.OperationCode.Protected, ExitCode.Protected)]
    [InlineData(PcMieru.Wire.OperationCode.MultipleMatches, ExitCode.MultipleMatches)]
    public void Helper_operation_codes_map_to_the_documented_exit_codes(PcMieru.Wire.OperationCode code, ExitCode expected) =>
        Assert.Equal(expected, ExitCodes.From(code));

    [Fact]
    public void Exit_code_numbers_are_frozen()
    {
        Assert.Equal(
            [0, 1, 2, 3, 4, 5, 6],
            Enum.GetValues<ExitCode>().Select(c => (int)c).Order().ToArray());
    }
}
