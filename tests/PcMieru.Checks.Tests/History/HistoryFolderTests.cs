namespace PcMieru.Checks.Tests;

/// <summary>
/// DESIGN-A §4, §8: a listing that cannot open or enumerate the history folder itself is the one failure that is not about a file
/// (<see cref="CheckHistoryFolderException"/>); a missing folder is an empty history, and a problem with a file is a warning.
/// </summary>
public sealed class HistoryFolderTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    private string Root => Path.Combine(_tmp.Path, "checks");

    public void Dispose() => _tmp.Dispose();

    private static DateTimeOffset T(int seconds) => Samples.Start.AddSeconds(seconds);

    private static Exception Fault(string name) => name switch
    {
        "access" => new UnauthorizedAccessException("denied"),
        "io" => new IOException("device error", unchecked((int)0x80070015)),
        "argument" => new ArgumentException("not a path"),
        "notsupported" => new NotSupportedException("not supported"),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private (Func<Task<CheckHistoryPage>> List, Func<Task<CheckHistoryGetResult>> Get) Open(string side, ICheckHistoryFileSystem files)
    {
        if (side == "store")
        {
            var store = Kit.Store(Root, files: files);
            return (() => store.ListAsync(Kit.All, CancellationToken.None), () => store.GetAsync(Kit.Id(1), CancellationToken.None));
        }

        var history = Kit.ReadOnly(Root, files);
        return (() => history.ListAsync(Kit.All, CancellationToken.None), () => history.GetAsync(Kit.Id(1), CancellationToken.None));
    }

    private (Func<Task<CheckHistoryPage>> List, Func<Task<CheckHistoryGetResult>> Get) OpenOnRealFileSystem(string side, string root)
    {
        if (side == "store")
        {
            var store = Kit.Store(root);
            return (() => store.ListAsync(Kit.All, CancellationToken.None), () => store.GetAsync(Kit.Id(1), CancellationToken.None));
        }

        var history = CheckHistory.OpenReadOnly(root);
        return (() => history.ListAsync(Kit.All, CancellationToken.None), () => history.GetAsync(Kit.Id(1), CancellationToken.None));
    }

    [Theory]
    [InlineData("store", "access")]
    [InlineData("store", "io")]
    [InlineData("store", "argument")]
    [InlineData("store", "notsupported")]
    [InlineData("readonly", "access")]
    [InlineData("readonly", "io")]
    [InlineData("readonly", "argument")]
    [InlineData("readonly", "notsupported")]
    public async Task A_folder_that_cannot_be_enumerated_is_a_folder_failure_from_both_the_listing_and_the_get(string side, string fault)
    {
        Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(10), Kit.Id(1)));
        var injected = Fault(fault);
        var files = new FaultFileSystem { ListFault = _ => injected };
        var (list, get) = Open(side, files);

        var onList = await Assert.ThrowsAsync<CheckHistoryFolderException>(list);
        var onGet = await Assert.ThrowsAsync<CheckHistoryFolderException>(get);

        foreach (var thrown in new[] { onList, onGet })
        {
            Assert.Same(injected, thrown.InnerException);
            Assert.Equal(injected.HResult, thrown.HResult);
            Assert.DoesNotContain(Root, thrown.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(injected.GetType().Name, thrown.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("store")]
    [InlineData("readonly")]
    public async Task A_missing_folder_is_not_a_folder_failure_it_is_an_empty_history(string side)
    {
        var (list, get) = OpenOnRealFileSystem(side, Root);

        var page = await list();
        var found = await get();

        Assert.Empty(page.Entries);
        Assert.Empty(page.Warnings);
        Assert.Equal(CheckHistoryGetStatus.NotFound, found.Status);
        Assert.False(Directory.Exists(Root));
    }

    [Theory]
    [InlineData("store")]
    [InlineData("readonly")]
    public async Task A_history_path_that_is_a_file_is_a_folder_failure(string side)
    {
        File.WriteAllText(Root, "this is a file, not a folder");
        var (list, get) = OpenOnRealFileSystem(side, Root);

        await Assert.ThrowsAsync<CheckHistoryFolderException>(list);
        await Assert.ThrowsAsync<CheckHistoryFolderException>(get);
        Assert.Equal("this is a file, not a folder", File.ReadAllText(Root));
    }

    [Theory]
    [InlineData("store")]
    [InlineData("readonly")]
    public async Task A_history_path_that_is_not_a_valid_path_is_a_folder_failure(string side)
    {
        var (list, get) = OpenOnRealFileSystem(side, Path.Combine(_tmp.Path, "bad\0name"));

        var thrown = await Assert.ThrowsAsync<CheckHistoryFolderException>(list);
        await Assert.ThrowsAsync<CheckHistoryFolderException>(get);
        Assert.IsAssignableFrom<ArgumentException>(thrown.InnerException);
    }

    [Theory]
    [InlineData("store")]
    [InlineData("readonly")]
    public async Task Problems_with_files_are_warnings_in_a_page_never_a_folder_failure(string side)
    {
        var name = Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(10), Kit.Id(1)));
        Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(5), Kit.Id(2)));
        Kit.PutText(Root, Kit.NameOf(CheckKind.Memory, T(20), Kit.Id(3)), "{ corrupt");
        var files = new FaultFileSystem
        {
            ReadFault = path => Path.GetFileName(path) == name ? Kit.SharingViolation() : null,
        };
        var (list, _) = Open(side, files);

        var page = await list();

        Assert.Equal([Kit.Id(2)], Kit.RunIds(page));
        Assert.Contains(page.Warnings, w => w.Code == "history_locked" && w.Count == 1);
        Assert.Contains(page.Warnings, w => w.Code == "history_unreadable" && w.Count == 1);
    }

    [Fact]
    public async Task A_save_into_a_history_path_that_is_a_file_fails_as_a_value_never_throws()
    {
        File.WriteAllText(Root, "this is a file, not a folder");

        var state = await Kit.Store(Root).SaveAsync(Samples.MemoryCheck());

        Assert.Equal(CheckSaveStatus.Failed, state.Status);
        Assert.Equal(CheckSaveFailureCodes.IoError, state.FailureCode);
        Assert.Equal("this is a file, not a folder", File.ReadAllText(Root));
    }
}
