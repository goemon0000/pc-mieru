using System.ComponentModel;
using System.Diagnostics;
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

    bool WaitForExit(int milliseconds);

    void Kill();
}

internal interface ISensorProcessFactory
{
    /// <summary>Starts a new SensorHost. Throws <see cref="FileNotFoundException"/> when the executable is missing.</summary>
    ISensorProcess Start();
}

/// <summary>Launches PCMieru.SensorHost.exe hidden, with its three standard streams redirected. Never through a shell and never elevated.</summary>
internal sealed class SystemSensorProcessFactory(string exePath) : ISensorProcessFactory
{
    public const string ExeName = "PCMieru.SensorHost.exe";

    public static string DefaultPath() => Path.Combine(AppContext.BaseDirectory, ExeName);

    public ISensorProcess Start()
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

    public static bool TryParse(string line, out SensorFrame? frame, out string error)
    {
        frame = null;
        error = string.Empty;
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
                return false;
            }

            frame = parsed;
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            error = "the line was not a SensorHost reading";
            return false;
        }
    }
}
