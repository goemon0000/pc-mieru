// Provenance: Off_makes_no_network_call_at_all and Not_due_makes_no_network_call are ported from UpdaterTests of the owner's pl-cloud (Parts Life, MIT, same owner;
// see NOTICE.txt); the other cases are new: this repo has Ask (no choice yet) as well as Off, a lock and a state folder that can be unusable.
using PcMieru.Update.Feed;

namespace PcMieru.Update.Tests;

/// <summary>
/// The cases in which **nothing goes out**: not a request, not an HTTP client, not even a file written. These come first because they are the promise of
/// DESIGN-B 8: in Ask and Off, with a state that says "not yet", while a rate limit is waited out, and while another check runs, the feed is silent.
/// Every test counts three ways: the fake handler's requests, the transports the service made, and the process-wide counters of FeedTraffic.
/// </summary>
[Collection(FeedCollection.Name)]
public sealed class FeedZeroHttpTests : IDisposable
{
    private readonly FeedRig _rig = new();
    private readonly long _requestsBefore = FeedTraffic.Requests;
    private readonly long _clientsBefore = FeedTraffic.ClientsCreated;

    public void Dispose() => _rig.Dispose();

    private void AssertSilent()
    {
        Assert.Equal(0, _rig.Gh.Count);
        Assert.Equal(0, _rig.ClientsMade);
        Assert.Equal(_requestsBefore, FeedTraffic.Requests);
        Assert.Equal(_clientsBefore, FeedTraffic.ClientsCreated);
    }

    [Theory]
    [InlineData(UpdateMode.Ask)]
    [InlineData(UpdateMode.Off)]
    public async Task Ask_and_Off_send_nothing_make_no_client_and_write_no_file(UpdateMode mode)
    {
        _rig.Release();
        foreach (var manual in new[] { false, true })
        {
            var result = await _rig.Check(mode, manual);
            Assert.Equal(UpdateCheckKind.NotAllowed, result.Kind);
            Assert.Equal(mode, result.Mode);
            Assert.False(result.Fetched);
        }

        AssertSilent();
        Assert.False(Directory.Exists(_rig.StateFolder), "not even the state folder is made");
    }

    [Theory]
    [InlineData(UpdateMode.Ask)]
    [InlineData(UpdateMode.Off)]
    public async Task The_mode_is_looked_at_before_the_token_and_before_a_lock(UpdateMode mode)
    {
        // A held lock and a cancelled token change nothing: the mode comes first, so the answer is "not allowed" and no file is touched.
        _rig.Release();
        Directory.CreateDirectory(_rig.StateFolder);
        Assert.Equal(LockOutcome.Acquired, _rig.Store.TryLock(out var held));
        using (held)
        {
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            var result = await _rig.Check(mode, manual: true, cancelled.Token);
            Assert.Equal(UpdateCheckKind.NotAllowed, result.Kind);
        }

        AssertSilent();
    }

    [Fact]
    public void Every_mode_is_classified_and_only_notify_and_automatic_may_use_the_network()
    {
        Assert.False(UpdateModes.MayUseNetwork(UpdateMode.Ask));
        Assert.False(UpdateModes.MayUseNetwork(UpdateMode.Off));
        Assert.True(UpdateModes.MayUseNetwork(UpdateMode.Notify));
        Assert.True(UpdateModes.MayUseNetwork(UpdateMode.Automatic));
        Assert.Equal(4, Enum.GetValues<UpdateMode>().Length); // a new mode must be classified on purpose: this test is where it is noticed
    }

    // Ported from Not_due_makes_no_network_call.
    [Fact]
    public async Task A_scheduled_check_that_is_not_due_asks_nobody()
    {
        _rig.Release();
        Assert.True(_rig.Store.TryWrite(new UpdateState { LastCheckUtc = _rig.Clock.Now.AddHours(-3) }));

        var result = await _rig.Check(UpdateMode.Notify);

        Assert.Equal(UpdateCheckKind.NoCandidate, result.Kind);
        Assert.False(result.Fetched);
        AssertSilent();
        Assert.Equal(_rig.Clock.Now.AddHours(-3), _rig.State().LastCheckUtc); // "I looked" is not rewritten by a look that did not look
    }

    [Fact]
    public async Task A_rate_limit_that_is_being_waited_out_asks_nobody_not_even_for_a_manual_check()
    {
        _rig.Release();
        var until = _rig.Clock.Now.AddHours(5);
        Assert.True(_rig.Store.TryWrite(new UpdateState { LastCheckUtc = _rig.Clock.Now.AddDays(-3), BackoffUntilUtc = until }));

        foreach (var manual in new[] { false, true })
        {
            var result = await _rig.Check(UpdateMode.Notify, manual);
            Assert.Equal(UpdateCheckKind.Waiting, result.Kind);
            Assert.Equal(until, result.WaitUntil);
            Assert.False(result.Fetched);
        }

        AssertSilent();
    }

    [Fact]
    public async Task A_wait_that_is_longer_than_a_week_is_not_believed_and_does_not_block_for_ever()
    {
        // A damaged or edited state file must not switch the feed off for years: past the cap the wait is ignored and the check goes on.
        _rig.Release();
        Assert.True(_rig.Store.TryWrite(new UpdateState { BackoffUntilUtc = _rig.Clock.Now.AddDays(400) }));

        var result = await _rig.Check(UpdateMode.Notify);

        Assert.Equal(UpdateCheckKind.Candidate, result.Kind);
    }

    [Fact]
    public async Task While_another_check_holds_check_lock_this_one_asks_nobody()
    {
        _rig.Release();
        Directory.CreateDirectory(_rig.StateFolder);
        Assert.Equal(LockOutcome.Acquired, _rig.Store.TryLock(out var held));
        using (held)
        {
            var result = await _rig.Check(UpdateMode.Notify, manual: true);
            Assert.Equal(UpdateCheckKind.Refused, result.Kind);
            Assert.Equal(UpdateRefusal.AnotherCheckRunning, result.Refusal);
            Assert.False(result.Fetched);
        }

        AssertSilent();
    }

    [Fact]
    public async Task A_state_folder_that_cannot_be_made_stops_the_check_before_any_request()
    {
        // A file where the folder should be: neither the lock nor the state can be had, so the check could not remember that it ran, so it does not run.
        _rig.Release();
        Directory.CreateDirectory(_rig.Temp);
        File.WriteAllText(_rig.StateFolder, "a file in the way");

        var result = await _rig.Check(UpdateMode.Notify);

        Assert.Equal(UpdateRefusal.StateUnavailable, result.Refusal);
        AssertSilent();
    }

    [Fact]
    public async Task A_state_file_that_cannot_be_written_stops_the_check_before_any_request()
    {
        // The state "I looked now" is written before the request. state.json being a folder makes the write fail, and then nothing is asked.
        _rig.Release();
        Directory.CreateDirectory(Path.Combine(_rig.StateFolder, UpdateStateStore.StateFileName));

        var result = await _rig.Check(UpdateMode.Notify);

        Assert.Equal(UpdateRefusal.StateUnavailable, result.Refusal);
        Assert.False(result.Fetched);
        AssertSilent();
    }

    [Fact]
    public async Task A_token_that_is_cancelled_before_the_check_asks_nobody()
    {
        _rig.Release();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rig.Check(UpdateMode.Notify, manual: true, cancelled.Token));

        AssertSilent();
        Assert.False(File.Exists(_rig.Store.StatePath));
    }

    [Fact]
    public void Reading_the_status_asks_nobody_and_a_missing_file_is_an_empty_status()
    {
        var status = _rig.Service().ReadStatus();

        Assert.Equal(UpdateStatus.Empty, status);
        AssertSilent();
        Assert.False(Directory.Exists(_rig.StateFolder));
    }

    [Fact]
    public void Making_a_service_makes_no_client()
    {
        // The HTTP client is made only when a check really goes out (the factory is called inside the check).
        _ = _rig.Service();
        AssertSilent();
    }

    [Fact]
    public async Task The_counters_do_count_so_that_silence_means_something()
    {
        // A counter that cannot move proves nothing: one real check through the fake handler moves both.
        _rig.Release();

        var result = await _rig.Check(UpdateMode.Notify);

        Assert.Equal(UpdateCheckKind.Candidate, result.Kind);
        Assert.Equal(1, _rig.ClientsMade);
        Assert.Equal(_clientsBefore + 1, FeedTraffic.ClientsCreated);
        Assert.Equal(_requestsBefore + 3, FeedTraffic.Requests); // the release, SHA256.txt, the manifest
    }
}
