using System.Diagnostics;
using System.Text.Json.Nodes;
using PcMieru.Core.Collection;

namespace PcMieru.Core.Tests.Collection;

/// <summary>
/// A list cannot carry a status, so SystemInfo carries one beside each (disks, adapters, GPUs). An empty list with a state
/// that is not Ok means "could not read", never "there are none". These tests pin the rule and each producer of the state.
/// </summary>
public sealed class ListStatusTests
{
    private const long GiB = 1L << 30;
    private static readonly GpuAdapter Real = new(0x166CA, "NVIDIA GeForce RTX 5070", 12 * GiB, 0x10DE, false);

    [Fact]
    public void A_state_nobody_filled_says_nothing_about_the_list()
    {
        Value<int> unset = default;

        Assert.True(ListStatus.IsUnset(unset));
        Assert.False(ListStatus.ExplainsEmptyList(0, unset));
    }

    [Theory]
    [InlineData(ValueStatus.NotCollected, true)]
    [InlineData(ValueStatus.Unsupported, true)]
    [InlineData(ValueStatus.Denied, true)]
    [InlineData(ValueStatus.Timeout, true)]
    [InlineData(ValueStatus.Error, true)]
    [InlineData(ValueStatus.Gone, true)]
    public void An_empty_list_whose_state_is_not_ok_is_explained_by_that_state(ValueStatus status, bool explained)
    {
        var state = Value.Missing<int>(status, "GetIfTable2", "why");

        Assert.False(ListStatus.IsUnset(state));
        Assert.Equal(explained, ListStatus.ExplainsEmptyList(0, state));
    }

    [Fact]
    public void An_empty_list_that_was_read_fine_is_none_and_a_list_with_entries_needs_no_explanation()
    {
        Assert.False(ListStatus.ExplainsEmptyList(0, Value.Ok(0, "GetIfTable2")));
        Assert.False(ListStatus.ExplainsEmptyList(2, Value.Failed<int>("GetIfTable2", "late failure")));
        Assert.False(ListStatus.ExplainsEmptyList(2, Value.Ok(2, "GetIfTable2")));
    }

    [Theory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    public void Describe_is_the_status_word_in_the_asked_language_then_the_reason(Language language)
    {
        var withReason = Value.Failed<int>("GetIfTable2", "GetIfTable2 returned 5");
        var withoutReason = Value.Missing<int>(ValueStatus.Unsupported, "x");

        Assert.Equal(Strings.Status(ValueStatus.Error, language) + ": GetIfTable2 returned 5", ListStatus.Describe(withReason, language));
        Assert.Equal(Strings.Status(ValueStatus.Unsupported, language), ListStatus.Describe(withoutReason, language));
        Assert.NotEqual(ListStatus.Describe(withReason, Language.Ja), ListStatus.Describe(withReason, Language.En));
    }

    [Fact]
    public void The_adapter_sampler_says_not_sampled_yet_before_its_first_tick()
    {
        var nic = new NicSampler();

        Assert.Equal(ValueStatus.NotCollected, nic.State.Status);
        Assert.True(ListStatus.ExplainsEmptyList(0, nic.State));
    }

    [Fact]
    public void An_unreadable_adapter_table_is_an_error_with_the_reason_and_an_empty_list()
    {
        var nic = new NicSampler();

        var nics = nic.Finish(tableRead: false, "GetIfTable2 returned 5", Stopwatch.GetTimestamp());

        Assert.Empty(nics);
        Assert.Equal(ValueStatus.Error, nic.State.Status);
        Assert.Equal("GetIfTable2", nic.State.Source);
        Assert.Contains("returned 5", nic.State.Detail, StringComparison.Ordinal);
        Assert.True(ListStatus.ExplainsEmptyList(nics.Count, nic.State));
    }

    [Fact]
    public void A_table_that_was_read_and_shows_no_adapter_is_ok_with_zero_not_an_error()
    {
        var nic = new NicSampler();

        var nics = nic.Finish(tableRead: true, string.Empty, Stopwatch.GetTimestamp());

        Assert.Empty(nics);
        Assert.True(nic.State.IsOk);
        Assert.Equal(0, nic.State.Data);
        Assert.False(ListStatus.ExplainsEmptyList(nics.Count, nic.State));
    }

    [Fact]
    public void The_state_recovers_by_itself_on_the_tick_after_a_failure()
    {
        var nic = new NicSampler();
        _ = nic.Finish(tableRead: false, "GetIfTable2 returned 5", Stopwatch.GetTimestamp());

        _ = nic.Finish(tableRead: true, string.Empty, Stopwatch.GetTimestamp());

        Assert.True(nic.State.IsOk);
    }

    [Trait("Category", "RealMachine")]
    [Fact]
    public void The_real_adapter_table_gives_an_ok_state_with_the_count_of_the_list()
    {
        var nic = new NicSampler();

        var nics = nic.Sample();

        Assert.True(nic.State.IsOk, ListStatus.Describe(nic.State));
        Assert.Equal(nics.Count, nic.State.Data);
    }

    [Fact]
    public void An_unavailable_pdh_reading_says_why_the_disk_list_is_empty()
    {
        var reading = PdhSystemSampler.Unavailable("PDH could not be opened");

        Assert.Empty(reading.Disks);
        Assert.Equal(ValueStatus.Error, reading.DisksState.Status);
        Assert.Equal("PDH PhysicalDisk", reading.DisksState.Source);
        Assert.Equal("PDH could not be opened", reading.DisksState.Detail);
        Assert.True(ListStatus.ExplainsEmptyList(reading.Disks.Count, reading.DisksState));
    }

    [Trait("Category", "RealMachine")]
    [Fact]
    public void The_real_pdh_reading_has_a_disk_state_that_agrees_with_the_list()
    {
        using var pdh = new PdhSystemSampler();

        var reading = pdh.Sample();

        Assert.False(ListStatus.IsUnset(reading.DisksState));
        if (reading.DisksState.IsOk)
        {
            Assert.Equal(reading.Disks.Count, reading.DisksState.Data);
        }
        else
        {
            Assert.Empty(reading.Disks);
        }
    }

    [Fact]
    public void Listed_adapters_make_the_gpu_list_ok_whatever_their_live_numbers_are()
    {
        var gpus = GpuSampler.Describe([Real], null, primed: false, "no baseline yet (first GPU sample)", ValueStatus.NotCollected);

        var state = GpuSampler.ListState(gpus, ValueStatus.NotCollected, "no baseline yet (first GPU sample)");

        Assert.True(state.IsOk);
        Assert.Equal(1, state.Data);
        Assert.Equal("DXGI", state.Source);
    }

    [Theory]
    [InlineData(ValueStatus.NotCollected)]
    [InlineData(ValueStatus.Unsupported)]
    [InlineData(ValueStatus.Error)]
    [InlineData(ValueStatus.Timeout)]
    public void An_empty_gpu_list_carries_the_status_and_the_reason_it_was_given(ValueStatus status)
    {
        var state = GpuSampler.ListState([], status, "the reason");

        Assert.Equal(status, state.Status);
        Assert.Equal("the reason", state.Detail);
        Assert.True(ListStatus.ExplainsEmptyList(0, state));
    }

    [Fact]
    public void A_gpu_sampler_that_was_never_switched_on_says_it_is_off_instead_of_no_gpu()
    {
        using var gpu = new GpuSampler();

        var reading = gpu.Sample(active: false);

        Assert.Empty(reading.Gpus);
        Assert.Equal(ValueStatus.NotCollected, reading.State.Status);
        Assert.Equal(GpuSampler.OffDetail, reading.State.Detail);
        Assert.True(ListStatus.ExplainsEmptyList(reading.Gpus.Count, reading.State));
    }

    [Fact]
    public void The_off_reading_keeps_the_known_adapters_ok()
    {
        var known = GpuSampler.Describe([Real], null, primed: false, "x", ValueStatus.NotCollected);

        var reading = GpuReading.Off(known);

        Assert.True(reading.State.IsOk);
        Assert.Equal(1, reading.State.Data);
    }

    [Trait("Category", "RealMachine")]
    [Fact]
    public void A_real_gpu_sample_never_lists_adapters_under_a_state_that_is_not_ok()
    {
        using var gpu = new GpuSampler();
        _ = gpu.Sample(active: true);
        Thread.Sleep(300);

        var reading = gpu.Sample(active: true);

        Assert.False(ListStatus.IsUnset(reading.State));
        if (reading.Gpus.Count > 0)
        {
            Assert.True(reading.State.IsOk, ListStatus.Describe(reading.State));
            Assert.Equal(reading.Gpus.Count, reading.State.Data);
        }
        else
        {
            Assert.True(reading.State.IsOk || ListStatus.ExplainsEmptyList(0, reading.State));
        }
    }

    [Fact]
    public void The_fake_snapshot_fills_all_three_states_with_the_count_of_each_list()
    {
        var system = FakeSnapshots.Create(DateTime.UtcNow, 1).System;

        Assert.Equal(system.Disks.Count, system.DisksState.Data);
        Assert.Equal(system.Nics.Count, system.NicsState.Data);
        Assert.Equal(system.Gpus.Count, system.GpusState.Data);
        Assert.All([system.DisksState, system.NicsState, system.GpusState], s => Assert.True(s.IsOk));
    }

    [Fact]
    public void The_states_are_written_in_camelCase_and_a_failed_one_has_its_reason_and_no_value()
    {
        var snapshot = FakeSnapshots.Create(DateTime.UtcNow, 1);
        var broken = snapshot with
        {
            System = snapshot.System with
            {
                Nics = [],
                NicsState = Value.Failed<int>("GetIfTable2", "GetIfTable2 returned 5"),
            },
        };

        var json = JsonNode.Parse(PcMieruJson.Serialize(broken))!["system"]!;

        Assert.Equal("error", (string?)json["nicsState"]!["status"]);
        Assert.Equal("GetIfTable2 returned 5", (string?)json["nicsState"]!["detail"]);
        Assert.Null(json["nicsState"]!["value"]);
        Assert.Equal("ok", (string?)json["disksState"]!["status"]);
        Assert.Equal(snapshot.System.Disks.Count, (int?)json["disksState"]!["value"]);
        Assert.Equal("ok", (string?)json["gpusState"]!["status"]);

        var back = PcMieruJson.Deserialize<Snapshot>(PcMieruJson.Serialize(broken))!;
        Assert.Equal(ValueStatus.Error, back.System.NicsState.Status);
        Assert.True(ListStatus.ExplainsEmptyList(back.System.Nics.Count, back.System.NicsState));
    }

    [Fact]
    public void A_json_from_before_the_states_existed_reads_as_unset_and_explains_nothing()
    {
        var node = JsonNode.Parse(PcMieruJson.Serialize(FakeSnapshots.Create(DateTime.UtcNow, 1)))!;
        var system = node["system"]!.AsObject();
        _ = system.Remove("disksState");
        _ = system.Remove("nicsState");
        _ = system.Remove("gpusState");

        var back = PcMieruJson.Deserialize<Snapshot>(node.ToJsonString())!;

        Assert.True(ListStatus.IsUnset(back.System.DisksState));
        Assert.True(ListStatus.IsUnset(back.System.NicsState));
        Assert.True(ListStatus.IsUnset(back.System.GpusState));
        Assert.False(ListStatus.ExplainsEmptyList(0, back.System.NicsState));
    }
}
