namespace PcMieru.Checks.Tests.Run;

/// <summary>What a request may ask for (DESIGN-A §2): a duration only for CPU and memory, in three lengths; elevation only for storage.</summary>
public sealed class CheckRequestTests
{
    [Fact]
    public void The_allowed_durations_are_thirty_one_hundred_twenty_and_three_hundred_seconds()
    {
        Assert.Equal([30.0, 120.0, 300.0], CheckRequest.AllowedDurations.Select(static d => d.TotalSeconds));
    }

    [Theory]
    [InlineData(CheckKind.System)]
    [InlineData(CheckKind.Cpu)]
    [InlineData(CheckKind.Memory)]
    [InlineData(CheckKind.Storage)]
    public void A_request_with_only_a_kind_is_valid_for_every_kind(CheckKind kind)
    {
        var request = new CheckRequest(kind);

        Assert.Null(request.Problem);
        request.EnsureValid();
        Assert.Null(request.Duration);
        Assert.False(request.AllowElevation); // off unless the caller asks
    }

    [Theory]
    [InlineData(CheckKind.Cpu, 30)]
    [InlineData(CheckKind.Cpu, 120)]
    [InlineData(CheckKind.Cpu, 300)]
    [InlineData(CheckKind.Memory, 30)]
    [InlineData(CheckKind.Memory, 120)]
    [InlineData(CheckKind.Memory, 300)]
    public void A_cpu_or_memory_request_may_have_one_of_the_three_durations(CheckKind kind, int seconds)
    {
        var request = new CheckRequest(kind, TimeSpan.FromSeconds(seconds));

        Assert.Null(request.Problem);
        request.EnsureValid();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    [InlineData(31)]
    [InlineData(60)]
    [InlineData(299)]
    [InlineData(301)]
    [InlineData(3600)]
    [InlineData(-30)]
    public void Any_other_duration_is_a_problem(int seconds)
    {
        foreach (var kind in new[] { CheckKind.Cpu, CheckKind.Memory })
        {
            var request = new CheckRequest(kind, TimeSpan.FromSeconds(seconds));

            Assert.NotNull(request.Problem);
            Assert.Throws<ArgumentException>(request.EnsureValid);
        }
    }

    [Fact]
    public void A_duration_a_fraction_off_is_not_one_of_the_three()
    {
        Assert.NotNull(new CheckRequest(CheckKind.Cpu, TimeSpan.FromSeconds(30).Add(TimeSpan.FromMilliseconds(1))).Problem);
    }

    [Theory]
    [InlineData(CheckKind.System)]
    [InlineData(CheckKind.Storage)]
    public void A_duration_belongs_to_cpu_and_memory_only(CheckKind kind)
    {
        var request = new CheckRequest(kind, TimeSpan.FromSeconds(30));

        Assert.NotNull(request.Problem);
        Assert.Throws<ArgumentException>(request.EnsureValid);
    }

    [Fact]
    public void Elevation_is_for_the_storage_check_only()
    {
        Assert.Null(new CheckRequest(CheckKind.Storage, AllowElevation: true).Problem);
        new CheckRequest(CheckKind.Storage, AllowElevation: true).EnsureValid();

        foreach (var kind in new[] { CheckKind.System, CheckKind.Cpu, CheckKind.Memory })
        {
            var request = new CheckRequest(kind, AllowElevation: true);

            Assert.NotNull(request.Problem);
            Assert.Throws<ArgumentException>(request.EnsureValid);
        }
    }

    [Fact]
    public void A_kind_that_is_not_in_the_enum_is_a_problem()
    {
        var request = new CheckRequest((CheckKind)99);

        Assert.NotNull(request.Problem);
        Assert.Throws<ArgumentException>(request.EnsureValid);
    }

    [Fact]
    public void Requests_with_the_same_values_are_equal()
    {
        Assert.Equal(new CheckRequest(CheckKind.Cpu, TimeSpan.FromSeconds(30)), new CheckRequest(CheckKind.Cpu, TimeSpan.FromSeconds(30)));
        Assert.NotEqual(new CheckRequest(CheckKind.Storage), new CheckRequest(CheckKind.Storage, AllowElevation: true));
    }
}
