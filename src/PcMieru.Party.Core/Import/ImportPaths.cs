namespace PcMieru.Party.Import;

/// <summary>Which old product folder a candidate save lives in.</summary>
public enum ImportOrigin
{
    /// <summary><c>%APPDATA%\PartsLife\state.json</c>: the name of Parts Life.</summary>
    PartsLife,

    /// <summary><c>%APPDATA%\PartsParty\state.json</c>: the older name of the same product.</summary>
    PartsParty,
}

/// <summary>One place the old product may have left its save.</summary>
public sealed record ImportCandidate(ImportOrigin Origin, string Path);

/// <summary>
/// Where the import reads and writes (DESIGN-C 4.1 items 1 and 2). Every path is derived from one injected root that stands for
/// <c>%APPDATA%</c>, so tests use a temp folder and never reach the real ones. The engine has no default: nothing in the importer
/// resolves a real folder unless the app passes <see cref="ForCurrentUser"/>.
/// </summary>
public sealed record ImportPaths
{
    public const string StateFileName = "state.json";

    public ImportPaths(string roamingRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roamingRoot);
        RoamingRoot = roamingRoot;
    }

    /// <summary>Stands for <c>%APPDATA%</c>.</summary>
    public string RoamingRoot { get; }

    /// <summary><c>Roaming\PCMieru\party</c>: where the converted save, the temp files and the original copy go.</summary>
    public string DestinationDir => Path.Combine(RoamingRoot, "PCMieru", "party");

    /// <summary><c>Roaming\PCMieru\party\state.json</c>.</summary>
    public string Destination => Path.Combine(DestinationDir, StateFileName);

    /// <summary>
    /// The candidates in the order they are looked at: PartsLife first; PartsParty only when PartsLife is really absent
    /// (FileNotFound or DirectoryNotFound). A deny, a sharing violation or an I/O error on the first stops there.
    /// </summary>
    public IReadOnlyList<ImportCandidate> Candidates =>
    [
        new(ImportOrigin.PartsLife, Path.Combine(RoamingRoot, "PartsLife", StateFileName)),
        new(ImportOrigin.PartsParty, Path.Combine(RoamingRoot, "PartsParty", StateFileName)),
    ];

    /// <summary>The real <c>%APPDATA%</c> of this user. For the apps; tests never call it.</summary>
    public static ImportPaths ForCurrentUser() =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
}
