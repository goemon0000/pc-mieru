using System.Text.Json;
using System.Text.Json.Nodes;

namespace PcMieru.Core.Tests;

public class ValueAndKeyTests
{
    [Fact]
    public void Default_value_is_not_collected_never_a_measurement()
    {
        Value<long> v = default;
        Assert.Equal(ValueStatus.NotCollected, v.Status);
        Assert.False(v.IsOk);
        Assert.False(v.TryGet(out _));
        Assert.Equal(-1, v.GetOr(-1));
    }

    [Fact]
    public void Ok_value_serializes_with_value_status_and_source()
    {
        var node = PcMieruJson.ToNode(Value.Ok(42L, "test"))!.AsObject();
        Assert.Equal(42, node["value"]!.GetValue<long>());
        Assert.Equal("ok", node["status"]!.GetValue<string>());
        Assert.Equal("test", node["source"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(ValueStatus.NotCollected, "notCollected")]
    [InlineData(ValueStatus.Unsupported, "unsupported")]
    [InlineData(ValueStatus.Denied, "denied")]
    [InlineData(ValueStatus.Timeout, "timeout")]
    [InlineData(ValueStatus.Error, "error")]
    [InlineData(ValueStatus.Gone, "gone")]
    public void A_missing_value_never_carries_a_value_and_never_reads_as_zero(ValueStatus status, string wire)
    {
        var node = PcMieruJson.ToNode(Value.Missing<long>(status, "test", "why"))!.AsObject();
        Assert.False(node.ContainsKey("value"));
        Assert.Equal(wire, node["status"]!.GetValue<string>());
        Assert.Equal("why", node["detail"]!.GetValue<string>());

        var back = PcMieruJson.Deserialize<Value<long>>(node.ToJsonString());
        Assert.Equal(status, back.Status);
        Assert.False(back.TryGet(out _));
    }

    [Fact]
    public void Value_round_trips_for_string_bool_and_enum()
    {
        Assert.Equal("x", PcMieruJson.Deserialize<Value<string>>(PcMieruJson.Serialize(Value.Ok("x", "s"))).Data);
        Assert.True(PcMieruJson.Deserialize<Value<bool>>(PcMieruJson.Serialize(Value.Ok(true, "s"))).Data);
        var processState = PcMieruJson.Deserialize<Value<ProcessState>>(PcMieruJson.Serialize(Value.Ok(ProcessState.Suspended, "s")));
        Assert.Equal(ProcessState.Suspended, processState.Data);
    }

    [Fact]
    public void A_value_whose_json_says_ok_without_a_value_is_refused()
    {
        Assert.ThrowsAny<JsonException>(() => PcMieruJson.Deserialize<Value<long>>("""{"status":"ok","source":"x"}"""));
    }

    [Fact]
    public void Value_of_an_unregistered_type_fails_loudly_not_silently()
    {
        Assert.ThrowsAny<NotSupportedException>(() => PcMieruJson.Serialize(Value.Ok(Guid.NewGuid(), "x")));
    }

    [Fact]
    public void Process_key_parses_formats_and_serializes_as_a_string()
    {
        var key = new ProcessKey(1234, 133_800_000_000_000_000);
        Assert.Equal("1234:133800000000000000", key.ToString());
        Assert.True(ProcessKey.TryParse(key.ToString(), out var back));
        Assert.Equal(key, back);
        Assert.Equal("\"1234:133800000000000000\"", PcMieruJson.Serialize(key));
        Assert.Equal(key, PcMieruJson.Deserialize<ProcessKey>("\"1234:133800000000000000\""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234")]
    [InlineData(":5")]
    [InlineData("5:")]
    [InlineData("-1:5")]
    [InlineData("a:b")]
    [InlineData("1:2:3")]
    public void Process_key_rejects_bad_text(string text)
    {
        Assert.False(ProcessKey.TryParse(text, out _));
    }

    [Fact]
    public void Same_pid_with_a_different_start_time_is_a_different_process()
    {
        Assert.NotEqual(new ProcessKey(10, 1), new ProcessKey(10, 2));
    }

    [Fact]
    public void Serialized_json_is_valid_and_uses_camel_case_names()
    {
        var json = PcMieruJson.Serialize(Value.Ok(1L, "s"));
        Assert.Equal("value", JsonNode.Parse(json)!.AsObject().First().Key);
    }
}
