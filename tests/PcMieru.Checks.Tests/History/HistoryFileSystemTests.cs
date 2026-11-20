namespace PcMieru.Checks.Tests;

/// <summary>
/// The real file system of the history: the final rename (a write-through <c>MoveFileEx</c> that never replaces), the exceptions it
/// maps the Win32 errors to, and the reading side. Temp folders only.
/// </summary>
public sealed class HistoryFileSystemTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    private string Dir => _tmp.Path;

    public void Dispose() => _tmp.Dispose();

    private string P(string name) => Path.Combine(Dir, name);

    private static PhysicalHistoryFileSystem Fs => PhysicalHistoryFileSystem.Instance;

    private static int Low(Exception ex) => ex.HResult & 0xFFFF;

    // ---- Move: rename, never replace ----

    [Fact]
    public void Move_renames_the_file_and_keeps_its_bytes()
    {
        File.WriteAllText(P("a.tmp"), "hello");

        Fs.Move(P("a.tmp"), P("a.json"));

        Assert.False(File.Exists(P("a.tmp")));
        Assert.Equal("hello", File.ReadAllText(P("a.json")));
    }

    [Fact]
    public void Move_to_a_name_that_exists_fails_and_changes_neither_file()
    {
        File.WriteAllText(P("a.tmp"), "new");
        File.WriteAllText(P("a.json"), "OTHER");
        var before = File.GetLastWriteTimeUtc(P("a.json"));

        var thrown = Assert.ThrowsAny<IOException>(() => Fs.Move(P("a.tmp"), P("a.json")));

        Assert.Equal(183, Low(thrown)); // ERROR_ALREADY_EXISTS
        Assert.False(HistoryRetry(thrown));
        Assert.Equal("new", File.ReadAllText(P("a.tmp")));
        Assert.Equal("OTHER", File.ReadAllText(P("a.json")));
        Assert.Equal(before, File.GetLastWriteTimeUtc(P("a.json")));
    }

    [Fact]
    public void Move_of_a_file_that_is_not_there_is_a_file_not_found_and_not_retried()
    {
        var thrown = Assert.Throws<FileNotFoundException>(() => Fs.Move(P("none.tmp"), P("none.json")));

        Assert.Equal(2, Low(thrown));
        Assert.False(HistoryRetry(thrown));
        Assert.DoesNotContain(Dir, thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Move_into_a_folder_that_is_not_there_is_a_directory_not_found()
    {
        File.WriteAllText(P("a.tmp"), "x");

        var thrown = Assert.Throws<DirectoryNotFoundException>(() => Fs.Move(P("a.tmp"), Path.Combine(Dir, "nowhere", "a.json")));

        Assert.Equal(3, Low(thrown));
        Assert.True(File.Exists(P("a.tmp")));
    }

    [Fact]
    public void Move_of_a_file_that_another_process_holds_without_sharing_delete_is_a_sharing_violation_that_is_retried()
    {
        File.WriteAllText(P("held.tmp"), "x");

        IOException thrown;
        using (new FileStream(P("held.tmp"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            thrown = Assert.ThrowsAny<IOException>(() => Fs.Move(P("held.tmp"), P("held.json")));
        }

        Assert.Equal(32, Low(thrown));
        Assert.True(HistoryRetry(thrown));
        Assert.True(File.Exists(P("held.tmp")));
        Assert.False(File.Exists(P("held.json")));

        // Once it is released the same move works.
        Fs.Move(P("held.tmp"), P("held.json"));
        Assert.True(File.Exists(P("held.json")));
    }

    [Fact]
    public void Move_handles_paths_with_Japanese_characters_and_spaces()
    {
        var folder = Path.Combine(Dir, "履歴 フォルダ");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "結果 1.tmp"), "内容");

        Fs.Move(Path.Combine(folder, "結果 1.tmp"), Path.Combine(folder, "結果 1.json"));

        Assert.Equal("内容", File.ReadAllText(Path.Combine(folder, "結果 1.json")));
        Assert.False(File.Exists(Path.Combine(folder, "結果 1.tmp")));
    }

    private static bool HistoryRetry(Exception ex) => CheckHistoryStore.IsTransientMoveFailure(ex);

    // ---- the Win32 error to exception mapping ----

    [Theory]
    [InlineData(2, typeof(FileNotFoundException), false, false)]
    [InlineData(3, typeof(DirectoryNotFoundException), false, false)]
    [InlineData(5, typeof(UnauthorizedAccessException), true, false)]
    [InlineData(32, typeof(IOException), true, false)]
    [InlineData(39, typeof(IOException), false, true)]
    [InlineData(112, typeof(IOException), false, true)]
    [InlineData(183, typeof(IOException), false, false)]
    [InlineData(1117, typeof(IOException), false, false)]
    public void A_win32_error_becomes_the_exception_the_file_apis_of_dotnet_would_throw(int error, Type type, bool transient, bool diskFull)
    {
        var ex = HistoryNative.ToException(error);

        Assert.Equal(type, ex.GetType());
        Assert.Equal(error, Low(ex));
        Assert.Equal(transient, CheckHistoryStore.IsTransientMoveFailure(ex));
        Assert.Equal(diskFull, ex is IOException io && CheckHistoryStore.IsDiskFull(io));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(5, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(39, false)]
    [InlineData(112, false)]
    [InlineData(183, false)]
    [InlineData(0, false)]
    public void Only_a_sharing_violation_and_an_access_denied_are_transient_whatever_the_high_word(int low, bool transient)
    {
        Assert.Equal(transient, CheckHistoryStore.IsTransientMoveFailure(new IOException("x", unchecked((int)(0x80070000u | (uint)low)))));
        Assert.Equal(transient, CheckHistoryStore.IsTransientMoveFailure(new IOException("x", low)));
    }

    [Fact]
    public void An_exception_that_is_neither_io_nor_access_is_never_transient()
    {
        Assert.False(CheckHistoryStore.IsTransientMoveFailure(new InvalidOperationException("x") { HResult = 32 }));
        Assert.False(CheckHistoryStore.IsTransientMoveFailure(new ArgumentException("x") { HResult = 5 }));
        Assert.True(CheckHistoryStore.IsTransientMoveFailure(new UnauthorizedAccessException("x")));
    }

    // ---- creating, deleting ----

    [Fact]
    public void CreateNew_of_a_name_that_exists_is_an_io_exception_and_leaves_the_file_alone()
    {
        File.WriteAllText(P("x.tmp"), "KEEP");

        Assert.ThrowsAny<IOException>(() => Fs.CreateNew(P("x.tmp")));

        Assert.Equal("KEEP", File.ReadAllText(P("x.tmp")));
    }

    [Fact]
    public void CreateNew_writes_flushes_and_closes_and_the_file_is_then_readable()
    {
        using (var file = Fs.CreateNew(P("x.tmp")))
        {
            file.Write("abc"u8);
            file.FlushToDisk();
        }

        Assert.Equal("abc", File.ReadAllText(P("x.tmp")));
    }

    [Fact]
    public void Delete_of_a_file_that_is_not_there_is_not_an_error()
    {
        Fs.Delete(P("none.tmp"));
        File.WriteAllText(P("x.tmp"), "x");
        Fs.Delete(P("x.tmp"));
        Assert.False(File.Exists(P("x.tmp")));
    }

    // ---- the reading side (the only one the read-only history has) ----

    [Fact]
    public void The_reader_lists_files_only_and_an_absent_folder_is_an_empty_list()
    {
        File.WriteAllText(P("a.json"), "x");
        Directory.CreateDirectory(P("sub"));
        File.WriteAllText(Path.Combine(P("sub"), "b.json"), "x");

        var entries = PhysicalHistoryFileReader.Instance.ListFiles(Dir);

        Assert.Equal(["a.json"], entries.Select(e => e.Name).ToArray());
        Assert.Equal(DateTimeKind.Utc, entries[0].LastWriteUtc.Kind);
        Assert.Empty(PhysicalHistoryFileReader.Instance.ListFiles(P("absent")));
    }

    [Fact]
    public void The_reader_does_not_take_a_file_for_an_empty_folder()
    {
        File.WriteAllText(P("afile"), "x");

        Assert.ThrowsAny<IOException>(() => PhysicalHistoryFileReader.Instance.ListFiles(P("afile")));
    }

    [Fact]
    public void The_reader_reads_a_file_another_reader_has_open_but_not_one_held_exclusively()
    {
        File.WriteAllText(P("a.json"), "abc");

        using (new FileStream(P("a.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal("abc"u8.ToArray(), PhysicalHistoryFileReader.Instance.ReadAllBytes(P("a.json"), 100));
        }

        using (new FileStream(P("a.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var thrown = Assert.ThrowsAny<IOException>(() => PhysicalHistoryFileReader.Instance.ReadAllBytes(P("a.json"), 100));
            Assert.Equal(32, Low(thrown));
        }
    }

    [Fact]
    public void The_reader_refuses_a_file_longer_than_the_limit_and_reports_a_file_that_is_gone()
    {
        File.WriteAllText(P("big.json"), new string('x', 101));

        Assert.Throws<InvalidDataException>(() => PhysicalHistoryFileReader.Instance.ReadAllBytes(P("big.json"), 100));
        Assert.Equal(101, PhysicalHistoryFileReader.Instance.ReadAllBytes(P("big.json"), 101).Length);
        Assert.Throws<FileNotFoundException>(() => PhysicalHistoryFileReader.Instance.ReadAllBytes(P("gone.json"), 100));
        Assert.Throws<DirectoryNotFoundException>(() => PhysicalHistoryFileReader.Instance.ReadAllBytes(Path.Combine(Dir, "nowhere", "gone.json"), 100));
    }

    [Fact]
    public void The_reader_can_read_a_file_that_is_renamed_while_it_is_open()
    {
        // Share Delete: a quarantine or a retention in another process may rename or delete the file during a read.
        File.WriteAllText(P("a.json"), "abc");

        using var open = new FileStream(P("a.json"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        File.Move(P("a.json"), P("a.json.bad-x"));

        Assert.Equal("abc", File.ReadAllText(P("a.json.bad-x")));
    }
}
