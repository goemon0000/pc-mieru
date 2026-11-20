namespace PcMieru.Checks.Tests;

/// <summary>The three file-name shapes of the history folder (DESIGN-A §4): committed, in flight, set aside.</summary>
public sealed class HistoryNamesTests
{
    private const string Stamp = "20261120T030004200Z";
    private const string Id = "89914ef957814cf7a4f86d58003ddf86";
    private const string Final = "memory-" + Stamp + "-" + Id + ".json";

    [Fact]
    public void A_committed_name_is_taken_apart_into_kind_time_and_uuid()
    {
        Assert.True(CheckHistoryNames.TryParse(Final, out var parsed));
        Assert.Equal(new HistoryFileName(Final, "memory", Stamp, Id), parsed);
    }

    [Theory]
    [InlineData("MEMORY-20261120T030004200Z-89914ef957814cf7a4f86d58003ddf86.json")]   // upper case kind
    [InlineData("memory-20261120t030004200z-89914ef957814cf7a4f86d58003ddf86.json")]   // lower case T and Z
    [InlineData("memory-20261120T030004200Z-89914EF957814CF7A4F86D58003DDF86.json")]   // upper case hex
    [InlineData("memory-20261120T030004200Z-89914ef957814cf7a4f86d58003ddf8.json")]    // 31 hex digits
    [InlineData("memory-20261120T030004200Z-89914ef957814cf7a4f86d58003ddf860.json")]  // 33 hex digits
    [InlineData("memory-20261120T030004200Z-89914ef9-5781-4cf7-a4f8-6d58003ddf86.json")] // dashed UUID
    [InlineData("memory-20261120T030004Z-89914ef957814cf7a4f86d58003ddf86.json")]      // no milliseconds
    [InlineData("memory-20261120T0300042000Z-89914ef957814cf7a4f86d58003ddf86.json")]  // four fraction digits
    [InlineData("memory-2026-11-20T03:00:04.200Z-89914ef957814cf7a4f86d58003ddf86.json")]
    [InlineData("-20261120T030004200Z-89914ef957814cf7a4f86d58003ddf86.json")]         // no kind
    [InlineData("1memory-20261120T030004200Z-89914ef957814cf7a4f86d58003ddf86.json")]  // kind must start with a letter
    [InlineData("memory-20261120T030004200Z-89914ef957814cf7a4f86d58003ddf86.JSON")]
    [InlineData("memory-20261120T030004200Z-89914ef957814cf7a4f86d58003ddf86.json ")]  // trailing space
    [InlineData("memory-20261120T030004200Z-89914ef957814cf7a4f86d58003ddf86.json\n")] // trailing newline (\z, not $)
    [InlineData(" memory-20261120T030004200Z-89914ef957814cf7a4f86d58003ddf86.json")]
    [InlineData("memory-20261120T030004200Z-89914ef957814cf7a4f86d58003ddf86.json.bad-20261121T040506789Z")]
    [InlineData("memory-20261120T030004200Z-89914ef957814cf7a4f86d58003ddf86.json.pcmtmp-4242-00000001.tmp")]
    [InlineData("memory-20261120T030004200Z-89914ef957814cf7a4f86d58003ddf86.json.tmp")]
    [InlineData("memory-20261120T030004200Z-89914ef957814cf7a4f86d58003ddf86.json.json")]
    [InlineData("notes.txt")]
    [InlineData("")]
    public void A_name_that_is_not_exactly_the_committed_shape_is_not_ours(string name)
    {
        Assert.False(CheckHistoryNames.TryParse(name, out _));
    }

    [Fact]
    public void A_kind_this_version_does_not_know_still_parses_as_a_name()
    {
        // The name alone says nothing about whether the kind is known; the reader decides (future kind: kept, not touched).
        Assert.True(CheckHistoryNames.TryParse("gpu-" + Stamp + "-" + Id + ".json", out var parsed));
        Assert.Equal("gpu", parsed.Kind);
    }

    [Theory]
    [InlineData(CheckKind.System, "system")]
    [InlineData(CheckKind.Cpu, "cpu")]
    [InlineData(CheckKind.Memory, "memory")]
    [InlineData(CheckKind.Storage, "storage")]
    public void The_committed_name_is_kind_utc_millisecond_stamp_and_uuid_without_dashes(CheckKind kind, string kindName)
    {
        var end = new DateTimeOffset(2026, 11, 20, 12, 0, 4, 200, TimeSpan.FromHours(9)); // 03:00:04.200 UTC
        var id = Guid.Parse("89914ef9-5781-4cf7-a4f8-6d58003ddf86");

        Assert.Equal($"{kindName}-20261120T030004200Z-89914ef957814cf7a4f86d58003ddf86.json", CheckHistoryNames.Format(kind, end, id));
    }

    [Fact]
    public void The_committed_name_cuts_the_time_to_whole_milliseconds_like_the_json_does()
    {
        var end = new DateTimeOffset(2026, 11, 20, 3, 0, 4, 200, TimeSpan.Zero).AddTicks(9999);
        Assert.Equal(Final, CheckHistoryNames.Format(CheckKind.Memory, end, Guid.Parse(Id)));
    }

    [Fact]
    public void The_tmp_name_is_the_final_name_plus_pid_and_nonce()
    {
        Assert.Equal(Final + ".pcmtmp-4242-0000abcd.tmp", CheckHistoryNames.TempName(Final, 4242, "0000abcd"));
        Assert.False(CheckHistoryNames.TryParse(CheckHistoryNames.TempName(Final, 4242, "0000abcd"), out _));
    }

    [Theory]
    [InlineData(Final + ".pcmtmp-4242-0000abcd.tmp", true)]
    [InlineData(Final + ".pcmtmp-1-00000000.tmp", true)]
    [InlineData(Final + ".pcmtmp-4294967295-ffffffff.tmp", true)]
    [InlineData("cpu-" + Stamp + "-" + Id + ".json.pcmtmp-4242-0000abcd.tmp", true)]
    [InlineData("system-" + Stamp + "-" + Id + ".json.pcmtmp-4242-0000abcd.tmp", true)]
    [InlineData("storage-" + Stamp + "-" + Id + ".json.pcmtmp-4242-0000abcd.tmp", true)]
    [InlineData("gpu-" + Stamp + "-" + Id + ".json.pcmtmp-4242-0000abcd.tmp", false)]   // a kind of a newer version
    [InlineData(Final + ".pcmtmp-4242-0000ABCD.tmp", false)]                           // upper case nonce
    [InlineData(Final + ".pcmtmp-4242-0000abc.tmp", false)]                            // 7 digits
    [InlineData(Final + ".pcmtmp-4242-0000abcde.tmp", false)]                          // 9 digits
    [InlineData(Final + ".pcmtmp-4242-0000abcd.tmp.bak", false)]
    [InlineData(Final + ".pcmtmp-4242-0000abcd.tmp\n", false)]
    [InlineData(Final + ".pcmtmp-x-0000abcd.tmp", false)]
    [InlineData(Final + ".pcmtmp--0000abcd.tmp", false)]
    [InlineData(Final + ".pcmtmp-12345678901-0000abcd.tmp", false)]                    // pid with 11 digits
    [InlineData(Final + ".tmp", false)]
    [InlineData(Final, false)]
    [InlineData(Final + ".bad-20261121T040506789Z", false)]
    [InlineData("foo.tmp", false)]
    [InlineData("memory-" + Stamp + ".json.pcmtmp-4242-0000abcd.tmp", false)]          // no UUID
    [InlineData("", false)]
    public void Only_the_exact_tmp_shape_of_a_known_kind_counts_as_a_write_in_flight(string name, bool own)
    {
        Assert.Equal(own, CheckHistoryNames.IsOwnTemp(name));
        Assert.Equal(own, CheckHistoryNames.TryParseTemp(name, out _));
    }

    [Theory]
    [InlineData("memory", "20261120T030004200Z", "89914ef957814cf7a4f86d58003ddf86", 4242, "0000abcd")]
    [InlineData("cpu", "20260101T000000000Z", "00000000000040008000000000000001", 1, "00000000")]
    [InlineData("storage", "20261231T235959999Z", "ffffffffffffffffffffffffffffffff", 2147483647, "ffffffff")]
    public void The_final_name_a_tmp_name_was_written_for_is_taken_out_of_it_and_the_pid_does_not_matter(string kind, string stamp, string id, int pid, string nonce)
    {
        var final = $"{kind}-{stamp}-{id}.json";

        Assert.True(CheckHistoryNames.TryParseTemp(CheckHistoryNames.TempName(final, pid, nonce), out var parsed));

        Assert.Equal(new HistoryFileName(final, kind, stamp, id), parsed);

        // The part taken out is itself a committed name.
        Assert.True(CheckHistoryNames.TryParse(parsed.Name, out var again));
        Assert.Equal(parsed, again);
    }

    [Fact]
    public void A_name_that_is_not_a_tmp_name_gives_no_final_name()
    {
        Assert.False(CheckHistoryNames.TryParseTemp(Final, out var none));
        Assert.Equal(default, none);
        Assert.False(CheckHistoryNames.TryParseTemp("gpu-" + Stamp + "-" + Id + ".json.pcmtmp-4242-0000abcd.tmp", out var future));
        Assert.Equal(default, future);
    }

    [Fact]
    public void A_set_aside_name_is_the_name_plus_bad_and_the_utc_stamp_and_then_a_counter()
    {
        var when = new DateTimeOffset(2026, 11, 21, 13, 5, 6, 789, TimeSpan.FromHours(9)); // 04:05:06.789 UTC
        Assert.Equal(Final + ".bad-20261121T040506789Z", CheckHistoryNames.BadName(Final, when, 0));
        Assert.Equal(Final + ".bad-20261121T040506789Z-1", CheckHistoryNames.BadName(Final, when, 1));
        Assert.Equal(Final + ".bad-20261121T040506789Z-12", CheckHistoryNames.BadName(Final, when, 12));

        // Neither a committed name nor an in-flight one: nothing lists, counts or deletes it.
        Assert.False(CheckHistoryNames.TryParse(CheckHistoryNames.BadName(Final, when, 0), out _));
        Assert.False(CheckHistoryNames.IsOwnTemp(CheckHistoryNames.BadName(Final, when, 0)));
    }
}
