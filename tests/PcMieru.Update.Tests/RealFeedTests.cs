using PcMieru.Update.Bundle;
using PcMieru.Update.Feed;
using Xunit.Abstractions;

namespace PcMieru.Update.Tests;

/// <summary>
/// The one test that talks to the internet: one real <c>GET https://api.github.com/repos/goemon0000/pc-mieru/releases/latest</c> through the real client (<see cref="UpdateHttp"/>),
/// then SHA256.txt and the outer manifest if the release has something to offer. It prints the decision. It does nothing unless <c>PCMIERU_REAL_FEED=1</c> (so that a run of the whole
/// real-machine category on a workstation does not call GitHub by accident, and so that CI never does: the category is filtered out there anyway).
/// <list type="bullet">
/// <item><c>PCMIERU_REAL_FEED_RUNNING</c>: the version this program pretends to be (default 0.4.0).</item>
/// <item><c>PCMIERU_REAL_FEED_EXPECT</c>: optional, one of <c>no-candidate</c>, <c>candidate</c>, <c>refused:&lt;UpdateRefusal&gt;</c>. When set, the decision must be that.</item>
/// <item><c>PCMIERU_REAL_FEED_OUT</c>: optional, a file the decision line is appended to.</item>
/// </list>
/// The state folder is a temp folder, deleted at the end: the real %LOCALAPPDATA% is not touched. Never a zip: the count of requests is asserted.
/// </summary>
[Trait("Category", "RealMachine")]
[Collection(FeedCollection.Name)]
public sealed class RealFeedTests
{
    private readonly ITestOutputHelper _output;

    public RealFeedTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task The_latest_real_release_is_decided_by_the_real_client()
    {
        if (Environment.GetEnvironmentVariable("PCMIERU_REAL_FEED") != "1")
        {
            return; // not asked for: no request is made, and the test says nothing
        }

        var runningText = Environment.GetEnvironmentVariable("PCMIERU_REAL_FEED_RUNNING") is { Length: > 0 } given ? given : "0.4.0";
        Assert.True(AppVersion.TryParse(runningText, out var running), $"PCMIERU_REAL_FEED_RUNNING='{runningText}' is not X.Y.Z");

        using var temp = new TempFolder();
        var service = new UpdateService(new UpdateServiceOptions { Running = running, StateFolder = temp.Sub("update") });

        var requestsBefore = FeedTraffic.Requests;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var decision = await service.CheckAsync(UpdateMode.Notify, manual: true, timeout.Token);
        var requests = FeedTraffic.Requests - requestsBefore;

        var line = $"real feed, running {running}: {decision} (requests: {requests})";
        _output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("PCMIERU_REAL_FEED_OUT") is { Length: > 0 } outFile)
        {
            File.AppendAllText(outFile, line + Environment.NewLine);
        }

        // The decision is one of the three answers of a check that went out; a wait, "not allowed" or a state problem would mean this test did not test the feed.
        Assert.True(decision.Fetched, "nothing was fetched: " + decision);
        Assert.Contains(decision.Kind, new[] { UpdateCheckKind.NoCandidate, UpdateCheckKind.Candidate, UpdateCheckKind.Refused });

        // The API, then at most SHA256.txt and the manifest. A zip is never asked for.
        Assert.InRange(requests, 1, 3 + UpdateHttp.MaxRedirects * 3);

        if (Environment.GetEnvironmentVariable("PCMIERU_REAL_FEED_EXPECT") is { Length: > 0 } expected)
        {
            var actual = decision.Kind switch
            {
                UpdateCheckKind.NoCandidate => "no-candidate",
                UpdateCheckKind.Candidate => "candidate",
                UpdateCheckKind.Refused => "refused:" + decision.Refusal,
                _ => decision.Kind.ToString(),
            };
            Assert.Equal(expected, actual);
        }
    }
}
