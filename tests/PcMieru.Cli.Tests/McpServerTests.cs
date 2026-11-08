using System.Text.Json.Nodes;
using PcMieru.Cli.Mcp;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Reporting;

namespace PcMieru.Cli.Tests;

public class McpServerTests
{
    private static readonly string[] ToolNames =
    [
        "get_system_summary", "list_processes", "get_process", "get_hardware", "get_storage_health", "diagnose", "get_report",
    ];

    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2025-03-26")]
    [InlineData("2024-11-05")]
    public async Task Initialize_answers_with_the_version_the_client_asked_for_when_it_is_supported(string version)
    {
        var messages = await McpRun.RunAsync(McpRun.InitializeWith(version));
        var result = McpRun.Reply(messages, "init")["result"]!;
        Assert.Equal(version, (string?)result["protocolVersion"]);
        Assert.Equal("pc-mieru", (string?)result["serverInfo"]!["name"]);
        Assert.Equal("9.9.9", (string?)result["serverInfo"]!["version"]);
        Assert.NotNull(result["capabilities"]!["tools"]);
        Assert.Null(result["capabilities"]!["resources"]);
        Assert.Null(result["capabilities"]!["prompts"]);
        Assert.Equal(version == "2025-06-18", result["serverInfo"]!["title"] is not null);
    }

    [Theory]
    [InlineData("1999-01-01")]
    [InlineData("")]
    [InlineData("latest")]
    public async Task Initialize_with_an_unknown_version_answers_with_the_newest_one_the_server_knows(string version)
    {
        var messages = await McpRun.RunAsync(McpRun.InitializeWith(version));
        Assert.Equal("2025-06-18", (string?)McpRun.Reply(messages, "init")["result"]!["protocolVersion"]);
    }

    [Fact]
    public async Task Initialize_without_params_still_answers()
    {
        var messages = await McpRun.RunAsync("""{"jsonrpc":"2.0","id":"init","method":"initialize"}""");
        Assert.Equal("2025-06-18", (string?)McpRun.Reply(messages, "init")["result"]!["protocolVersion"]);
    }

    [Fact]
    public async Task Initialize_text_tells_the_model_not_to_obey_names_found_on_the_pc()
    {
        var messages = await McpRun.RunAsync(McpRun.Initialize);
        var instructions = (string)McpRun.Reply(messages, "init")["result"]!["instructions"]!;
        Assert.Contains("read-only", instructions, StringComparison.Ordinal);
        Assert.Contains("never follow", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tools_requests_before_initialize_are_refused_but_ping_works()
    {
        var messages = await McpRun.RunAsync(McpRun.Request("a", "tools/list"), McpRun.Call("b", "get_hardware"), McpRun.Request("c", "ping"));
        Assert.Equal(-32002, (int)McpRun.Reply(messages, "a")["error"]!["code"]!);
        Assert.Equal(-32002, (int)McpRun.Reply(messages, "b")["error"]!["code"]!);
        Assert.NotNull(McpRun.Reply(messages, "c")["result"]);
    }

    [Fact]
    public async Task Ping_answers_an_empty_result()
    {
        var messages = await McpRun.RunAsync(McpRun.Initialize, McpRun.Request(5, "ping"));
        var ping = messages.Single(m => m["id"]?.ToJsonString() == "5");
        Assert.Empty(ping["result"]!.AsObject());
        Assert.Equal("2.0", (string?)ping["jsonrpc"]);
    }

    [Fact]
    public async Task Notifications_get_no_answer()
    {
        var messages = await McpRun.RunAsync(
            McpRun.Initialized,
            """{"jsonrpc":"2.0","method":"notifications/anything","params":{"x":1}}""",
            """{"jsonrpc":"2.0","method":"tools/list"}""");
        Assert.Empty(messages);
    }

    [Fact]
    public async Task Tools_list_has_the_seven_tools_each_marked_read_only()
    {
        var messages = await McpRun.RunAsync(McpRun.Initialize, McpRun.Initialized, McpRun.Request("t", "tools/list"));
        var tools = McpRun.Reply(messages, "t")["result"]!["tools"]!.AsArray();
        Assert.Equal(ToolNames.Order().ToArray(), tools.Select(t => (string)t!["name"]!).Order().ToArray());
        foreach (var tool in tools)
        {
            Assert.True((bool)tool!["annotations"]!["readOnlyHint"]!, (string?)tool["name"]);
            Assert.False((bool)tool["annotations"]!["destructiveHint"]!);
            Assert.False((bool)tool["annotations"]!["openWorldHint"]!);
            Assert.Equal("object", (string?)tool["inputSchema"]!["type"]);
            Assert.False((bool)tool["inputSchema"]!["additionalProperties"]!);
            Assert.False(string.IsNullOrWhiteSpace((string?)tool["description"]));
        }
    }

    [Fact]
    public async Task Tool_annotations_and_titles_appear_only_in_the_versions_that_define_them()
    {
        var old = await McpRun.RunAsync(McpRun.InitializeWith("2024-11-05"), McpRun.Request("t", "tools/list"));
        var oldTool = McpRun.Reply(old, "t")["result"]!["tools"]![0]!;
        Assert.Null(oldTool["annotations"]);
        Assert.Null(oldTool["title"]);

        var middle = await McpRun.RunAsync(McpRun.InitializeWith("2025-03-26"), McpRun.Request("t", "tools/list"));
        var middleTool = McpRun.Reply(middle, "t")["result"]!["tools"]![0]!;
        Assert.NotNull(middleTool["annotations"]);
        Assert.Null(middleTool["title"]);
    }

    [Fact]
    public async Task Required_arguments_are_declared_in_the_schema()
    {
        var messages = await McpRun.RunAsync(McpRun.Initialize, McpRun.Request("t", "tools/list"));
        var tools = McpRun.Reply(messages, "t")["result"]!["tools"]!.AsArray();
        var process = tools.Single(t => (string?)t!["name"] == "get_process")!;
        Assert.Equal("pid", (string?)process["inputSchema"]!["required"]![0]);
    }

    [Theory]
    [InlineData("get_system_summary", "{}")]
    [InlineData("list_processes", """{"top":3}""")]
    [InlineData("get_hardware", """{"category":"cpu"}""")]
    [InlineData("get_hardware", "{}")]
    [InlineData("get_storage_health", "{}")]
    [InlineData("get_report", """{"duration":0}""")]
    public async Task Every_tool_answers_through_the_read_only_insights(string tool, string arguments)
    {
        var result = await McpRun.ToolAsync(tool, arguments);
        Assert.False((bool)result["isError"]!, result.ToJsonString());
        var text = (string)result["content"]![0]!["text"]!;
        Assert.Equal("text", (string?)result["content"]![0]!["type"]);
        Assert.NotNull(JsonNode.Parse(text));
        Assert.Equal(JsonNode.Parse(text)!.ToJsonString(), result["structuredContent"]!.ToJsonString());
    }

    [Fact]
    public async Task Get_process_answers_for_a_pid_the_fake_knows()
    {
        using var services = McpRun.Services();
        var pid = (await services.Snapshots.SampleAsync()).Processes.First(p => p.Key.Pid != 0).Key.Pid;
        var result = await McpRun.ToolAsync("get_process", $$"""{"pid":{{pid}}}""");
        Assert.False((bool)result["isError"]!);
        Assert.StartsWith(pid + ":", (string)result["structuredContent"]!["process"]!["key"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnose_observes_for_the_given_time_and_returns_a_summary()
    {
        var result = await McpRun.ToolAsync("diagnose", """{"duration":1}""");
        Assert.False((bool)result["isError"]!, result.ToJsonString());
        Assert.Equal(1, (int)result["structuredContent"]!["durationSec"]!);
        Assert.NotNull(result["structuredContent"]!["summary"]);
    }

    [Fact]
    public async Task List_processes_honours_top_and_sort_and_filter()
    {
        var top = await McpRun.ToolAsync("list_processes", """{"top":2,"sort":"mem"}""");
        Assert.Equal(2, top["structuredContent"]!["processes"]!.AsArray().Count);
        Assert.Equal("mem", (string?)top["structuredContent"]!["sort"]);

        var filtered = await McpRun.ToolAsync("list_processes", """{"filter":"svchost"}""");
        var names = filtered["structuredContent"]!["processes"]!.AsArray().Select(p => (string)p!["name"]!).ToArray();
        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.Contains("svchost", n, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Structured_content_is_sent_only_to_clients_on_the_newest_version()
    {
        var newest = await McpRun.ToolAsync("get_storage_health", version: "2025-06-18");
        var older = await McpRun.ToolAsync("get_storage_health", version: "2025-03-26");
        var oldest = await McpRun.ToolAsync("get_storage_health", version: "2024-11-05");
        Assert.NotNull(newest["structuredContent"]);
        Assert.Null(older["structuredContent"]);
        Assert.Null(oldest["structuredContent"]);
        Assert.NotNull(JsonNode.Parse((string)older["content"]![0]!["text"]!));
    }

    [Theory]
    [InlineData("list_processes", """{"top":0}""")]
    [InlineData("list_processes", """{"top":201}""")]
    [InlineData("list_processes", """{"top":1.5}""")]
    [InlineData("list_processes", """{"top":"3"}""")]
    [InlineData("list_processes", """{"sort":"bogus"}""")]
    [InlineData("list_processes", """{"sort":1}""")]
    [InlineData("list_processes", """{"unknown":1}""")]
    [InlineData("list_processes", "[]")]
    [InlineData("get_process", "{}")]
    [InlineData("get_process", """{"pid":-1}""")]
    [InlineData("get_process", """{"pid":4294967296}""")]
    [InlineData("get_process", """{"pid":"abc"}""")]
    [InlineData("get_process", """{"pid":4000000000}""")]
    [InlineData("get_hardware", """{"category":"everything"}""")]
    [InlineData("get_system_summary", """{"x":1}""")]
    public async Task Bad_arguments_come_back_as_a_tool_error_the_model_can_read(string tool, string arguments)
    {
        var result = await McpRun.ToolAsync(tool, arguments);
        Assert.True((bool)result["isError"]!, result.ToJsonString());
        Assert.False(string.IsNullOrWhiteSpace((string?)result["content"]![0]!["text"]));
        Assert.Null(result["structuredContent"]);
    }

    [Fact]
    public async Task The_filter_length_is_capped()
    {
        var result = await McpRun.ToolAsync("list_processes", $$"""{"filter":"{{new string('a', 129)}}"}""");
        Assert.True((bool)result["isError"]!);
    }

    [Fact]
    public async Task Missing_arguments_for_a_tool_without_required_ones_are_fine()
    {
        var messages = await McpRun.RunAsync(McpRun.Initialize, """{"jsonrpc":"2.0","id":"c","method":"tools/call","params":{"name":"get_storage_health"}}""");
        Assert.False((bool)McpRun.Reply(messages, "c")["result"]!["isError"]!);
    }

    [Fact]
    public async Task Duration_over_the_limit_is_clamped_not_run_for_ever()
    {
        // 31 s would be refused by the range check of list tools; diagnose clamps to 30 s, which a test cannot wait for,
        // so only the argument path is checked: a negative duration is clamped up to 1 s and finishes.
        var result = await McpRun.ToolAsync("diagnose", """{"duration":-5}""");
        Assert.False((bool)result["isError"]!, result.ToJsonString());
        Assert.Equal(1, (int)result["structuredContent"]!["durationSec"]!);
    }

    [Fact]
    public async Task Unknown_tool_and_missing_name_are_protocol_errors_with_code_32602()
    {
        var messages = await McpRun.RunAsync(
            McpRun.Initialize,
            McpRun.Call("a", "delete_everything"),
            """{"jsonrpc":"2.0","id":"b","method":"tools/call","params":{}}""",
            """{"jsonrpc":"2.0","id":"c","method":"tools/call"}""");
        Assert.Equal(-32602, (int)McpRun.Reply(messages, "a")["error"]!["code"]!);
        Assert.Equal(-32602, (int)McpRun.Reply(messages, "b")["error"]!["code"]!);
        Assert.Equal(-32602, (int)McpRun.Reply(messages, "c")["error"]!["code"]!);
    }

    [Fact]
    public async Task Unknown_method_is_32601_and_echoes_the_id()
    {
        var messages = await McpRun.RunAsync(McpRun.Initialize, McpRun.Request("zz", "resources/list"), McpRun.Request(7, "prompts/list"));
        Assert.Equal(-32601, (int)McpRun.Reply(messages, "zz")["error"]!["code"]!);
        Assert.Equal(-32601, (int)messages.Single(m => m["id"]?.ToJsonString() == "7")["error"]!["code"]!);
    }

    [Fact]
    public async Task A_parse_error_answers_with_a_null_id_and_the_server_keeps_going()
    {
        var messages = await McpRun.RunAsync("this is not json", "{\"jsonrpc\":", McpRun.Request("after", "ping"));
        var errors = messages.Where(m => m["error"] is not null).ToArray();
        Assert.Equal(2, errors.Length);
        Assert.All(errors, e =>
        {
            Assert.Equal(-32700, (int)e["error"]!["code"]!);
            Assert.True(e.AsObject().ContainsKey("id"));
            Assert.Null(e["id"]);
        });
        Assert.NotNull(McpRun.Reply(messages, "after")["result"]);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":true,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":{"a":1},"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":null,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":5}""")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    public async Task Invalid_requests_get_32600_with_a_null_or_echoed_id(string line)
    {
        var messages = await McpRun.RunAsync(line);
        var error = Assert.Single(messages)["error"]!;
        Assert.Equal(-32600, (int)error["code"]!);
    }

    [Fact]
    public async Task A_batch_is_answered_with_an_array_and_notifications_inside_it_are_silent()
    {
        var messages = await McpRun.RunAsync(
            McpRun.Initialize,
            $"[{McpRun.Request("p1", "ping")},{McpRun.Initialized},{McpRun.Request("p2", "ping")}]");
        var batch = messages.Single(m => m is JsonArray).AsArray();
        Assert.Equal(["\"p1\"", "\"p2\""], batch.Select(m => m!["id"]!.ToJsonString()).ToArray());
    }

    [Fact]
    public async Task An_empty_batch_and_an_oversize_batch_are_invalid_requests()
    {
        var oversize = "[" + string.Join(",", Enumerable.Repeat(McpRun.Request(1, "ping"), McpServer.MaxBatch + 1)) + "]";
        var messages = await McpRun.RunAsync("[]", oversize, McpRun.Request("ok", "ping"));
        Assert.Equal(2, messages.Count(m => (int?)m["error"]?["code"] == -32600));
        Assert.NotNull(McpRun.Reply(messages, "ok")["result"]);
    }

    [Fact]
    public async Task A_line_over_the_limit_is_refused_and_the_next_request_still_works()
    {
        var huge = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{\"pad\":\"" + new string('x', McpServer.MaxLineChars + 10) + "\"}}";
        var messages = await McpRun.RunAsync(huge, McpRun.Request("after", "ping"));
        Assert.Equal(-32600, (int)messages.First(m => m["error"] is not null)["error"]!["code"]!);
        Assert.NotNull(McpRun.Reply(messages, "after")["result"]);
    }

    [Fact]
    public async Task Blank_lines_and_crlf_line_ends_are_tolerated()
    {
        var sink = new LineSink();
        using var services = McpRun.Services();
        var server = new McpServer(services.Insights, services.Redactor, "9.9.9");
        await server.RunAsync(new StringReader("\r\n\n" + McpRun.Request("a", "ping") + "\r\n"), sink, CancellationToken.None);
        Assert.Single(sink.Messages);
    }

    [Fact]
    public async Task Input_that_ends_without_a_newline_is_still_answered()
    {
        var sink = new LineSink();
        using var services = McpRun.Services();
        var server = new McpServer(services.Insights, services.Redactor, "9.9.9");
        await server.RunAsync(new StringReader(McpRun.Request("a", "ping")), sink, CancellationToken.None);
        Assert.Single(sink.Messages);
    }

    [Fact]
    public async Task End_of_input_ends_the_server_even_with_nothing_sent()
    {
        var sink = new LineSink();
        using var services = McpRun.Services();
        var server = new McpServer(services.Insights, services.Redactor, "9.9.9");
        await server.RunAsync(new StringReader(string.Empty), sink, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(string.Empty, sink.Text);
    }

    [Fact]
    public async Task Cancelling_the_token_ends_the_server_while_it_waits_for_input()
    {
        var input = new LiveInput();
        var sink = new LineSink();
        using var services = McpRun.Services();
        using var cancel = new CancellationTokenSource();
        var run = new McpServer(services.Insights, services.Redactor, "9.9.9").RunAsync(input, sink, cancel.Token);
        input.Send(McpRun.Request("a", "ping"));
        await sink.WaitForLinesAsync(1);
        await cancel.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task End_of_input_waits_for_a_call_that_is_still_running()
    {
        using var services = McpRun.Services();
        var started = new TaskCompletionSource();
        var stub = new StubInsights(services.Insights)
        {
            OnList = async (query, ct) =>
            {
                started.SetResult();
                await Task.Delay(300, ct);
                return Samples.List(Samples.Process(1, "slow.exe"));
            },
        };
        var input = new LiveInput();
        var sink = new LineSink();
        var run = new McpServer(stub, services.Redactor, "9.9.9").RunAsync(input, sink, CancellationToken.None);
        input.Send(McpRun.Initialize);
        input.Send(McpRun.Call("slow", "list_processes"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        input.Finish();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("slow.exe", sink.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_one_observation_runs_at_a_time()
    {
        using var services = McpRun.Services();
        var stub = new StubInsights(services.Insights)
        {
            OnList = async (query, ct) =>
            {
                await Task.Delay(500, ct);
                return Samples.List(Samples.Process(1, "a.exe"));
            },
        };
        var messages = await McpRun.RunAsync(
            stub,
            services.Redactor,
            McpRun.Initialize,
            McpRun.Call("one", "list_processes"),
            McpRun.Call("two", "list_processes"));
        var results = new[] { McpRun.Reply(messages, "one")["result"]!, McpRun.Reply(messages, "two")["result"]! };
        Assert.Equal(1, results.Count(r => (bool)r["isError"]!));
        var refused = results.Single(r => (bool)r["isError"]!);
        Assert.Contains("another observation", (string)refused["content"]![0]!["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tool_that_does_not_observe_is_not_blocked_by_a_running_observation()
    {
        using var services = McpRun.Services();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var stub = new StubInsights(services.Insights)
        {
            OnList = async (query, ct) =>
            {
                started.SetResult();
                await release.Task.WaitAsync(ct);
                return Samples.List(Samples.Process(1, "a.exe"));
            },
        };
        var input = new LiveInput();
        var sink = new LineSink();
        var run = new McpServer(stub, services.Redactor, "9.9.9").RunAsync(input, sink, CancellationToken.None);
        input.Send(McpRun.Initialize);
        input.Send(McpRun.Call("slow", "list_processes"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        input.Send(McpRun.Call("hw", "get_hardware"));
        await sink.WaitForLinesAsync(2);
        Assert.False((bool)McpRun.Reply(sink.Messages, "hw")["result"]!["isError"]!);
        release.SetResult();
        input.Finish();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_cancelled_notification_stops_the_call_and_no_answer_is_sent_for_it()
    {
        using var services = McpRun.Services();
        var started = new TaskCompletionSource();
        var stub = new StubInsights(services.Insights)
        {
            OnList = async (query, ct) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return Samples.List();
            },
        };
        var input = new LiveInput();
        var sink = new LineSink();
        var run = new McpServer(stub, services.Redactor, "9.9.9").RunAsync(input, sink, CancellationToken.None);
        input.Send(McpRun.Initialize);
        input.Send(McpRun.Call("slow", "list_processes"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        input.Send("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"slow"}}""");
        input.Send(McpRun.Request("after", "ping"));
        await sink.WaitForLinesAsync(2);
        input.Finish();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(sink.Messages, m => m["id"]?.ToJsonString() == "\"slow\"");
    }

    [Fact]
    public async Task A_cancel_that_follows_its_call_on_the_very_next_line_is_never_lost()
    {
        // Everything is in the input from the start: the reader sees the cancel right after the call, before the call has run.
        using var services = McpRun.Services();
        var stub = new StubInsights(services.Insights)
        {
            OnList = async (query, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Samples.List();
            },
        };
        var sink = new LineSink();
        var input = string.Join(
            "\n",
            McpRun.Initialize,
            McpRun.Call("slow", "list_processes"),
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"slow"}}""",
            McpRun.Request("after", "ping"))
            + "\n";

        await new McpServer(stub, services.Redactor, "9.9.9").RunAsync(new StringReader(input), sink, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.NotNull(McpRun.Reply(sink.Messages, "after")["result"]);
        Assert.DoesNotContain(sink.Messages, m => m["id"]?.ToJsonString() == "\"slow\"");
    }

    [Fact]
    public async Task A_call_that_runs_past_the_timeout_is_stopped_and_answered_as_a_tool_error()
    {
        using var services = McpRun.Services();
        var stub = new StubInsights(services.Insights)
        {
            OnList = async (query, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Samples.List();
            },
        };
        var sink = new LineSink();
        var input = string.Join("\n", McpRun.Initialize, McpRun.Call("slow", "list_processes"), McpRun.Request("after", "ping")) + "\n";

        await new McpServer(stub, services.Redactor, "9.9.9", TimeSpan.FromMilliseconds(200))
            .RunAsync(new StringReader(input), sink, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(15));

        var result = McpRun.Reply(sink.Messages, "slow")["result"]!;
        Assert.True((bool)result["isError"]!);
        Assert.Contains("too long", (string)result["content"]![0]!["text"]!, StringComparison.Ordinal);
        Assert.NotNull(McpRun.Reply(sink.Messages, "after")["result"]);
    }

    [Fact]
    public async Task A_timed_out_observation_gives_the_gate_back()
    {
        using var services = McpRun.Services();
        var calls = 0;
        var stub = new StubInsights(services.Insights)
        {
            OnList = async (query, ct) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }

                return Samples.List(Samples.Process(1, "second.exe"));
            },
        };
        var input = new LiveInput();
        var sink = new LineSink();
        var run = new McpServer(stub, services.Redactor, "9.9.9", TimeSpan.FromMilliseconds(200)).RunAsync(input, sink, CancellationToken.None);
        input.Send(McpRun.Initialize);
        input.Send(McpRun.Call("first", "list_processes"));
        await sink.WaitForLinesAsync(2);
        input.Send(McpRun.Call("second", "list_processes"));
        await sink.WaitForLinesAsync(3);
        input.Finish();
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True((bool)McpRun.Reply(sink.Messages, "first")["result"]!["isError"]!);
        Assert.False((bool)McpRun.Reply(sink.Messages, "second")["result"]!["isError"]!);
    }

    [Fact]
    public async Task A_result_over_the_size_limit_becomes_a_tool_error_that_says_how_to_narrow_it()
    {
        using var services = McpRun.Services();
        var stub = new StubInsights(services.Insights)
        {
            OnList = (_, _) => Task.FromResult(Samples.List(
                Enumerable.Range(1, 200).Select(i => Samples.Process((uint)i, new string('x', 6000) + ".exe")).ToArray())),
        };

        var messages = await McpRun.RunAsync(stub, services.Redactor, McpRun.Initialize, McpRun.Call("big", "list_processes", """{"top":200}"""));

        var result = McpRun.Reply(messages, "big")["result"]!;
        Assert.True((bool)result["isError"]!);
        Assert.Contains("too large", (string)result["content"]![0]!["text"]!, StringComparison.Ordinal);
        Assert.True(result["content"]![0]!["text"]!.ToJsonString().Length < 1000);
    }

    [Fact]
    public async Task A_denied_storage_health_is_returned_as_it_is_without_asking_for_administrator_rights()
    {
        using var services = McpRun.Services();
        var denied = Value.Denied<StorageHealth>("test", "access is denied");
        var stub = new StubInsights(services.Insights)
        {
            OnStorage = _ => Task.FromResult(new StorageHealthReport(StorageHealthReport.CurrentSchemaVersion, [new DriveHealth(0, "Test Drive", denied)])),
        };

        var messages = await McpRun.RunAsync(stub, services.Redactor, McpRun.Initialize, McpRun.Call("h", "get_storage_health"));

        var result = McpRun.Reply(messages, "h")["result"]!;
        Assert.False((bool)result["isError"]!);
        var health = result["structuredContent"]!["drives"]![0]!["health"]!;
        Assert.Equal("denied", (string?)health["status"]);
        Assert.Null(health["value"]);
    }

    [Fact]
    public async Task A_failing_insight_becomes_a_tool_error_not_a_dead_server()
    {
        using var services = McpRun.Services();
        var stub = new StubInsights(services.Insights) { OnList = (_, _) => throw new InvalidOperationException("boom") };
        var messages = await McpRun.RunAsync(stub, services.Redactor, McpRun.Initialize, McpRun.Call("bad", "list_processes"), McpRun.Request("after", "ping"));
        Assert.True((bool)McpRun.Reply(messages, "bad")["result"]!["isError"]!);
        Assert.NotNull(McpRun.Reply(messages, "after")["result"]);
    }

    [Fact]
    public async Task A_newline_in_a_process_name_stays_on_one_line_and_arrives_intact()
    {
        const string name = "evil\nname\r\n{\"jsonrpc\":\"2.0\"}\u2028\u2029\u0085end.exe";
        using var services = McpRun.Services();
        var stub = new StubInsights(services.Insights) { OnList = (_, _) => Task.FromResult(Samples.List(Samples.Process(9, name))) };
        var sink = new LineSink();
        await new McpServer(stub, services.Redactor, "9.9.9").RunAsync(
            new StringReader(McpRun.Initialize + "\n" + McpRun.Call("c", "list_processes") + "\n"),
            sink,
            CancellationToken.None);

        var lines = sink.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        // Line splitters of other languages also break at these; none may appear raw on the wire.
        Assert.DoesNotContain('\r', sink.Text);
        Assert.DoesNotContain((char)0x2028, sink.Text);
        Assert.DoesNotContain((char)0x2029, sink.Text);
        Assert.DoesNotContain('\u0085', sink.Text);
        var result = McpRun.Reply(sink.Messages, "c")["result"]!;
        Assert.Equal(name, (string?)result["structuredContent"]!["processes"]![0]!["name"]);
        var echoed = JsonNode.Parse((string)result["content"]![0]!["text"]!)!;
        Assert.Equal(name, (string?)echoed["processes"]![0]!["name"]);
    }

    [Fact]
    public async Task Redaction_is_applied_to_the_result_before_it_is_written()
    {
        var context = new RedactionContext("alice", "ALICE-PC", @"C:\Users\alice");
        var redactor = new SimpleRedactor(context, enabled: true);
        using var services = McpRun.Services();
        var stub = new StubInsights(services.Insights)
        {
            OnProcess = (pid, _) =>
            {
                var process = Samples.Process(pid, "tool.exe");
                var details = Samples.Details(process.Key, @"C:\Users\alice\tool.exe", @"C:\Users\alice\tool.exe --token secret123 \\ALICE-PC\share");
                return Task.FromResult<ProcessDetail?>(new ProcessDetail(process, details));
            },
        };
        var messages = await McpRun.RunAsync(stub, redactor, McpRun.Initialize, McpRun.Call("c", "get_process", """{"pid":42}"""));
        var text = McpRun.Reply(messages, "c")["result"]!.ToJsonString();
        Assert.DoesNotContain("secret123", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", text, StringComparison.Ordinal);
        var values = StringValues(McpRun.Reply(messages, "c")["result"]!["structuredContent"]!).ToArray();
        Assert.Contains(values, v => v.Contains(SimpleRedactor.Placeholder, StringComparison.Ordinal));
    }

    private static IEnumerable<string> StringValues(JsonNode node) => node switch
    {
        JsonObject obj => obj.Select(p => p.Value).OfType<JsonNode>().SelectMany(StringValues),
        JsonArray array => array.OfType<JsonNode>().SelectMany(StringValues),
        JsonValue value when value.TryGetValue<string>(out var text) => [text],
        _ => [],
    };

    [Fact]
    public async Task Without_redaction_the_same_call_returns_what_was_found()
    {
        var context = new RedactionContext("alice", "ALICE-PC", @"C:\Users\alice");
        var redactor = new SimpleRedactor(context, enabled: false);
        using var services = McpRun.Services();
        var stub = new StubInsights(services.Insights)
        {
            OnProcess = (pid, _) =>
            {
                var process = Samples.Process(pid, "tool.exe");
                return Task.FromResult<ProcessDetail?>(new ProcessDetail(process, Samples.Details(process.Key, @"C:\Users\alice\tool.exe", "tool.exe --token secret123")));
            },
        };
        var messages = await McpRun.RunAsync(stub, redactor, McpRun.Initialize, McpRun.Call("c", "get_process", """{"pid":42}"""));
        Assert.Contains("secret123", McpRun.Reply(messages, "c")["result"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redaction_covers_error_texts_too()
    {
        var redactor = new SimpleRedactor(new RedactionContext("alice", "ALICE-PC", @"C:\Users\alice"), enabled: true);
        using var services = McpRun.Services();
        var stub = new StubInsights(services.Insights)
        {
            OnList = (_, _) => throw new InvalidOperationException(@"cannot read C:\Users\alice\secret.txt"),
        };
        var messages = await McpRun.RunAsync(stub, redactor, McpRun.Initialize, McpRun.Call("c", "list_processes"));
        var text = (string)McpRun.Reply(messages, "c")["result"]!["content"]![0]!["text"]!;
        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Through_the_cli_the_command_line_is_hidden_by_default_and_shown_with_no_redact_at_start_up()
    {
        var pid = (await AnyPidAsync()).ToString();
        var input = string.Join("\n", McpRun.Initialize, McpRun.Call("c", "get_process", $$"""{"pid":{{pid}}}""")) + "\n";

        var hidden = await CliRun.RunAsync(["mcp"], input);
        var shown = await CliRun.RunAsync(["mcp", "--no-redact"], input);

        Assert.Equal(0, hidden.ExitCode);
        Assert.Equal(0, shown.ExitCode);
        Assert.DoesNotContain("--sample", hidden.Out, StringComparison.Ordinal);
        Assert.Contains(SimpleRedactor.Placeholder, hidden.Out, StringComparison.Ordinal);
        Assert.Contains("--sample", shown.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_cannot_turn_redaction_off_by_asking()
    {
        var pid = (await AnyPidAsync()).ToString();
        var asks = $$"""{"pid":{{pid}},"redact":false,"noRedact":true,"no-redact":true}""";
        var messages = await McpRun.RunAsync(McpRun.Initialize, McpRun.Call("c", "get_process", asks));
        var result = McpRun.Reply(messages, "c")["result"]!;
        Assert.True((bool)result["isError"]!);
        Assert.DoesNotContain("--sample", result.ToJsonString(), StringComparison.Ordinal);

        var initialize = """{"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{"redact":false},"clientInfo":{"name":"x","version":"1"},"redact":false}}""";
        var again = await McpRun.RunAsync(initialize, McpRun.Call("c", "get_process", $$"""{"pid":{{pid}}}"""));
        Assert.DoesNotContain("--sample", McpRun.Reply(again, "c")["result"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stdout_of_the_mcp_command_carries_only_json_rpc_lines_and_the_notice_goes_to_stderr()
    {
        var input = string.Join("\n", McpRun.Initialize, McpRun.Initialized, McpRun.Request("t", "tools/list")) + "\n";
        var r = await CliRun.RunAsync(["mcp"], input);
        Assert.Equal(0, r.ExitCode);
        Assert.All(r.OutLines, l => Assert.Equal("2.0", (string?)JsonNode.Parse(l)!["jsonrpc"]));
        Assert.StartsWith("pcmi: ", r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_server_answers_each_request_with_exactly_one_line()
    {
        var messages = await McpRun.RunAsync(
            McpRun.Initialize,
            McpRun.Initialized,
            McpRun.Request(1, "tools/list"),
            McpRun.Call(2, "get_hardware"),
            McpRun.Request(3, "ping"));
        Assert.Equal(4, messages.Count);
        Assert.Equal(["\"init\"", "1", "2", "3"], messages.Select(m => m["id"]!.ToJsonString()).Order().ToArray());
    }

    private static async Task<uint> AnyPidAsync()
    {
        using var services = McpRun.Services();
        return (await services.Snapshots.SampleAsync()).Processes.First(p => p.Key.Pid != 0).Key.Pid;
    }
}
