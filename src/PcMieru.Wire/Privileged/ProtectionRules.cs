using System.Text.Json.Serialization;
namespace PcMieru.Wire;

[JsonConverter(typeof(CamelCaseEnumConverter<ProtectionOutcome>))]
public enum ProtectionOutcome
{
    /// <summary>Nothing found that protects the process, and every question was answered.</summary>
    Allowed,

    /// <summary>At least one rule says hands off.</summary>
    Protected,

    /// <summary>A question could not be answered. Treated like <see cref="Protected"/>: nothing is offered.</summary>
    Unknown,
}

[JsonConverter(typeof(CamelCaseEnumConverter<ProtectionReason>))]
public enum ProtectionReason
{
    None,

    /// <summary>PID 0 or 4.</summary>
    SystemProcess,

    /// <summary>PC Mieru itself (GUI, CLI, Helper, SensorHost).</summary>
    Self,

    /// <summary>IsProcessCritical: terminating it blue-screens Windows.</summary>
    Critical,

    /// <summary>GetProcessInformation(ProcessProtectionLevelInfo) is not PROTECTION_LEVEL_NONE.</summary>
    ProtectedProcess,

    /// <summary>smss, csrss, wininit, services, lsass, winlogon, fontdrvhost or dwm in the System32 folder.</summary>
    WindowsCore,

    /// <summary>A question (critical / protection level / image path) failed.</summary>
    CannotConfirm,
}

public readonly record struct ProtectionAssessment(ProtectionOutcome Outcome, ProtectionReason Reason)
{
    public bool MayOperate => Outcome == ProtectionOutcome.Allowed;
}

/// <summary>
/// What was learned about a process from one open handle. null = the question failed (not "no").
/// </summary>
public sealed record ProcessFacts(uint Pid, string? ImagePath, bool? IsCritical, bool? HasProtection);

/// <summary>Who "we" are: the running process and the folder our exes live in.</summary>
public sealed record SelfIdentity(uint Pid, string? Directory);

/// <summary>
/// The decision, free of any API call so it can be tested exhaustively. A definite "protected" wins over an
/// unanswered question; only a fully answered "nothing protects it" allows the operation (fail closed).
/// </summary>
public static class ProtectionRules
{
    public static readonly IReadOnlyList<string> SelfFileNames =
        ["PCMieru.exe", "pcmi.exe", "PCMieru.Helper.exe", "PCMieru.NetMeter.exe", "PCMieru.SensorHost.exe"];

    public static readonly IReadOnlyList<string> WindowsCoreFileNames =
        ["smss.exe", "csrss.exe", "wininit.exe", "services.exe", "lsass.exe", "winlogon.exe", "fontdrvhost.exe", "dwm.exe"];

    public static ProtectionAssessment Evaluate(ProcessFacts facts, SelfIdentity self, string systemDirectory)
    {
        if (facts.Pid is 0 or 4)
        {
            return new(ProtectionOutcome.Protected, ProtectionReason.SystemProcess);
        }

        if (facts.Pid == self.Pid)
        {
            return new(ProtectionOutcome.Protected, ProtectionReason.Self);
        }

        if (facts.ImagePath is { } path && IsOurFile(path, self))
        {
            return new(ProtectionOutcome.Protected, ProtectionReason.Self);
        }

        if (facts.IsCritical == true)
        {
            return new(ProtectionOutcome.Protected, ProtectionReason.Critical);
        }

        if (facts.HasProtection == true)
        {
            return new(ProtectionOutcome.Protected, ProtectionReason.ProtectedProcess);
        }

        if (facts.ImagePath is { } image && IsWindowsCore(image, systemDirectory))
        {
            return new(ProtectionOutcome.Protected, ProtectionReason.WindowsCore);
        }

        if (facts.IsCritical is null || facts.HasProtection is null || facts.ImagePath is null)
        {
            return new(ProtectionOutcome.Unknown, ProtectionReason.CannotConfirm);
        }

        return new(ProtectionOutcome.Allowed, ProtectionReason.None);
    }

    /// <summary>One of our exes in our own folder. A same-named file elsewhere is not us.</summary>
    public static bool IsOurFile(string imagePath, SelfIdentity self)
    {
        if (string.IsNullOrEmpty(self.Directory))
        {
            return false;
        }

        var name = FileNameOf(imagePath);
        if (name is null || !SelfFileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return SameFolder(imagePath, self.Directory);
    }

    /// <summary>A core binary by its full System32 path, not by name alone (a lookalike elsewhere is just a process).</summary>
    public static bool IsWindowsCore(string imagePath, string systemDirectory)
    {
        var name = FileNameOf(imagePath);
        return name is not null
            && WindowsCoreFileNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            && SameFolder(imagePath, systemDirectory);
    }

    private static string? FileNameOf(string path)
    {
        var slash = path.LastIndexOf('\\');
        var name = slash < 0 ? path : path[(slash + 1)..];
        return name.Length == 0 ? null : name;
    }

    private static bool SameFolder(string imagePath, string directory)
    {
        var slash = imagePath.LastIndexOf('\\');
        if (slash <= 0)
        {
            return false;
        }

        return string.Equals(
            Normalize(imagePath[..slash]),
            Normalize(directory),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string folder)
    {
        var f = folder.TrimEnd('\\');
        return f.StartsWith(@"\\?\", StringComparison.Ordinal) ? f[4..] : f;
    }
}
