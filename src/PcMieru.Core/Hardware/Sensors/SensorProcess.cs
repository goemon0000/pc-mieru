using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Hardware.Sensors;

/// <summary>The part of a running SensorHost the client needs. A seam so the restart logic can be tested without launching anything.</summary>
internal interface ISensorProcess : IDisposable
{
    /// <summary>The next line of output, or null when the process closed its output.</summary>
    Task<string?> ReadLineAsync(CancellationToken ct);

    bool HasExited { get; }

    int? ExitCode { get; }

    /// <summary>The last few lines the process wrote to its error output (for the reason shown when it fails).</summary>
    string StderrTail { get; }

    /// <summary>Closes standard input. SensorHost treats that as "the parent is gone" and exits by itself.</summary>
    void CloseInput();

    /// <summary>
    /// Asks the running SensorHost to take frames every <paramref name="periodMs"/> ms (1000 or 2000) from its next sampling boundary.
    /// Never waits: the request is handed to a writer and the latest one wins. A request to a process that is gone is dropped.
    /// </summary>
    void SetPeriod(int periodMs);

    bool WaitForExit(int milliseconds);

    void Kill();
}

internal interface ISensorProcessFactory
{
    /// <summary>
    /// Starts a new SensorHost taking frames every <paramref name="intervalMs"/> ms from the start (<c>--interval-ms</c>) and numbering its
    /// frames with <paramref name="generation"/> (<c>--generation</c>). Throws <see cref="FileNotFoundException"/> when the executable is missing.
    /// </summary>
    ISensorProcess Start(int intervalMs, long generation);
}

/// <summary>Launches PCMieru.SensorHost.exe hidden, with its three standard streams redirected. Never through a shell and never elevated.</summary>
internal sealed class SystemSensorProcessFactory(string exePath) : ISensorProcessFactory
{
    public const string ExeName = "PCMieru.SensorHost.exe";

    public static string DefaultPath() => Path.Combine(AppContext.BaseDirectory, ExeName);

    public ISensorProcess Start(int intervalMs, long generation)
    {
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"{ExeName} was not found next to the app", exePath);
        }

        var utf8 = new UTF8Encoding(false);
        var info = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
        };
        info.ArgumentList.Add("--interval-ms");
        info.ArgumentList.Add(intervalMs.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--generation");
        info.ArgumentList.Add(generation.ToString(CultureInfo.InvariantCulture));
        var process = Process.Start(info) ?? throw new Win32Exception("the process did not start");
        return new SystemSensorProcess(process);
    }
}

internal sealed class SystemSensorProcess : ISensorProcess
{
    private const int TailLines = 4;
    private const int TailLineLength = 200;

    private readonly Process _process;
    private readonly Queue<string> _tail = new();
    private readonly object _tailGate = new();
    private int _requestedMs;
    private int _writing;

    public SystemSensorProcess(Process process)
    {
        _process = process;
        // The error pipe must be drained or SensorHost blocks as soon as it fills.
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            lock (_tailGate)
            {
                _tail.Enqueue(e.Data.Length > TailLineLength ? e.Data[..TailLineLength] : e.Data);
                while (_tail.Count > TailLines)
                {
                    _tail.Dequeue();
                }
            }
        };
        process.BeginErrorReadLine();
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public string StderrTail
    {
        get
        {
            lock (_tailGate)
            {
                return string.Join(" | ", _tail);
            }
        }
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct) => await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);

    public void CloseInput()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    public void SetPeriod(int periodMs)
    {
        // Latest wins, and the pipe write happens on a pool thread: a caller (the UI thread releasing a recording lease, the watchdog)
        // never waits on the child's stdin.
        Volatile.Write(ref _requestedMs, periodMs);
        if (Interlocked.Exchange(ref _writing, 1) == 0)
        {
            _ = Task.Run(DrainRequests);
        }
    }

    private void DrainRequests()
    {
        do
        {
            var ms = Interlocked.Exchange(ref _requestedMs, 0);
            if (ms != 0)
            {
                try
                {
                    _process.StandardInput.WriteLine(SensorCadence.Line(ms));
                    _process.StandardInput.Flush();
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    // The child is gone or its input is closed: the watchdog notices that on its own.
                }
            }

            Volatile.Write(ref _writing, 0);
        }
        while (Volatile.Read(ref _requestedMs) != 0 && Interlocked.Exchange(ref _writing, 1) == 0);
    }

    public bool WaitForExit(int milliseconds)
    {
        try
        {
            return _process.WaitForExit(milliseconds);
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    public void Dispose() => _process.Dispose();
}

/// <summary>One stdout line of SensorHost, checked before it is believed.</summary>
internal static class SensorFrameParser
{
    /// <summary>A frame for a handful of GPUs is a few KB. Anything near this size is not a frame.</summary>
    public const int MaxLineChars = 256 * 1024;

    public static bool TryParse(string line, out SensorFrame? frame, out string error) => TryParse(line, out frame, out error, out _);

    /// <param name="incompatible">True when the line is a well-formed frame of ANOTHER schema version: a SensorHost that does not belong to
    /// this app. Retrying cannot help, so the client reports it as a standing failure instead of restarting the child again and again.</param>
    public static bool TryParse(string line, out SensorFrame? frame, out string error, out bool incompatible)
    {
        frame = null;
        error = string.Empty;
        incompatible = false;
        if (line.Length > MaxLineChars)
        {
            error = "an output line was far longer than any reading";
            return false;
        }

        try
        {
            var parsed = PcMieruJson.Deserialize<SensorFrame>(line);
            if (parsed is null)
            {
                error = "the line was empty";
                return false;
            }

            if (parsed.SchemaVersion != SensorFrame.CurrentSchemaVersion)
            {
                error = $"SensorHost speaks schema {parsed.SchemaVersion}, this app expects {SensorFrame.CurrentSchemaVersion}";
                incompatible = true;
                return false;
            }

            // Schema 2 frames always say which acquisition they are. A frame without them is not a schema 2 frame whatever it calls itself.
            if (parsed.Sequence <= 0 || parsed.PeriodMs <= 0 || parsed.MonotonicUs <= 0)
            {
                error = "a SensorHost frame came without its sequence, period or capture time";
                return false;
            }

            frame = parsed;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The line is untrusted input: a bad date makes a converter throw FormatException, not JsonException. Whatever it throws,
            // the line is not a reading and the read loop must carry on with the next one.
            error = "the line was not a SensorHost reading";
            return false;
        }
    }
}
