using System.Diagnostics;
using PcMieru.Core.Collection;
using Xunit.Abstractions;

namespace PcMieru.Core.Tests.Collection;

public sealed class ProcessMetadataProviderTests(ITestOutputHelper output)
{
    private static readonly SignatureInfo Signed = new(SignatureState.SignedEmbedded, "Test Signer");

    private sealed class FakeReader : IFactReader
    {
        private int _reads;

        public Func<ProcessKey, StableFacts> Stable { get; set; } = key => Facts($@"C:\Test\app{key.Pid}.exe");

        public Func<ProcessKey, Value<bool>> Efficiency { get; set; } = _ => Value.Ok(false, "test");

        public int Reads => Volatile.Read(ref _reads);

        public StableFacts ReadStable(ProcessKey key)
        {
            _ = Interlocked.Increment(ref _reads);
            return Stable(key);
        }

        public Value<bool> ReadEfficiency(ProcessKey key) => Efficiency(key);
    }

    private sealed class FakeCatalog(Func<string, string?, SignatureInfo?, KnownProcess?> match) : IKnownProcessCatalog
    {
        public KnownProcess? Match(string imageName, string? path, SignatureInfo? signature) => match(imageName, path, signature);
    }

    private static StableFacts Facts(string? path) => new(
        path is null ? Value.Unsupported<string>("test", "no path") : Value.Ok(path, "test"),
        Value.Ok("app.exe --flag", "test"),
        Value.Ok(@"PC\user", "test"),
        Value.Ok("Product", "test"),
        Value.Ok("Description", "test"),
        Value.Ok("Company", "test"),
        Value.Ok("x64", "test"),
        Cacheable: true);

    private static ProcessKey Key(uint pid) => new(pid, 1_000 + pid);

    private static ProcessMetadataProvider Make(
        FakeReader reader,
        Func<string, Value<SignatureInfo>>? verify = null,
        Func<IKnownProcessCatalog?>? catalog = null,
        Func<ProcessKey, string?>? nameOf = null,
        ServiceMap? services = null,
        TimeSpan? budget = null)
    {
        // Like the real verifier: a finished check is remembered by file, and Peek answers from that memory only.
        var inner = verify ?? (_ => Value.Ok(Signed, "test"));
        var remembered = new System.Collections.Concurrent.ConcurrentDictionary<string, Value<SignatureInfo>>();
        return new(
            reader,
            file =>
            {
                var result = inner(file);
                if (result.IsOk)
                {
                    remembered[file] = result;
                }

                return result;
            },
            services ?? new ServiceMap(() => new Dictionary<uint, string[]>(), TimeSpan.FromHours(1)),
            catalog ?? (() => null),
            nameOf ?? (_ => null),
            budget ?? TimeSpan.FromSeconds(5),
            file => remembered.TryGetValue(file, out var known) ? known : (Value<SignatureInfo>?)null);
    }

    [Fact]
    public async Task Stable_facts_are_read_once_and_then_served_from_the_cache()
    {
        var reader = new FakeReader();
        var provider = Make(reader);

        var first = await provider.GetAsync(Key(10));
        var second = await provider.GetAsync(Key(10));

        Assert.Equal(1, reader.Reads);
        Assert.Equal(@"C:\Test\app10.exe", first.Path.Data);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal("Company", second.Company.Data);
        Assert.Equal(Key(10), second.Key);
        Assert.Equal(SignatureState.SignedEmbedded, second.Signature.Data!.State);
    }

    [Fact]
    public async Task Parallel_callers_share_one_read()
    {
        var release = new ManualResetEventSlim(false);
        var reader = new FakeReader
        {
            Stable = key =>
            {
                release.Wait(5000);
                return Facts(@"C:\Test\slow.exe");
            },
        };
        var provider = Make(reader);

        var all = Enumerable.Range(0, 10).Select(_ => provider.GetAsync(Key(20))).ToList();
        await Task.Delay(150);
        release.Set();
        var results = await Task.WhenAll(all);

        Assert.Equal(1, reader.Reads);
        Assert.All(results, r => Assert.Equal(@"C:\Test\slow.exe", r.Path.Data));
    }

    [Fact]
    public async Task Reads_never_run_more_than_three_at_a_time()
    {
        var reader = new FakeReader
        {
            Stable = key =>
            {
                Thread.Sleep(40);
                return Facts($@"C:\Test\p{key.Pid}.exe");
            },
        };
        var provider = Make(reader);

        var results = await Task.WhenAll(Enumerable.Range(1, 24).Select(i => provider.GetAsync(Key((uint)i))));

        output.WriteLine($"peak concurrent reads: {provider.PeakConcurrentReads}");
        Assert.Equal(24, reader.Reads);
        Assert.InRange(provider.PeakConcurrentReads, 1, ProcessMetadataProvider.MaxConcurrentReads);
        Assert.Equal(24, results.Select(r => r.Path.Data).Distinct().Count());
    }

    [Fact]
    public async Task Signature_checks_never_run_more_than_two_at_a_time_and_share_one_check_per_file()
    {
        var running = 0;
        var peak = 0;
        var checks = 0;
        var provider = Make(
            new FakeReader(),
            verify: file =>
            {
                _ = Interlocked.Increment(ref checks);
                var now = Interlocked.Increment(ref running);
                int seen;
                while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen)
                {
                }

                Thread.Sleep(60);
                _ = Interlocked.Decrement(ref running);
                return Value.Ok(Signed, "test");
            });

        // Eight different files, each asked about by three callers at once.
        var all = Enumerable.Range(1, 8).SelectMany(i => Enumerable.Range(0, 3).Select(_ => provider.GetAsync(Key((uint)i)))).ToList();
        _ = await Task.WhenAll(all);

        output.WriteLine($"signature checks: {checks}, peak concurrent: {peak}");
        Assert.InRange(peak, 1, ProcessMetadataProvider.MaxConcurrentSignatureChecks);
        Assert.Equal(8, checks);
    }

    [Fact]
    public async Task The_cache_is_bounded()
    {
        var provider = Make(new FakeReader { Stable = key => Facts($@"C:\Test\p{key.Pid}.exe") });

        for (var i = 1u; i <= (uint)ProcessMetadataProvider.MaxCachedProcesses + 100; i++)
        {
            _ = await provider.GetAsync(Key(i));
        }

        Assert.InRange(provider.CachedCount, 1, ProcessMetadataProvider.MaxCachedProcesses);
    }

    [Fact]
    public async Task A_process_that_has_gone_answers_Gone_for_everything_and_that_is_remembered()
    {
        var reader = new FakeReader { Stable = _ => StableFacts.AllGone("test", "no such process") };
        var provider = Make(reader);

        var details = await provider.GetAsync(Key(30));
        _ = await provider.GetAsync(Key(30));

        Assert.Equal(ValueStatus.Gone, details.Path.Status);
        Assert.Equal(ValueStatus.Gone, details.CommandLine.Status);
        Assert.Equal(ValueStatus.Gone, details.Owner.Status);
        Assert.Equal(ValueStatus.Gone, details.Product.Status);
        Assert.Equal(ValueStatus.Gone, details.Description.Status);
        Assert.Equal(ValueStatus.Gone, details.Company.Status);
        Assert.Equal(ValueStatus.Gone, details.Architecture.Status);
        Assert.Equal(ValueStatus.Gone, details.EfficiencyMode.Status);
        Assert.Equal(ValueStatus.Gone, details.Signature.Status);
        Assert.Empty(details.ServiceNames);
        Assert.Null(details.Known);
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task A_failed_read_is_reported_but_not_remembered()
    {
        var calls = 0;
        var reader = new FakeReader
        {
            Stable = _ => ++calls == 1 ? throw new IOException("boom") : Facts(@"C:\Test\ok.exe"),
        };
        var provider = Make(reader);

        var first = await provider.GetAsync(Key(40));
        var second = await provider.GetAsync(Key(40));

        Assert.Equal(ValueStatus.Error, first.Path.Status);
        Assert.Equal(ValueStatus.Error, first.Signature.Status);
        Assert.Equal(ValueStatus.Ok, second.Path.Status);
        Assert.Equal(2, reader.Reads);
    }

    [Fact]
    public async Task A_result_marked_not_cacheable_is_asked_again()
    {
        var reader = new FakeReader
        {
            Stable = _ => Facts(@"C:\Test\x.exe") with { Cacheable = false },
        };
        var provider = Make(reader);

        _ = await provider.GetAsync(Key(41));
        _ = await provider.GetAsync(Key(41));

        Assert.Equal(2, reader.Reads);
    }

    [Fact]
    public async Task Efficiency_mode_is_read_every_time_because_it_can_change()
    {
        var next = 0;
        var reader = new FakeReader { Efficiency = _ => Value.Ok(Interlocked.Increment(ref next) % 2 == 1, "test") };
        var provider = Make(reader);

        var first = await provider.GetAsync(Key(50));
        var second = await provider.GetAsync(Key(50));

        Assert.True(first.EfficiencyMode.Data);
        Assert.False(second.EfficiencyMode.Data);
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task A_throwing_efficiency_read_is_a_value_not_an_exception()
    {
        var provider = Make(new FakeReader { Efficiency = _ => throw new InvalidOperationException("boom") });

        var details = await provider.GetAsync(Key(51));

        Assert.Equal(ValueStatus.Error, details.EfficiencyMode.Status);
        Assert.Equal(ValueStatus.Ok, details.Path.Status);
    }

    [Fact]
    public async Task Service_names_come_from_the_service_map_by_pid()
    {
        var services = new ServiceMap(() => new Dictionary<uint, string[]> { [60] = ["Alpha", "Beta"] }, TimeSpan.FromHours(1));
        var provider = Make(new FakeReader(), services: services);

        var hosting = await provider.GetAsync(Key(60));
        var plain = await provider.GetAsync(Key(61));

        Assert.Equal(["Alpha", "Beta"], hosting.ServiceNames.ToArray());
        Assert.Empty(plain.ServiceNames);
    }

    private sealed class NoCacheProvider : IProcessDetailsProvider
    {
        public Task<ProcessDetails> GetAsync(ProcessKey key, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public void A_provider_that_keeps_no_cache_peeks_null_for_every_key_in_order()
    {
        IProcessDetailsProvider provider = new NoCacheProvider();

        var peeked = provider.PeekMany([Key(1), Key(2), Key(3)]);

        Assert.Equal(3, peeked.Count);
        Assert.All(peeked, Assert.Null);
    }

    [Fact]
    public async Task Peeking_an_unknown_process_gives_null_at_once_and_starts_the_read_so_a_later_peek_finds_it()
    {
        var release = new ManualResetEventSlim(false);
        var reader = new FakeReader { Stable = key => { release.Wait(5000); return Facts($@"C:\Test\peek{key.Pid}.exe"); } };
        var provider = Make(reader);

        var clock = Stopwatch.StartNew();
        var first = provider.PeekMany([Key(300)]);
        Assert.True(clock.ElapsedMilliseconds < 1000, "PeekMany waited for the read");
        Assert.Null(Assert.Single(first));

        release.Set();
        ProcessDetails? found = null;
        var deadline = Stopwatch.StartNew();
        while (found is null && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            found = provider.PeekMany([Key(300)])[0];
            await Task.Delay(20);
        }

        Assert.NotNull(found);
        Assert.Equal(@"C:\Test\peek300.exe", found.Path.Data);
        Assert.Equal(1, reader.Reads); // the peeks joined one read, none started a second
    }

    [Fact]
    public async Task Peeking_a_cached_process_returns_its_facts_without_reading_again_and_keeps_the_order()
    {
        var reader = new FakeReader();
        var provider = Make(reader);
        _ = await provider.GetAsync(Key(11));
        _ = await provider.GetAsync(Key(13));
        var before = reader.Reads;

        var peeked = provider.PeekMany([Key(13), Key(12), Key(11)]);

        Assert.Equal(3, peeked.Count);
        Assert.Equal(Key(13), peeked[0]!.Key);
        Assert.Null(peeked[1]); // never asked before: null now, read started in the background
        Assert.Equal(Key(11), peeked[2]!.Key);
        Assert.Equal("Product", peeked[0]!.Product.Data);
        Assert.True(peeked[2]!.Signature.IsOk, peeked[2]!.Signature.Status + " " + peeked[2]!.Signature.Detail);
        Assert.Equal(SignatureState.SignedEmbedded, peeked[2]!.Signature.Data!.State);
        Assert.True(reader.Reads <= before + 1, "the two cached keys cost no read; only the unknown one may be read");
    }

    [Fact]
    public async Task Peeking_does_not_wait_for_a_signature_check_and_shows_it_as_not_collected_until_it_finishes()
    {
        var release = new ManualResetEventSlim(false);
        var provider = Make(new FakeReader(), verify: _ => { release.Wait(10_000); return Value.Ok(Signed, "test"); });
        _ = provider.PeekMany([Key(40)]);
        ProcessDetails? early = null;
        var deadline = Stopwatch.StartNew();
        while (early is null && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            early = provider.PeekMany([Key(40)])[0];
            await Task.Delay(20);
        }

        Assert.NotNull(early);
        Assert.Equal(ValueStatus.NotCollected, early.Signature.Status);
        Assert.Equal(ValueStatus.Ok, early.Path.Status);

        release.Set();
        Value<SignatureInfo> later = early.Signature;
        deadline.Restart();
        while (!later.IsOk && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            later = provider.PeekMany([Key(40)])[0]!.Signature;
            await Task.Delay(20);
        }

        Assert.True(later.IsOk, later.Detail);
    }

    [Fact]
    public async Task Peeking_a_process_that_was_found_gone_says_Gone_and_after_Dispose_everything_is_null()
    {
        var reader = new FakeReader { Stable = key => StableFacts.AllGone("test", "gone") };
        var provider = Make(reader);
        _ = await provider.GetAsync(Key(50));

        var gone = Assert.Single(provider.PeekMany([Key(50)]));
        Assert.Equal(ValueStatus.Gone, gone!.Path.Status);
        Assert.Equal(ValueStatus.Gone, gone.EfficiencyMode.Status);

        provider.Dispose();
        Assert.Null(Assert.Single(provider.PeekMany([Key(50)])));
    }

    [Fact]
    public async Task A_signature_check_that_takes_too_long_is_a_Timeout_and_the_answer_arrives_on_a_later_call()
    {
        var release = new ManualResetEventSlim(false);
        var provider = Make(
            new FakeReader(),
            verify: file =>
            {
                release.Wait(10_000);
                return Value.Ok(Signed, "test");
            },
            budget: TimeSpan.FromMilliseconds(150));

        var first = await provider.GetAsync(Key(70));
        Assert.Equal(ValueStatus.Timeout, first.Signature.Status);
        Assert.Equal(ValueStatus.Ok, first.Path.Status); // the rest does not wait for the signature

        release.Set();
        Value<SignatureInfo> later = default;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            later = (await provider.GetAsync(Key(70))).Signature;
            if (later.IsOk)
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.True(later.IsOk, later.Detail);
    }

    [Fact]
    public async Task A_throwing_verifier_is_a_value_not_an_exception()
    {
        var provider = Make(new FakeReader(), verify: _ => throw new InvalidOperationException("boom"));

        var details = await provider.GetAsync(Key(71));

        Assert.Equal(ValueStatus.Error, details.Signature.Status);
    }

    [Fact]
    public async Task Without_a_known_path_the_signature_says_why_it_was_not_checked()
    {
        var checks = 0;
        var provider = Make(
            new FakeReader { Stable = _ => Facts(null) },
            verify: file =>
            {
                _ = Interlocked.Increment(ref checks);
                return Value.Ok(Signed, "test");
            });

        var details = await provider.GetAsync(Key(72));

        Assert.Equal(ValueStatus.Unsupported, details.Signature.Status);
        Assert.Equal(0, checks);
    }

    [Fact]
    public async Task The_catalog_is_asked_with_the_file_name_the_path_and_the_verified_signature()
    {
        string? seenName = null;
        string? seenPath = null;
        SignatureInfo? seenSignature = null;
        var known = new KnownProcess("test.app", "ja", "en", PathMatches: true, SignerMatches: true);
        var provider = Make(
            new FakeReader(),
            catalog: () => new FakeCatalog((name, path, signature) =>
            {
                seenName = name;
                seenPath = path;
                seenSignature = signature;
                return known;
            }));

        var details = await provider.GetAsync(Key(80));

        Assert.Same(known, details.Known);
        Assert.Equal("app80.exe", seenName);
        Assert.Equal(@"C:\Test\app80.exe", seenPath);
        Assert.Equal(Signed, seenSignature);
    }

    [Fact]
    public async Task When_the_signature_is_unknown_the_catalog_gets_null_not_a_made_up_one()
    {
        SignatureInfo? seen = new(SignatureState.Invalid, "marker");
        var provider = Make(
            new FakeReader(),
            verify: _ => Value.Failed<SignatureInfo>("test", "could not check"),
            catalog: () => new FakeCatalog((_, _, signature) =>
            {
                seen = signature;
                return null;
            }));

        _ = await provider.GetAsync(Key(81));

        Assert.Null(seen);
    }

    [Fact]
    public async Task Without_a_path_the_catalog_gets_the_name_from_the_latest_sample()
    {
        string? seenName = null;
        string? seenPath = "unset";
        var provider = Make(
            new FakeReader { Stable = _ => Facts(null) },
            catalog: () => new FakeCatalog((name, path, _) =>
            {
                seenName = name;
                seenPath = path;
                return null;
            }),
            nameOf: _ => "System");

        _ = await provider.GetAsync(Key(4));

        Assert.Equal("System", seenName);
        Assert.Null(seenPath);
    }

    [Fact]
    public async Task A_catalog_that_throws_or_is_missing_leaves_Known_empty()
    {
        var throwing = Make(new FakeReader(), catalog: () => new FakeCatalog((_, _, _) => throw new InvalidOperationException("broken")));
        var unregistered = Make(new FakeReader(), catalog: () => throw new KeyNotFoundException("no catalog registered"));
        var none = Make(new FakeReader(), catalog: () => null);

        Assert.Null((await throwing.GetAsync(Key(82))).Known);
        Assert.Null((await unregistered.GetAsync(Key(82))).Known);
        Assert.Null((await none.GetAsync(Key(82))).Known);
    }

    [Fact]
    public async Task A_caller_that_gives_up_does_not_cancel_the_read_others_are_waiting_for()
    {
        var release = new ManualResetEventSlim(false);
        var reader = new FakeReader
        {
            Stable = _ =>
            {
                release.Wait(5000);
                return Facts(@"C:\Test\shared.exe");
            },
        };
        var provider = Make(reader);
        using var cts = new CancellationTokenSource(100);

        var impatient = provider.GetAsync(Key(90), cts.Token);
        var patient = provider.GetAsync(Key(90));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);
        release.Set();
        var details = await patient;

        Assert.Equal(@"C:\Test\shared.exe", details.Path.Data);
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task An_already_cancelled_token_throws_before_any_work()
    {
        var reader = new FakeReader();
        var provider = Make(reader);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetAsync(Key(91), cts.Token));

        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task After_Dispose_the_provider_refuses_new_questions()
    {
        var provider = Make(new FakeReader());
        provider.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.GetAsync(Key(92)));
    }
}

public sealed class SystemFactReaderTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_spawned_process_is_described_from_the_real_system()
    {
        using var ping = new SpawnedPing();
        var provider = new ProcessMetadataProvider();

        var d = await provider.GetAsync(ping.Key);

        output.WriteLine($"path={d.Path.Data} | cmd={d.CommandLine.Data} | owner={d.Owner.Data} | arch={d.Architecture.Data}");
        output.WriteLine($"product={d.Product.Data} | description={d.Description.Data} | company={d.Company.Data}");
        output.WriteLine($"efficiency={d.EfficiencyMode.Status}/{d.EfficiencyMode.Data} | signature={d.Signature.Data?.State}/{d.Signature.Data?.Signer} | services={d.ServiceNames.Count}");

        Assert.True(d.Path.IsOk, d.Path.Detail);
        Assert.EndsWith("ping.exe", d.Path.Data, StringComparison.OrdinalIgnoreCase);
        Assert.True(d.CommandLine.IsOk, d.CommandLine.Detail);
        Assert.Contains("127.0.0.1", d.CommandLine.Data);
        Assert.True(d.Owner.IsOk, d.Owner.Detail);
        Assert.Contains(@"\", d.Owner.Data);
        Assert.True(d.Architecture.IsOk, d.Architecture.Detail);
        Assert.Equal("x64", d.Architecture.Data, ignoreCase: true);
        Assert.True(d.Description.IsOk, d.Description.Detail);
        Assert.Contains("Microsoft", d.Company.Data);
        Assert.True(d.Signature.IsOk, d.Signature.Detail);
        Assert.Contains(d.Signature.Data!.State, new[] { SignatureState.SignedEmbedded, SignatureState.SignedCatalog });
        Assert.Contains(d.EfficiencyMode.Status, new[] { ValueStatus.Ok, ValueStatus.Unsupported });
        Assert.Empty(d.ServiceNames);
        Assert.Null(d.Known);
    }

    [Fact]
    public async Task A_process_that_ended_after_it_was_described_keeps_its_facts_but_efficiency_becomes_Gone()
    {
        var ping = new SpawnedPing();
        var provider = new ProcessMetadataProvider();
        var before = await provider.GetAsync(ping.Key);
        Assert.True(before.Path.IsOk);

        ping.Dispose();
        var after = await provider.GetAsync(ping.Key);

        Assert.Equal(before.Path, after.Path); // the cache remembers what was true
        Assert.Equal(ValueStatus.Gone, after.EfficiencyMode.Status);
    }

    [Fact]
    public async Task A_process_that_ended_before_anyone_asked_is_Gone_for_everything()
    {
        var ping = new SpawnedPing();
        var key = ping.Key;
        ping.Dispose();
        var provider = new ProcessMetadataProvider();

        var d = await provider.GetAsync(key);

        Assert.Equal(ValueStatus.Gone, d.Path.Status);
        Assert.Equal(ValueStatus.Gone, d.CommandLine.Status);
        Assert.Equal(ValueStatus.Gone, d.Owner.Status);
        Assert.Equal(ValueStatus.Gone, d.Company.Status);
        Assert.Equal(ValueStatus.Gone, d.Architecture.Status);
        Assert.Equal(ValueStatus.Gone, d.EfficiencyMode.Status);
        Assert.Equal(ValueStatus.Gone, d.Signature.Status);
    }

    [Fact]
    public async Task A_PID_that_now_belongs_to_another_process_is_Gone_not_someone_elses_details()
    {
        using var ping = new SpawnedPing();
        var provider = new ProcessMetadataProvider();

        var d = await provider.GetAsync(new ProcessKey(ping.Pid, ping.Key.CreateTime + 12345));

        Assert.Equal(ValueStatus.Gone, d.Path.Status);
        Assert.Equal(ValueStatus.Gone, d.Signature.Status);
    }

    [Fact]
    public void Without_a_handle_the_path_by_PID_is_used_only_when_the_latest_sample_vouches_for_the_key()
    {
        // The test process stands in for one this user cannot open: ReadWithoutHandle is what runs when OpenProcess says Denied.
        var self = ProcessHandleTests.ProcessKeyOf((uint)Environment.ProcessId);
        var name = Path.GetFileName(Environment.ProcessPath);

        var vouched = new SystemFactReader(k => k == self ? name : null).ReadWithoutHandle(self, "access denied");
        var unvouched = new SystemFactReader(_ => null).ReadWithoutHandle(self, "access denied");
        var noSample = new SystemFactReader(null).ReadWithoutHandle(self, "access denied");

        Assert.True(vouched.Path.IsOk, vouched.Path.Detail);
        Assert.Equal(Environment.ProcessPath, vouched.Path.Data, ignoreCase: true);
        Assert.Equal(ValueStatus.Denied, vouched.Owner.Status);
        Assert.Equal(ValueStatus.Denied, vouched.CommandLine.Status);
        Assert.Equal(ValueStatus.Denied, vouched.Architecture.Status);
        Assert.True(vouched.Cacheable);
        Assert.Equal(ValueStatus.Denied, unvouched.Path.Status);
        Assert.Equal(ValueStatus.Denied, unvouched.Company.Status);
        Assert.Equal(ValueStatus.Denied, noSample.Path.Status);
    }

    [Fact]
    public void A_process_this_user_cannot_open_gets_its_path_only_when_the_latest_sample_vouches_for_it()
    {
        var protectedProcess = FindProcessThatCannotBeOpened();
        if (protectedProcess is not { } found)
        {
            output.WriteLine("no process on this machine refuses OpenProcess for this user (running elevated?), so the Denied path is not exercised here");
            return;
        }

        var key = new ProcessKey(found.Pid, 0);
        var vouched = new SystemFactReader(k => k.Pid == found.Pid ? found.Name : null).ReadStable(key);
        var unvouched = new SystemFactReader(_ => null).ReadStable(key);
        var noSample = new SystemFactReader(null).ReadStable(key);
        output.WriteLine($"{found.Name} ({found.Pid}): vouched path={vouched.Path.Data}, company={vouched.Company.Data}, owner={vouched.Owner.Status}");

        Assert.True(vouched.Path.IsOk, vouched.Path.Detail);
        Assert.EndsWith(".exe", vouched.Path.Data, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ValueStatus.Denied, vouched.Owner.Status);
        Assert.Equal(ValueStatus.Denied, vouched.CommandLine.Status);
        Assert.Equal(ValueStatus.Denied, unvouched.Path.Status);
        Assert.Equal(ValueStatus.Denied, noSample.Path.Status);
        Assert.Equal(ValueStatus.Denied, unvouched.Company.Status);
    }

    [Fact]
    public void The_System_process_has_no_image_file_and_says_so()
    {
        var facts = new SystemFactReader(k => k.Pid == 4 ? "System" : null).ReadStable(ProcessHandleTests.ProcessKeyOf(4));

        output.WriteLine($"System: path={facts.Path.Status} ({facts.Path.Detail}), owner={facts.Owner.Status}");

        Assert.NotEqual(ValueStatus.Ok, facts.Path.Status);
        Assert.NotEqual(ValueStatus.Gone, facts.Path.Status);
        Assert.NotEqual(ValueStatus.Error, facts.Path.Status);
    }

    private static (uint Pid, string Name)? FindProcessThatCannotBeOpened()
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var pid = (uint)process.Id;
                if (pid <= 4)
                {
                    continue;
                }

                if (ProcessHandle.Open(new ProcessKey(pid, 0), out var handle, out _) == OpenOutcome.Denied && ProcessImagePath.Resolve(pid) is not null)
                {
                    return (pid, process.ProcessName + ".exe");
                }

                handle?.Dispose();
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }
}
