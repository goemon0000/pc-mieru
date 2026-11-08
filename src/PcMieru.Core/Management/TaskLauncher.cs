using System.ComponentModel;
using System.Diagnostics;
using PcMieru.Wire;

namespace PcMieru.Core.Management;

/// <summary>Starts a program through the shell. A seam so the parsing and the error mapping are tested without starting anything.</summary>
internal interface IShellExecutor
{
    /// <summary>Returns 0 when it was started, otherwise the Win32 error. <paramref name="elevated"/> uses the "runas" verb (a UAC prompt when not already elevated).</summary>
    int Start(string file, string arguments, bool elevated);
}

/// <summary>
/// "Run new task": what the Run box (Win+R) does with the text, started from PC Mieru. An elevated run is
/// <c>runas</c> of THIS process, never the Helper: the Helper only does the fixed operations it has names for.
/// </summary>
public sealed class TaskLauncher : ITaskLauncher
{
    private const int MaxShownLength = 120;
    private const int ErrorInvalidParameter = 87;

    private readonly IShellExecutor _executor;
    private readonly Func<string, bool> _pathExists;

    public TaskLauncher()
        : this(new SystemShellExecutor(), PathExists)
    {
    }

    internal TaskLauncher(IShellExecutor executor, Func<string, bool> pathExists)
    {
        _executor = executor;
        _pathExists = pathExists;
    }

    public OperationResult Run(string commandLine, bool elevated)
    {
        var text = (commandLine ?? "").Trim();
        if (text.Length == 0)
        {
            return OperationMapper.ToResult(OperationCode.InvalidArguments, MessageKeys.TaskEmpty);
        }

        var expanded = Environment.ExpandEnvironmentVariables(text);
        var (file, arguments) = Split(expanded, _pathExists);
        var shown = Shorten(text);

        var error = _executor.Start(file, arguments, elevated);
        return error switch
        {
            0 => OperationMapper.ToResult(OperationCode.Ok, MessageKeys.TaskStarted, null, ("command", shown)),
            2 or 3 => OperationMapper.ToResult(OperationCode.NotFound, MessageKeys.TaskNotFound, null, ("command", shown)),
            ManagementNative.ERROR_CANCELLED => OperationMapper.FromHelper(new HelperOutcome(OperationCode.Cancelled, true, null)),
            5 => OperationMapper.ToResult(OperationCode.Denied, MessageKeys.TaskFailed, null, ("code", "5")),
            ErrorInvalidParameter => OperationMapper.ToResult(OperationCode.InvalidArguments, MessageKeys.TaskFailed, null, ("code", "87")),
            _ => OperationMapper.ToResult(
                OperationCode.Error, MessageKeys.TaskFailed, null, ("code", error.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        };
    }

    /// <summary>
    /// Program and arguments, the way the Run box would read them: a quoted program; else the whole text when it is itself
    /// an existing path; else the SHORTEST leading part that is an existing file (also with ".exe"), so that
    /// "C:\Program Files\x\y.exe /a" works unquoted; else the first word (the shell finds it on PATH or in App Paths).
    /// </summary>
    internal static (string File, string Arguments) Split(string text, Func<string, bool> pathExists)
    {
        text = text.Trim();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            if (close > 0)
            {
                return (text[1..close], text[(close + 1)..].Trim());
            }

            return (text[1..], "");
        }

        if (pathExists(text))
        {
            return (text, "");
        }

        for (var i = text.IndexOf(' '); i > 0; i = text.IndexOf(' ', i + 1))
        {
            var head = text[..i];
            if (pathExists(head) || pathExists(head + ".exe"))
            {
                return (head, text[(i + 1)..].Trim());
            }
        }

        var space = text.IndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
    }

    private static string Shorten(string text) =>
        text.Length <= MaxShownLength ? text : text[..(MaxShownLength - 3)] + "...";

    private static bool PathExists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

internal sealed class SystemShellExecutor : IShellExecutor
{
    public int Start(string file, string arguments, bool elevated)
    {
        try
        {
            var info = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            if (elevated)
            {
                info.Verb = "runas";
            }

            using var process = Process.Start(info);
            return 0;
        }
        catch (Win32Exception ex)
        {
            return ex.NativeErrorCode == 0 ? 1 : ex.NativeErrorCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or PlatformNotSupportedException)
        {
            return 87;
        }
    }
}
