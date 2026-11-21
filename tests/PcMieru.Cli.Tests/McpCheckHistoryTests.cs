using System.Text.Json.Nodes;
using PcMieru.Checks;
using PcMieru.Cli.Mcp;
using PcMieru.Core.Reporting;

namespace PcMieru.Cli.Tests;

/// <summary>
/// The MCP tool <c>get_check_history</c>: read-only by its type, a list that is the CLI's summary, one saved run by <c>runId</c>, a
/// "not found" that is never said for a file that exists but cannot be read, the size limit and cancellation of every tool, and a redaction
/// that stays on whatever the server was started with. Histories are temporary folders seeded through the real store, or stubs.
/// </summary>
public class McpCheckHistoryTests
{
    private const string ToolName = "get_check_history";

    private sealed class StubHistory(
        Func<CheckHistoryQuery, CancellationToken, Task<CheckHistoryPage>>? list = null,
        Func<Guid, CancellationToken, Task<CheckHistoryGetResult>>? get = null) : IReadOnlyCheckHistory
    {
        private readonly object _lock = new();
        private readonly List<CheckHistoryQuery> _queries = [];
        private readonly List<Guid> _fetched = [];

        public IReadOnlyList<CheckHistoryQuery> Queries
        {
            get
            {
                lock (_lock)
                {
                    return _queries.ToArray();
                }
            }
        }

        public IReadOnlyList<Guid> Fetched
        {
            get
            {
                lock (_lock)
                {
                    return _fetched.ToArray();
                }
            }
        }

        public Task<CheckHistoryPage> ListAsync(CheckHistoryQuery query, CancellationToken ct)
        {
            lock (_lock)
            {
                _queries.Add(query);
            }

            return list is null ? Task.FromResult(new CheckHistoryPage([], [])) : list(query, ct);
        }

        public Task<CheckHistoryGetResult> GetAsync(Guid runId, CancellationToken ct)
        {
            lock (_lock)
            {
                _fetched.Add(runId);
            }

            return get is null ? Task.FromResult(CheckHistoryGetResult.NotFound) : get(runId, ct);
        }
    }

    private static string Text(JsonNode result) => (string)result["content"]![0]!["text"]!;

    private static bool IsError(JsonNode result) => (bool)result["isError"]!;

    private static JsonNode Data(JsonNode result)
    {
        Assert.False(IsError(result), result.ToJsonString());
        return result["structuredContent"]!;
    }

    private static string Args(Guid id) => "{\"runId\":\"" + id.ToString("D") + "\"}";

    // ---- the tool as the list says it ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_tool_list_has_it_read_only_with_the_arguments_of_the_spec_and_no_others()
    {
        var messages = await McpRun.RunAsync(McpRun.Initialize, McpRun.Initialized, McpRun.Request("t", "tools/list"));

        var tool = McpRun.Reply(messages, "t")["result"]!["tools"]!.AsArray().Single(t => (string?)t!["name"] == ToolName)!;
        Assert.True((bool)tool["annotations"]!["readOnlyHint"]!);
        Assert.False((bool)tool["annotations"]!["destructiveHint"]!);
        Assert.False((bool)tool["annotations"]!["openWorldHint"]!);
        var schema = tool["inputSchema"]!;
        Assert.Equal("object", (string?)schema["type"]);
        Assert.False((bool)schema["additionalProperties"]!);
        Assert.Equal(["kind", "limit", "runId"], schema["properties"]!.AsObject().Select(p => p.Key).Order().ToArray());
        Assert.Equal(["system", "cpu", "memory", "storage"], schema["properties"]!["kind"]!["enum"]!.AsArray().Select(n => (string)n!).ToArray());
        Assert.Equal(1, (int)schema["properties"]!["limit"]!["minimum"]!);
        Assert.Equal(200, (int)schema["properties"]!["limit"]!["maximum"]!);
        Assert.Equal(20, (int)schema["properties"]!["limit"]!["default"]!);
        Assert.Equal("string", (string?)schema["properties"]!["runId"]!["type"]);
        Assert.Null(schema["required"]);
        Assert.Contains("never starts a check", (string)tool["description"]!, StringComparison.Ordinal);
    }

    // ---- a list ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_no_arguments_it_lists_the_newest_twenty_as_the_summary_of_the_cli()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, Enumerable.Range(1, 25).Select(n => CheckSamples.SystemOk(n)).ToArray());
        var history = CheckHistory.OpenReadOnly(data.Checks);

        var result = await McpRun.HistoryToolAsync(history);

        var entries = Data(result)["entries"]!.AsArray();
        Assert.Equal(20, entries.Count);
        Assert.Equal(CheckSamples.Id(25).ToString("D"), (string?)entries[0]!["runId"]);
        Assert.Equal(CheckSamples.Id(6).ToString("D"), (string?)entries[19]!["runId"]);
        Assert.Equal(["runId", "kind", "startedAtUtc", "endedAtUtc", "outcome", "verdict", "counts"], entries[0]!.AsObject().Select(p => p.Key).ToArray());
        Assert.Empty(Data(result)["warnings"]!.AsArray());

        // The same rows, in the same words and order, as `pcmi check history --json`.
        var cli = await CliRun.CheckAsync(["check", "history", "--json"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()));
        var cliEntries = JsonNode.Parse(cli.Out)!["entries"]!.AsArray();
        Assert.Equal(25, cliEntries.Count);
        Assert.Equal(string.Join("|", cliEntries.Take(20).Select(e => e!.ToJsonString())), string.Join("|", entries.Select(e => e!.ToJsonString())));
    }

    [Fact]
    public async Task The_text_of_the_result_is_the_same_document_as_the_structured_one()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, CheckSamples.SystemOk(1), CheckSamples.StorageRead(2));

        var result = await McpRun.HistoryToolAsync(CheckHistory.OpenReadOnly(data.Checks));

        Assert.Equal(Data(result).ToJsonString(), JsonNode.Parse(Text(result))!.ToJsonString());
    }

    [Fact]
    public async Task The_list_with_a_file_that_cannot_be_used_is_the_cli_document_with_its_warning_and_is_not_an_error()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, CheckSamples.SystemOk(1), CheckSamples.StorageRead(2), CheckSamples.SystemCaution(3));
        var broken = Path.Combine(data.Checks, "system-20261120T030400000Z-" + CheckSamples.Id(4).ToString("N") + ".json");
        await File.WriteAllTextAsync(broken, "{ this is not json");

        var result = await McpRun.HistoryToolAsync(CheckHistory.OpenReadOnly(data.Checks), "{\"limit\":200}");

        var document = Data(result);
        Assert.Equal(3, document["entries"]!.AsArray().Count);
        var warning = Assert.Single(document["warnings"]!.AsArray());
        Assert.Equal("history_unreadable", (string?)warning!["code"]);
        Assert.Equal(1, (int)warning["count"]!);
        var cli = await CliRun.CheckAsync(["check", "history", "--json"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()));
        Assert.Equal(JsonNode.Parse(cli.Out)!.ToJsonString(), document.ToJsonString());
        // Reading moved nothing aside.
        Assert.True(File.Exists(broken));
    }

    [Fact]
    public async Task A_history_folder_that_is_not_there_is_an_empty_list_and_nothing_is_made()
    {
        using var data = new TempFolder();

        var result = await McpRun.HistoryToolAsync(CheckHistory.OpenReadOnly(data.Checks));

        var document = Data(result);
        Assert.Empty(document["entries"]!.AsArray());
        Assert.Empty(document["warnings"]!.AsArray());
        Assert.False(Directory.Exists(data.Checks));
        Assert.False(Directory.Exists(data.Path));
    }

    [Fact]
    public async Task Listing_and_fetching_change_no_file_in_the_folder()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, CheckSamples.SystemOk(1), CheckSamples.StorageRead(2));
        File.WriteAllText(Path.Combine(data.Checks, "system-20261120T030400000Z-" + CheckSamples.Id(4).ToString("N") + ".json"), "{ not json");
        string Snapshot() => string.Join(
            "\n",
            new DirectoryInfo(data.Checks).GetFileSystemInfos().OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => f.Name + "|" + ((f as FileInfo)?.Length ?? -1) + "|" + f.LastWriteTimeUtc.Ticks));
        var before = Snapshot();
        var history = CheckHistory.OpenReadOnly(data.Checks);

        await McpRun.HistoryToolAsync(history);
        await McpRun.HistoryToolAsync(history, Args(CheckSamples.Id(1)));
        await McpRun.HistoryToolAsync(history, Args(CheckSamples.Id(4)));
        await McpRun.HistoryToolAsync(history, Args(Guid.NewGuid()));

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task Before_protocol_2025_06_18_the_list_is_in_the_text_only()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, CheckSamples.SystemOk(1));

        var result = await McpRun.HistoryToolAsync(CheckHistory.OpenReadOnly(data.Checks), version: "2024-11-05");

        Assert.False(IsError(result));
        Assert.Null(result["structuredContent"]);
        Assert.Single(JsonNode.Parse(Text(result))!["entries"]!.AsArray());
    }

    // ---- limit and kind --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_default_is_twenty_and_the_query_the_library_gets_has_the_kind_and_the_limit_of_the_call()
    {
        var stub = new StubHistory();

        await McpRun.HistoryToolAsync(stub);
        await McpRun.HistoryToolAsync(stub, "{\"kind\":\"storage\",\"limit\":7}");
        await McpRun.HistoryToolAsync(stub, "{\"limit\":1}");
        await McpRun.HistoryToolAsync(stub, "{\"limit\":200}");

        Assert.Equal(
            [new CheckHistoryQuery(null, 20), new CheckHistoryQuery(CheckKind.Storage, 7), new CheckHistoryQuery(null, 1), new CheckHistoryQuery(null, 200)],
            stub.Queries.ToArray());
    }

    [Fact]
    public async Task A_limit_of_one_gives_the_newest_and_a_limit_of_two_hundred_gives_all_two_hundred_of_a_kind()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, Enumerable.Range(1, 205).Select(n => CheckSamples.SystemOk(n)).ToArray());
        var history = CheckHistory.OpenReadOnly(data.Checks);

        var one = Data(await McpRun.HistoryToolAsync(history, "{\"limit\":1}"));
        var most = Data(await McpRun.HistoryToolAsync(history, "{\"limit\":200}"));
        var plain = Data(await McpRun.HistoryToolAsync(history));

        Assert.Equal(CheckSamples.Id(205).ToString("D"), (string?)Assert.Single(one["entries"]!.AsArray())!["runId"]);
        Assert.Equal(200, most["entries"]!.AsArray().Count);
        Assert.Equal(CheckSamples.Id(205).ToString("D"), (string?)most["entries"]![0]!["runId"]);
        Assert.Equal(20, plain["entries"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("{\"limit\":0}", "'limit'")]
    [InlineData("{\"limit\":-1}", "'limit'")]
    [InlineData("{\"limit\":201}", "'limit'")]
    [InlineData("{\"limit\":1000000000000}", "'limit'")]
    [InlineData("{\"limit\":1.5}", "'limit'")]
    [InlineData("{\"limit\":\"5\"}", "'limit'")]
    [InlineData("{\"limit\":true}", "'limit'")]
    [InlineData("{\"kind\":\"gpu\"}", "kind")]
    [InlineData("{\"kind\":5}", "kind")]
    [InlineData("{\"foo\":1}", "unknown argument 'foo'")]
    [InlineData("{\"runId\":5}", "runId")]
    [InlineData("{\"runId\":\"not-a-uuid\"}", "UUID")]
    [InlineData("{\"runId\":\"00000000-0000-0000-0000-00000000000g\"}", "UUID")]
    [InlineData("{\"runId\":\"00000001-1111-4222-8333-444455556666\",\"kind\":\"system\"}", "alone")]
    [InlineData("{\"runId\":\"00000001-1111-4222-8333-444455556666\",\"limit\":5}", "alone")]
    [InlineData("[]", "object")]
    public async Task A_bad_argument_is_a_tool_error_that_names_it_and_the_library_is_never_asked(string arguments, string says)
    {
        var stub = new StubHistory();

        var result = await McpRun.HistoryToolAsync(stub, arguments);

        Assert.True(IsError(result), result.ToJsonString());
        Assert.Contains(says, Text(result), StringComparison.Ordinal);
        Assert.Null(result["structuredContent"]);
        Assert.Empty(stub.Queries);
        Assert.Empty(stub.Fetched);
    }

    [Fact]
    public async Task A_runId_that_is_too_long_is_a_tool_error()
    {
        var stub = new StubHistory();

        var result = await McpRun.HistoryToolAsync(stub, "{\"runId\":\"" + new string('a', 65) + "\"}");

        Assert.True(IsError(result));
        Assert.Empty(stub.Fetched);
    }

    [Fact]
    public async Task The_kind_filter_gives_only_that_kind()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(
            data.Checks,
            CheckSamples.SystemOk(1),
            CheckSamples.StorageRead(2),
            CheckSamples.Failed(CheckKind.Cpu, 3),
            CheckSamples.StorageDenied(4),
            CheckSamples.SystemCaution(5));
        var history = CheckHistory.OpenReadOnly(data.Checks);

        var storage = Data(await McpRun.HistoryToolAsync(history, "{\"kind\":\"storage\"}"))["entries"]!.AsArray();
        var cpu = Data(await McpRun.HistoryToolAsync(history, "{\"kind\":\"cpu\"}"))["entries"]!.AsArray();
        var memory = Data(await McpRun.HistoryToolAsync(history, "{\"kind\":\"memory\"}"))["entries"]!.AsArray();

        Assert.Equal([CheckSamples.Id(4).ToString("D"), CheckSamples.Id(2).ToString("D")], storage.Select(e => (string)e!["runId"]!).ToArray());
        Assert.All(storage, e => Assert.Equal("storage", (string?)e!["kind"]));
        Assert.Equal([CheckSamples.Id(3).ToString("D")], cpu.Select(e => (string)e!["runId"]!).ToArray());
        Assert.Empty(memory);
    }

    // ---- one run by runId -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_runId_gives_the_saved_result_as_it_was_saved()
    {
        using var data = new TempFolder();
        var saved = CheckSamples.WithNoteAndValues(3);
        await CheckSamples.SeedAsync(data.Checks, CheckSamples.SystemOk(1), CheckSamples.StorageRead(2), saved);
        var history = CheckHistory.OpenReadOnly(data.Checks);

        var result = await McpRun.HistoryToolAsync(history, Args(saved.RunId));

        var document = Data(result);
        Assert.Equal(["result"], document.AsObject().Select(p => p.Key).ToArray());
        Assert.Equal("pcm-check/1", (string?)document["result"]!["format"]);
        Assert.Equal(JsonNode.Parse(CheckJson.Serialize(saved))!.ToJsonString(), document["result"]!.ToJsonString());
        Assert.Equal(saved.RunId, CheckJson.Deserialize(document["result"]!.ToJsonString()).RunId);
        Assert.Equal(JsonNode.Parse(Text(result))!.ToJsonString(), document.ToJsonString());
    }

    [Fact]
    public async Task The_runId_may_be_written_with_or_without_dashes_and_in_capitals()
    {
        using var data = new TempFolder();
        var saved = CheckSamples.StorageRead(2);
        await CheckSamples.SeedAsync(data.Checks, saved);
        var history = CheckHistory.OpenReadOnly(data.Checks);

        foreach (var text in new[] { saved.RunId.ToString("N"), saved.RunId.ToString("D").ToUpperInvariant(), saved.RunId.ToString("B") })
        {
            var result = await McpRun.HistoryToolAsync(history, "{\"runId\":\"" + text + "\"}");

            Assert.Equal(saved.RunId.ToString("D"), (string?)Data(result)["result"]!["runId"]);
        }
    }

    [Fact]
    public async Task A_run_that_is_not_there_is_a_not_found_tool_error_and_says_nothing_of_a_warning()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, CheckSamples.SystemOk(1));
        var history = CheckHistory.OpenReadOnly(data.Checks);

        var result = await McpRun.HistoryToolAsync(history, Args(Guid.NewGuid()));

        Assert.True(IsError(result));
        Assert.StartsWith("not found", Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain("history_", Text(result), StringComparison.Ordinal);
        Assert.Null(result["structuredContent"]);
    }

    [Fact]
    public async Task A_history_that_is_not_there_has_no_run_either_and_nothing_is_made()
    {
        using var data = new TempFolder();

        var result = await McpRun.HistoryToolAsync(CheckHistory.OpenReadOnly(data.Checks), Args(CheckSamples.Id(1)));

        Assert.True(IsError(result));
        Assert.StartsWith("not found", Text(result), StringComparison.Ordinal);
        Assert.False(Directory.Exists(data.Path));
    }

    [Fact]
    public async Task A_run_whose_file_cannot_be_read_is_not_said_to_be_missing_and_carries_the_warning_code()
    {
        foreach (var code in CheckHistoryWarningCodes.All)
        {
            var stub = new StubHistory(get: (_, _) => Task.FromResult(CheckHistoryGetResult.Unreadable(code)));

            var result = await McpRun.HistoryToolAsync(stub, Args(CheckSamples.Id(1)));

            Assert.True(IsError(result), code);
            var text = Text(result);
            Assert.Contains(code, text, StringComparison.Ordinal);
            Assert.DoesNotContain("not found", text, StringComparison.Ordinal);
            Assert.Contains("not missing", text, StringComparison.Ordinal);
            Assert.Null(result["structuredContent"]);
        }
    }

    [Fact]
    public async Task The_two_answers_are_two_different_texts()
    {
        var gone = await McpRun.HistoryToolAsync(new StubHistory(get: (_, _) => Task.FromResult(CheckHistoryGetResult.NotFound)), Args(CheckSamples.Id(1)));
        var locked = await McpRun.HistoryToolAsync(new StubHistory(get: (_, _) => Task.FromResult(CheckHistoryGetResult.Unreadable("history_locked"))), Args(CheckSamples.Id(1)));

        Assert.True(IsError(gone));
        Assert.True(IsError(locked));
        Assert.NotEqual(Text(gone), Text(locked));
    }

    [Fact]
    public async Task A_run_whose_file_another_process_holds_is_an_unreadable_error_now_and_found_when_it_is_let_go()
    {
        using var data = new TempFolder();
        var saved = CheckSamples.SystemOk(1);
        await CheckSamples.SeedAsync(data.Checks, saved, CheckSamples.SystemOk(2));
        var history = CheckHistory.OpenReadOnly(data.Checks);
        var path = Path.Combine(data.Checks, CheckSamples.FileName(saved));

        JsonNode held;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            held = await McpRun.HistoryToolAsync(history, Args(saved.RunId));
        }

        var freed = await McpRun.HistoryToolAsync(history, Args(saved.RunId));

        Assert.True(IsError(held), held.ToJsonString());
        Assert.Contains("history_locked", Text(held), StringComparison.Ordinal);
        Assert.DoesNotContain("not found", Text(held), StringComparison.Ordinal);
        Assert.Equal(saved.RunId.ToString("D"), (string?)Data(freed)["result"]!["runId"]);
    }

    [Fact]
    public async Task A_damaged_file_is_unreadable_not_missing_and_is_left_where_it_is()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, CheckSamples.SystemOk(1));
        var id = CheckSamples.Id(4);
        var path = Path.Combine(data.Checks, "system-20261120T030400000Z-" + id.ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "{ this is not json");

        var result = await McpRun.HistoryToolAsync(CheckHistory.OpenReadOnly(data.Checks), Args(id));

        Assert.True(IsError(result), result.ToJsonString());
        Assert.Matches("history_[a-z_]+", Text(result));
        Assert.DoesNotContain("not found", Text(result), StringComparison.Ordinal);
        Assert.Equal("{ this is not json", await File.ReadAllTextAsync(path));
    }

    // ---- a folder that cannot be opened ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_file_where_the_history_folder_should_be_is_a_tool_error_for_a_list_and_for_a_run()
    {
        using var data = new TempFolder(create: true);
        await File.WriteAllTextAsync(data.Checks, "this is a file, not a folder");
        var history = CheckHistory.OpenReadOnly(data.Checks);

        var list = await McpRun.HistoryToolAsync(history);
        var one = await McpRun.HistoryToolAsync(history, Args(CheckSamples.Id(1)));

        foreach (var result in new[] { list, one })
        {
            Assert.True(IsError(result), result.ToJsonString());
            Assert.Contains("check history folder cannot be read", Text(result), StringComparison.Ordinal);
            Assert.Null(result["structuredContent"]);
        }

        Assert.True(File.Exists(data.Checks));
    }

    // ---- the redaction stays on --------------------------------------------------------------------------------------------------------

    private static readonly SimpleRedactor Alice = new(new RedactionContext("alice", "PCNAME", @"C:\Users\alice"), enabled: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_name_of_the_user_in_a_saved_result_is_hidden_whether_the_server_redacts_or_not(bool serverRedacts)
    {
        using var data = new TempFolder();
        var saved = CheckSamples.SystemCaution(1, "alice portable RAM");
        await CheckSamples.SeedAsync(data.Checks, saved);

        var result = await McpRun.HistoryToolAsync(CheckHistory.OpenReadOnly(data.Checks), Args(saved.RunId), redact: serverRedacts, historyRedactor: Alice);

        var text = Text(result);
        Assert.DoesNotContain("alice", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<user> portable RAM", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", Data(result).ToJsonString(), StringComparison.OrdinalIgnoreCase);
        // What is not personal is as it was saved.
        Assert.Equal(saved.RunId.ToString("D"), (string?)Data(result)["result"]!["runId"]);
        Assert.Equal("2026-11-20T03:01:00.000Z", (string?)Data(result)["result"]!["startedAtUtc"]);
        Assert.Equal("caution", (string?)Data(result)["result"]!["verdict"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_address_in_a_saved_result_is_hidden_by_the_real_redactor_whether_the_server_redacts_or_not(bool serverRedacts)
    {
        using var data = new TempFolder();
        var saved = CheckSamples.SystemCaution(2, "Memory at 192.168.1.20 and 00:1A:2B:3C:4D:5E");
        await CheckSamples.SeedAsync(data.Checks, saved);

        var result = await McpRun.HistoryToolAsync(CheckHistory.OpenReadOnly(data.Checks), Args(saved.RunId), redact: serverRedacts);

        var text = Text(result);
        Assert.DoesNotContain("192.168.1.20", text, StringComparison.Ordinal);
        Assert.DoesNotContain("00:1A:2B:3C:4D:5E", text, StringComparison.Ordinal);
        Assert.Contains("<ip>", text, StringComparison.Ordinal);
        Assert.Contains("<mac>", text, StringComparison.Ordinal);
        Assert.Equal(saved.RunId.ToString("D"), (string?)Data(result)["result"]!["runId"]);
    }

    // ---- the size limit and cancellation of every tool apply --------------------------------------------------------------------------------

    [Fact]
    public async Task A_result_over_the_size_limit_is_the_tool_error_of_every_tool()
    {
        var entries = Enumerable.Range(1, 8000)
            .Select(i => new CheckHistoryEntry(
                CheckSamples.Id(i), CheckKind.System, CheckSamples.Start, CheckSamples.Start.AddSeconds(1), CheckOutcome.Completed, CheckVerdict.Ok, new CheckCounts(3, 0, 0, 0, 1)))
            .ToArray();
        var stub = new StubHistory(list: (_, _) => Task.FromResult(new CheckHistoryPage(entries, [])));

        var result = await McpRun.HistoryToolAsync(stub);

        Assert.True(IsError(result), "a list over the size limit was returned");
        Assert.Contains("too large", Text(result), StringComparison.Ordinal);
        Assert.True(Text(result).Length < 1000);
    }

    [Fact]
    public async Task A_cancelled_call_gets_no_answer_and_the_library_was_told_to_stop()
    {
        using var services = McpRun.Services();
        var started = new TaskCompletionSource();
        var stopped = new TaskCompletionSource();
        var stub = new StubHistory(list: async (_, ct) =>
        {
            ct.Register(() => stopped.TrySetResult());
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new CheckHistoryPage([], []);
        });
        var input = new LiveInput();
        var sink = new LineSink();
        var run = new McpServer(services.Insights, services.Redactor, "9.9.9", stub).RunAsync(input, sink, CancellationToken.None);
        input.Send(McpRun.Initialize);
        input.Send(McpRun.Call("slow", ToolName));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        input.Send("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"slow"}}""");
        input.Send(McpRun.Request("after", "ping"));
        await sink.WaitForLinesAsync(2);
        input.Finish();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(sink.Messages, m => m["id"]?.ToJsonString() == "\"slow\"");
        Assert.Contains(sink.Messages, m => m["id"]?.ToJsonString() == "\"after\"");
    }

    [Fact]
    public async Task A_call_that_takes_too_long_is_stopped_with_a_tool_error()
    {
        using var services = McpRun.Services();
        var stub = new StubHistory(list: async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new CheckHistoryPage([], []);
        });
        var sink = new LineSink();
        var server = new McpServer(services.Insights, services.Redactor, "9.9.9", stub, TimeSpan.FromMilliseconds(200));

        await server.RunAsync(
            new StringReader(string.Join("\n", McpRun.Initialize, McpRun.Initialized, McpRun.Call("c", ToolName)) + "\n"),
            sink,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        var result = McpRun.Reply(sink.Messages, "c")["result"]!;
        Assert.True(IsError(result));
        Assert.Contains("too long", Text(result), StringComparison.Ordinal);
    }

    // ---- an error of the library other than the folder ---------------------------------------------------------------------------------

    [Fact]
    public async Task An_unexpected_failure_of_the_library_is_a_tool_error_not_a_crash_and_the_next_call_still_works()
    {
        using var services = McpRun.Services();
        var stub = new StubHistory(list: (_, _) => throw new InvalidOperationException("boom"));

        var messages = await McpRun.RunWithHistoryAsync(
            stub,
            historyRedactor: null,
            services.Insights,
            services.Redactor,
            McpRun.Initialize,
            McpRun.Initialized,
            McpRun.Call("a", ToolName),
            McpRun.Request("b", "ping"));

        Assert.True(IsError(McpRun.Reply(messages, "a")["result"]!));
        Assert.NotNull(McpRun.Reply(messages, "b")["result"]);
    }
}
