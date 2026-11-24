using PcMieru.Update.Feed;

namespace PcMieru.Gui.Tests;

/// <summary>What the window does with a stage: only Automatic asks for one, a refusal is shown until a check judges something else, a hand-off reason comes and goes.</summary>
public sealed class UpdateStageControllerTests
{
    private static (UpdateController Controller, FakeUpdateService Fake, List<int> Changes) Make(string mode)
    {
        var fake = new FakeUpdateService(UpdateKit.Running);
        var settings = UpdateKit.Impatient(UpdateKit.Settings(null, mode));
        var controller = new UpdateController(fake, settings, new ManualTimers(), () => fake.Placement, UpdateKit.Running);
        var changes = new List<int>();
        controller.Changed += () => changes.Add(changes.Count);
        controller.Start();
        return (controller, fake, changes);
    }

    [Theory]
    [InlineData("notify")]
    [InlineData("off")]
    [InlineData(null)]
    public async Task Only_Automatic_asks_for_a_stage(string? mode)
    {
        var (controller, fake, _) = Make(mode!);
        fake.Answer = (_, _) => fake.Candidate();
        fake.StageAnswer = () => throw new InvalidOperationException("staged outside Automatic");

        await controller.CheckNowAsync();
        Assert.Equal(0, fake.StageCalls);
        Assert.Null(controller.StageRefusal);
    }

    [Fact]
    public async Task A_refused_stage_is_kept_until_a_check_judges_no_candidate()
    {
        var (controller, fake, changes) = Make("auto");
        fake.Answer = (_, _) => fake.Candidate();
        fake.StageAnswer = () => UpdateStageResult.Refuse(UpdateRefusal.ZipHashMismatch, "bad zip");

        await controller.CheckNowAsync();
        Assert.Equal(1, fake.StageCalls);
        Assert.Equal(UpdateRefusal.ZipHashMismatch, controller.StageRefusal);
        Assert.NotEmpty(changes);

        fake.Answer = (_, _) => UpdateCheck.NoCandidate(fetched: true);
        await controller.CheckNowAsync();
        Assert.Null(controller.StageRefusal);
        Assert.Equal(1, fake.StageCalls);
    }

    [Fact]
    public async Task A_stage_that_was_not_attempted_shows_no_refusal()
    {
        var (controller, fake, _) = Make("auto");
        fake.Answer = (_, _) => fake.Candidate();

        await controller.CheckNowAsync();
        Assert.Equal(1, fake.StageCalls);
        Assert.Null(controller.StageRefusal);
    }

    [Fact]
    public void A_deferral_is_announced_once_and_goes_when_the_mode_is_no_longer_Automatic()
    {
        var (controller, _, changes) = Make("auto");
        var before = changes.Count;

        controller.SetDeferral(new UpdateDeferral(GateHolders.Pcmi));
        controller.SetDeferral(new UpdateDeferral(GateHolders.Pcmi));
        Assert.Equal(before + 1, changes.Count);
        Assert.Equal(GateHolders.Pcmi, controller.Deferral?.Holders);

        Assert.True(controller.ChooseMode(UpdateMode.Notify));
        Assert.Null(controller.Deferral);
    }
}
