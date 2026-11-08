using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PcMieru.Wire;

namespace PcMieru.Core.Management;

internal enum LaunchStatus
{
    /// <summary>The Helper ran and ended; <see cref="LaunchResult.ExitCode"/> is its exit code.</summary>
    Exited,

    /// <summary>The user said no at the UAC prompt (Win32 error 1223).</summary>
    Declined,

    /// <summary>The process could not be started; <see cref="LaunchResult.Error"/> is the Win32 error.</summary>
    StartFailed,

    /// <summary>It was started but did not end within the time allowed. It is NOT killed (it runs elevated, and stopping it half way could leave a change half made).</summary>
    TimedOut,

    /// <summary>The caller gave up while waiting. The Helper is left to finish by itself.</summary>
    Cancelled,

    /// <summary>It was started, but no handle came back to wait on or the exit code could not be read.</summary>
    Unobserved,
}

internal readonly record struct LaunchResult(LaunchStatus Status, int ExitCode = 0, int Error = 0);

/// <summary>Starts the Helper (with the UAC prompt) and waits for it. A seam so the rest can be tested without any prompt.</summary>
internal interface IElevatedLauncher
{
    LaunchResult Run(string exe, string arguments, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// <c>runas</c> through the shell (ShellExecuteEx under Process.Start): one UAC prompt per call. With
/// <paramref name="verb"/> null the same code starts the exe as an ordinary child with no prompt, which is how the one
/// end-to-end test runs the real Helper without elevating.
/// </summary>
internal sealed class SystemElevatedLauncher(string? verb = "runas") : IElevatedLauncher
{
    private const int ErrorCancelled = ManagementNative.ERROR_CANCELLED;
    private const int SliceMs = 250;

    public LaunchResult Run(string exe, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return new LaunchResult(LaunchStatus.Cancelled);
        }

        Process? process;
        try
        {
            var info = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            if (verb is not null)
            {
                info.Verb = verb;
            }

            process = Process.Start(info);
        }
        catch (Win32Exception ex)
        {
            return ex.NativeErrorCode == ErrorCancelled
                ? new LaunchResult(LaunchStatus.Declined, Error: ErrorCancelled)
                : new LaunchResult(LaunchStatus.StartFailed, Error: ex.NativeErrorCode);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new LaunchResult(LaunchStatus.StartFailed, Error: ex.HResult & 0xFFFF);
        }

        if (process is null)
        {
            return new LaunchResult(LaunchStatus.Unobserved);
        }

        using (process)
        {
            try
            {
                var deadline = Stopwatch.StartNew();
                while (!process.WaitForExit(SliceMs))
                {
                    if (ct.IsCancellationRequested)
                    {
                        return new LaunchResult(LaunchStatus.Cancelled);
                    }

                    if (deadline.Elapsed >= timeout)
                    {
                        return new LaunchResult(LaunchStatus.TimedOut);
                    }
                }

                return new LaunchResult(LaunchStatus.Exited, process.ExitCode);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return new LaunchResult(LaunchStatus.Unobserved);
            }
        }
    }
}

/// <summary>
/// The real <see cref="IHelperClient"/>. For each request it makes a fresh nonce and an empty result file in the user's
/// own folder, starts <c>PCMieru.Helper.exe</c> with <c>runas</c> (one UAC prompt, the request fixed on the command
/// line), waits, and reports what happened. The EXIT CODE is the truth about the outcome; the result file is read only
/// for its wording, and only when its nonce and operation are the ones sent. Callers pass a request with a placeholder
/// nonce and result path: both are replaced here.
/// </summary>
public sealed class HelperClient : IHelperClient
{
    /// <summary>Long enough for a service stop plus start with the staged wait; the Helper's own waits are shorter.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private const long MaxResultBytes = 256 * 1024;
    private const int NonceLength = 32;

    private readonly IElevatedLauncher _launcher;
    private readonly string _helperPath;
    private readonly string _resultDirectory;
    private readonly TimeSpan _timeout;

    public HelperClient()
        : this(
            new SystemElevatedLauncher(),
            Path.Combine(AppContext.BaseDirectory, "PCMieru.Helper.exe"),
            Path.Combine(ManagementFolder.Path, "helper"),
            DefaultTimeout)
    {
    }

    internal HelperClient(IElevatedLauncher launcher, string helperPath, string resultDirectory, TimeSpan timeout)
    {
        _launcher = launcher;
        _helperPath = helperPath;
        _resultDirectory = resultDirectory;
        _timeout = timeout;
    }

    public async Task<HelperOutcome> RunAsync(HelperRequest request, CancellationToken ct = default)
    {
        var opName = HelperOperations.Get(request.Op).Name;
        var nonce = RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", NonceLength);

        // Refuse what the Helper would refuse, BEFORE any prompt: a request it cannot accept is not worth a UAC dialog.
        var resultPath = Path.Combine(_resultDirectory, nonce + ".json");
        var fixedRequest = request with { Nonce = nonce, ResultPath = resultPath };
        IReadOnlyList<string> args;
        try
        {
            args = fixedRequest.ToArgs();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Synthetic(opName, nonce, OperationCode.InvalidArguments, ManagementStrings.HelperBadRequest, ex.GetType().Name);
        }

        if (!HelperRequest.TryParse(args, out _, out var parseError))
        {
            return Synthetic(opName, nonce, OperationCode.InvalidArguments, ManagementStrings.HelperBadRequest, parseError);
        }

        if (!File.Exists(_helperPath))
        {
            return Synthetic(opName, nonce, OperationCode.Unsupported, MessageKeys.HelperMissing, "helper not found: " + _helperPath);
        }

        if (ct.IsCancellationRequested)
        {
            return new HelperOutcome(OperationCode.Cancelled, false, null, "cancelled before the prompt");
        }

        var fileError = TryCreateResultFile(resultPath);
        if (fileError != 0)
        {
            // The Helper requires --result and writes only into a file made here, so there is nothing safe to hand over: do not prompt.
            return Synthetic(
                opName, nonce, OperationCode.Error, MessageKeys.HelperStartFailed, "result file could not be created",
                ("code", fileError.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var commandLine = string.Join(' ', args.Select(QuoteArgument));
        try
        {
            // The UAC prompt is modal and answered by the user; it cannot be cancelled from here, so it is not raced against ct.
            var launch = await Task.Run(() => _launcher.Run(_helperPath, commandLine, _timeout, ct), CancellationToken.None).ConfigureAwait(false);
            return Interpret(launch, opName, nonce, resultPath);
        }
        finally
        {
            TryDelete(resultPath);
        }
    }

    private HelperOutcome Interpret(LaunchResult launch, string opName, string nonce, string resultPath)
    {
        switch (launch.Status)
        {
            case LaunchStatus.Declined:
                return new HelperOutcome(OperationCode.Cancelled, true, null, "UAC declined");
            case LaunchStatus.StartFailed:
                return Synthetic(
                    opName, nonce, OperationCode.Error, MessageKeys.HelperStartFailed, "start failed: " + launch.Error,
                    ("code", launch.Error.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            case LaunchStatus.TimedOut:
                return Synthetic(opName, nonce, OperationCode.Error, ManagementStrings.HelperTimeout, "timed out");
            case LaunchStatus.Cancelled:
                return new HelperOutcome(OperationCode.Cancelled, false, null, "cancelled while waiting; the helper was left running");
            case LaunchStatus.Unobserved:
                return new HelperOutcome(OperationCode.Error, false, null, "started, but the result could not be observed");
        }

        if (launch.ExitCode is < 0 or > 255 || !Enum.IsDefined((OperationCode)launch.ExitCode))
        {
            return Synthetic(
                opName, nonce, OperationCode.Error, MessageKeys.HelperUnexpectedExit, "unexpected exit code " + launch.ExitCode,
                ("code", launch.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var code = (OperationCode)launch.ExitCode;
        return new HelperOutcome(code, false, ReadResult(resultPath, nonce, opName), "exit " + launch.ExitCode);
    }

    /// <summary>The Helper's file, or null unless it parses and carries OUR nonce and operation.</summary>
    internal static HelperResult? ReadResult(string path, string nonce, string opName)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxResultBytes)
            {
                return null;
            }

            var text = File.ReadAllText(path, Encoding.UTF8);
            var result = JsonSerializer.Deserialize(text, WireJsonContext.Default.HelperResult);
            return result is not null
                && string.Equals(result.Nonce, nonce, StringComparison.Ordinal)
                && string.Equals(result.Op, opName, StringComparison.Ordinal)
                    ? result
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>An outcome the CLIENT made (the Helper never ran or said nothing usable): the same shape as a Helper result, so callers map it the same way.</summary>
    private static HelperOutcome Synthetic(string opName, string nonce, OperationCode code, string key, string detail, params (string Name, string Value)[] args) =>
        new(code, false, ActionResult.Of(code, key, args).ToHelperResult(nonce, opName), detail);

    /// <summary>Makes the empty file the Helper will write into. Returns 0, or the error (a Win32 code) if it could not be made.</summary>
    private int TryCreateResultFile(string path)
    {
        try
        {
            Directory.CreateDirectory(_resultDirectory);
            TrimStale(_resultDirectory);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (ex.HResult & 0xFFFF) is var code and not 0 ? code : 1;
        }
    }

    /// <summary>Files an earlier run could not delete (the Helper was still holding it). Only our own "&lt;nonce&gt;.json" names, only when old.</summary>
    private static void TrimStale(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (HelperRequest.IsValidNonce(name) && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1))
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed is not worth failing the operation for.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; TrimStale removes it on a later call.
        }
    }

    /// <summary>One argument as CommandLineToArgvW (and the .NET runtime) will read it back: quotes and backslashes before quotes are escaped.</summary>
    internal static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }

        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
                backslashes = 0;
            }
            else
            {
                builder.Append('\\', backslashes).Append(c);
                backslashes = 0;
            }
        }

        return builder.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
