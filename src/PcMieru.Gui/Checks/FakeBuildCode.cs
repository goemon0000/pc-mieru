using PcMieru.Checks.Build;
using PcMieru.Core;

namespace PcMieru.Gui;

/// <summary>
/// The configuration code of <c>--fakes</c>, and the screens' tests: the library's own sample machine, nothing laid over it. The plain sample is a
/// made-up PC passed through the real rules; the trimmed one is a large made-up PC that the real rules cut to the length limit, so its notes and its
/// unread categories are what the rules and the readers gave (DESIGN-A §7). The Gui invents no note and no unread entry: a dialog that shows a
/// sentence shows one the library wrote. Nothing here reads or measures.
/// </summary>
internal sealed class FakeBuildCode(SampleVariant variant = SampleVariant.Plain) : IBuildCodeProvider, IFakeImplementation
{
    private readonly IBuildCodeProvider _sample = BuildCodeProviders.Sample(variant);

    /// <summary>The sample this fake gives.</summary>
    public SampleVariant Variant => variant;

    public Task<BuildCodeResult> CreateAsync(CancellationToken ct) => _sample.CreateAsync(ct);
}
