using PcMieru.Party.Import;

namespace PcMieru.Party.Core.Tests.Import;

/// <summary>The decision of the old-product probe, the mutex look-up, the paths, the shared names, and the structural guard of the engine files.</summary>
public class ImportProbeAndPathsTests
{
    private const string Me = "S-1-5-21-1-2-3-1001";
    private const string Other = "S-1-5-21-1-2-3-1002";

    private static LegacyProcess P(string name, string? sid) => new(Random.Shared.Next(1000, 60000), name, sid);

    private static LegacyRunningState Decide(ResidentMutexState mutex, params LegacyProcess[] processes) =>
        WindowsLegacyRunningProbe.Decide(processes, mutex, Me);

    // ---- Decide ----

    [Fact]
    public void Nothing_running_and_no_mutex_is_NotRunning()
    {
        Assert.Equal(LegacyRunningState.NotRunning, Decide(ResidentMutexState.Absent));
    }

    [Theory]
    [InlineData("PartsLife")]
    [InlineData("PartsParty")]
    [InlineData("partslife")]
    public void An_old_process_of_this_user_is_Running(string name)
    {
        Assert.Equal(LegacyRunningState.Running, Decide(ResidentMutexState.Absent, P(name, Me)));
        Assert.Equal(LegacyRunningState.Running, Decide(ResidentMutexState.Absent, P(name, Me.ToLowerInvariant())));
    }

    [Fact]
    public void An_old_process_of_another_user_whose_owner_can_be_read_is_not_ours()
    {
        Assert.Equal(LegacyRunningState.NotRunning, Decide(ResidentMutexState.Absent, P("PartsLife", Other)));
    }

    [Fact]
    public void An_old_process_whose_owner_cannot_be_read_is_Unknown_unless_one_of_ours_is_found()
    {
        Assert.Equal(LegacyRunningState.Unknown, Decide(ResidentMutexState.Absent, P("PartsLife", null)));
        Assert.Equal(LegacyRunningState.Running, Decide(ResidentMutexState.Absent, P("PartsLife", null), P("PartsParty", Me)));
        Assert.Equal(LegacyRunningState.Running, Decide(ResidentMutexState.Absent, P("PartsParty", Me), P("PartsLife", null)));
    }

    [Fact]
    public void A_process_of_another_name_is_not_the_old_product()
    {
        Assert.Equal(LegacyRunningState.NotRunning, Decide(ResidentMutexState.Absent, P("notepad", Me), P("PartsLifeHelper", Me)));
    }

    [Fact]
    public void A_held_mutex_with_no_Party_of_this_user_is_the_old_product()
    {
        Assert.Equal(LegacyRunningState.Running, Decide(ResidentMutexState.Held));
        Assert.Equal(LegacyRunningState.Running, Decide(ResidentMutexState.Held, P("PCMieru.Party", Other)));
    }

    [Fact]
    public void A_held_mutex_with_our_own_Party_running_is_just_Party()
    {
        Assert.Equal(LegacyRunningState.NotRunning, Decide(ResidentMutexState.Held, P("PCMieru.Party", Me)));
        Assert.Equal(LegacyRunningState.NotRunning, Decide(ResidentMutexState.Held, P("pcmieru.party", Me.ToLowerInvariant())));
    }

    [Fact]
    public void A_Party_whose_owner_cannot_be_read_may_be_the_holder_and_that_is_Unknown()
    {
        Assert.Equal(LegacyRunningState.Unknown, Decide(ResidentMutexState.Held, P("PCMieru.Party", null)));
        Assert.Equal(LegacyRunningState.NotRunning, Decide(ResidentMutexState.Held, P("PCMieru.Party", null), P("PCMieru.Party", Me)));
    }

    [Fact]
    public void An_old_process_of_this_user_is_Running_even_beside_our_own_Party()
    {
        Assert.Equal(LegacyRunningState.Running, Decide(ResidentMutexState.Held, P("PCMieru.Party", Me), P("PartsLife", Me)));
    }

    [Fact]
    public void A_Party_alone_without_the_mutex_is_NotRunning()
    {
        Assert.Equal(LegacyRunningState.NotRunning, Decide(ResidentMutexState.Absent, P("PCMieru.Party", Me)));
    }

    [Fact]
    public void A_mutex_that_cannot_be_looked_at_is_Unknown_unless_a_process_says_Running()
    {
        Assert.Equal(LegacyRunningState.Unknown, Decide(ResidentMutexState.Unknown));
        Assert.Equal(LegacyRunningState.Running, Decide(ResidentMutexState.Unknown, P("PartsLife", Me)));
    }

    // ---- Check (the pieces are given) ----

    [Fact]
    public void Check_uses_the_given_list_and_mutex_look_up()
    {
        var probe = new WindowsLegacyRunningProbe(() => [P("PartsLife", Me)], () => ResidentMutexState.Absent, Me);
        Assert.Equal(LegacyRunningState.Running, probe.Check());
        probe = new WindowsLegacyRunningProbe(() => [], () => ResidentMutexState.Absent, Me);
        Assert.Equal(LegacyRunningState.NotRunning, probe.Check());
    }

    [Fact]
    public void A_look_up_that_throws_is_Unknown_never_NotRunning()
    {
        var listThrows = new WindowsLegacyRunningProbe(() => throw new InvalidOperationException("boom"), () => ResidentMutexState.Absent, Me);
        Assert.Equal(LegacyRunningState.Unknown, listThrows.Check());
        var mutexThrows = new WindowsLegacyRunningProbe(() => [], () => throw new InvalidOperationException("boom"), Me);
        Assert.Equal(LegacyRunningState.Unknown, mutexThrows.Check());
    }

    // ---- the real look-ups, on things the test made ----

    [Fact]
    public void The_owner_of_this_process_is_this_user_and_a_bad_pid_has_no_owner()
    {
        Assert.Equal(PartyMutexNames.CurrentSid(), ProcessOwner.TrySid(Environment.ProcessId), ignoreCase: true);
        Assert.Null(ProcessOwner.TrySid(int.MaxValue));
        Assert.Null(ProcessOwner.TrySid(0));
    }

    [Fact]
    public void The_process_list_names_only_the_three_products()
    {
        // Whatever runs on the machine: only the three names are ever reported, and listing never fails.
        var list = WindowsLegacyRunningProbe.ListProcesses();
        string[] known = ["PartsLife", "PartsParty", "PCMieru.Party"];
        Assert.All(list, p => Assert.Contains(p.Name, known));
    }

    [Fact]
    public void A_mutex_is_looked_at_without_being_taken()
    {
        string name = @"Local\PCMieru.Party.Probe.test." + Guid.NewGuid().ToString("N");
        Assert.Equal(ResidentMutexState.Absent, WindowsLegacyRunningProbe.MutexPresence(name));

        using (var made = new Mutex(false, name))
        {
            Assert.Equal(ResidentMutexState.Held, WindowsLegacyRunningProbe.MutexPresence(name));

            // Looking at it did not take it: another thread can still take it, and while it is taken the look-up still says Held.
            bool taken = false;
            var whileTaken = ResidentMutexState.Absent;
            var other = new Thread(() =>
            {
                taken = made.WaitOne(0);
                whileTaken = WindowsLegacyRunningProbe.MutexPresence(name);
                if (taken) made.ReleaseMutex();
            });
            other.Start();
            other.Join();
            Assert.True(taken);
            Assert.Equal(ResidentMutexState.Held, whileTaken);
        }

        Assert.Equal(ResidentMutexState.Absent, WindowsLegacyRunningProbe.MutexPresence(name));   // the last handle is gone: no object
    }

    // ---- paths ----

    [Fact]
    public void The_candidates_are_PartsLife_first_then_PartsParty_and_the_destination_is_under_PCMieru_party()
    {
        string root = Path.Combine(Path.GetTempPath(), "party-import-paths-test");   // never created: paths only
        var paths = new ImportPaths(root);

        Assert.Equal(root, paths.RoamingRoot);
        Assert.Collection(paths.Candidates,
            c =>
            {
                Assert.Equal(ImportOrigin.PartsLife, c.Origin);
                Assert.Equal(Path.Combine(root, "PartsLife", "state.json"), c.Path);
            },
            c =>
            {
                Assert.Equal(ImportOrigin.PartsParty, c.Origin);
                Assert.Equal(Path.Combine(root, "PartsParty", "state.json"), c.Path);
            });
        Assert.Equal(Path.Combine(root, "PCMieru", "party"), paths.DestinationDir);
        Assert.Equal(Path.Combine(root, "PCMieru", "party", "state.json"), paths.Destination);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_root_is_refused(string root)
    {
        Assert.Throws<ArgumentException>(() => new ImportPaths(root));
    }

    [Fact]
    public void A_null_root_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new ImportPaths(null!));
    }

    // ---- the shared names ----

    [Fact]
    public void The_resident_mutex_name_is_what_the_old_product_and_the_new_window_share()
    {
        // Pinned: the name of the old product's last version. A change here splits the one-resident rule between the products.
        Assert.Equal(@"Global\PCMieru.PartsLife.Resident.v1.c169ebe52e9c0ba4", PartyMutexNames.ResidentNameFor("S-1-5-21-1-2-3-1001"));
        Assert.Equal(@"Global\PCMieru.Party.Import.v1.c169ebe52e9c0ba4", PartyMutexNames.ImportNameFor("S-1-5-21-1-2-3-1001"));
        Assert.NotEqual(PartyMutexNames.ResidentNameFor(Me), PartyMutexNames.ResidentNameFor(Other));
        Assert.Equal(PartyMutexNames.ResidentPrefix + PartyMutexNames.SidHash(Me), PartyMutexNames.ResidentNameFor(Me));
        Assert.Matches("^[0-9a-f]{16}$", PartyMutexNames.SidHash(Me));
    }

    // ---- structural guard: the engine files ----

    private static string ImportSourceDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "PcMieru.Party.Core", "Import")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "repository root not found above " + AppContext.BaseDirectory);
        return Path.Combine(dir!, "src", "PcMieru.Party.Core", "Import");
    }

    private static IEnumerable<(string Name, string Code)> Sources() =>
        Directory.GetFiles(ImportSourceDir(), "*.cs")
            .Select(f => (Path.GetFileName(f), string.Join('\n', File.ReadAllLines(f).Where(l => !l.TrimStart().StartsWith("//")))));

    [Fact]
    public void Only_ImportPaths_resolves_a_real_folder_and_only_it_names_ForCurrentUser()
    {
        string[] realFolder = ["SpecialFolder", "GetFolderPath", "GetEnvironmentVariable", "APPDATA", "LocalApplicationData", "UserProfile", "GetTempPath", "CurrentDirectory"];
        foreach (var (name, code) in Sources())
        {
            if (name == "ImportPaths.cs")
            {
                Assert.Contains("ForCurrentUser", code);
                continue;
            }

            Assert.DoesNotContain("ForCurrentUser", code);
            foreach (string word in realFolder)
                Assert.False(code.Contains(word, StringComparison.Ordinal), $"{name} names {word}");
        }
    }

    [Fact]
    public void Nothing_in_Import_touches_the_registry_starts_a_program_or_ends_one()
    {
        string[] forbidden = ["Registry", "Process.Start", "ProcessStartInfo", ".Kill(", "TerminateProcess", "Environment.Exit", "HttpClient", "Socket", "Console."];
        foreach (var (name, code) in Sources())
            foreach (string word in forbidden)
                Assert.False(code.Contains(word, StringComparison.Ordinal), $"{name} mentions {word}");
    }

    [Fact]
    public void The_old_save_is_only_ever_opened_for_reading()
    {
        // The engine writes into the destination folder only; every write API is aimed at paths built from DestinationDir or Destination.
        string engine = Sources().Single(s => s.Name == "ImportEngine.cs").Code;
        foreach (string write in new[] { "File.WriteAll", "File.Copy", "File.Delete", "File.Move", "FileMode.Create", "FileMode.CreateNew", "FileAccess.Write" })
        {
            foreach (string line in engine.Split('\n').Where(l => l.Contains(write, StringComparison.Ordinal)))
                Assert.False(line.Contains("candidate", StringComparison.OrdinalIgnoreCase) || line.Contains("Candidates", StringComparison.Ordinal), "a write is aimed at a candidate: " + line.Trim());
        }

        string file = Sources().Single(s => s.Name == "SourceFile.cs").Code;
        Assert.DoesNotContain("FileAccess.Write", file);
        Assert.DoesNotContain("FileAccess.ReadWrite", file);
        Assert.DoesNotContain("File.Delete", file);
        Assert.DoesNotContain("File.Move", file);
        Assert.Contains("FileAccess.Read", file);
    }
}
