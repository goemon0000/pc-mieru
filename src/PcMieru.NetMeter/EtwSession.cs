using System.Diagnostics;
using System.Runtime.InteropServices;
using PcMieru.Wire;

namespace PcMieru.NetMeter;

/// <summary>What the session is doing right now, as the reply reports it.</summary>
internal readonly record struct EtwStatus(ulong EventsLost, ulong BuffersLost, bool Healthy, uint ErrorCode);

/// <summary>
/// One real-time ETW session named after the user, with Microsoft-Windows-Kernel-Network enabled for the IPv4 and IPv6
/// keywords, read by <c>ProcessTrace</c> on a dedicated thread. Only the send and receive data events of TCP and UDP are
/// counted (<see cref="NetMeterEvents"/>); their process id and byte count are found by property name through TDH, once per
/// (event id, version), never at a fixed offset. An event whose fields cannot be found is counted as undecodable and
/// reported, not guessed.
///
/// The session is a system-wide object that outlives this process if the process is killed. So a start under the exact
/// same name stops the leftover of an earlier meter first, and no other session is ever touched.
///
/// The x64 structures are used at their documented offsets (all of them verified against the SDK headers and against a live
/// trace by the RealMachine tests): EVENT_TRACE_PROPERTIES is 120 bytes, EVENT_TRACE_LOGFILEW 448, EVENT_RECORD 112.
/// </summary>
internal sealed unsafe class EtwSession
{
    // EVENT_TRACE_PROPERTIES (120 bytes) followed by room for the logger name and the file name.
    private const int PropertiesSize = 120;
    private const int NameBytes = 2048;
    private const int BlockSize = PropertiesSize + (2 * NameBytes);

    // Offsets inside EVENT_TRACE_PROPERTIES.
    private const int WnodeBufferSize = 0;
    private const int WnodeClientContext = 40;
    private const int WnodeFlags = 44;
    private const int PropBufferSize = 48;
    private const int PropMinimumBuffers = 52;
    private const int PropMaximumBuffers = 56;
    private const int PropLogFileMode = 64;
    private const int PropFlushTimer = 68;
    private const int PropEventsLost = 88;
    private const int PropRealTimeBuffersLost = 100;
    private const int PropLoggerNameOffset = 116;

    private const uint WnodeFlagTracedGuid = 0x0002_0000;
    private const uint EventTraceRealTimeMode = 0x0000_0100;
    private const uint EventTraceUseMsFlushTimer = 0x0000_0010; // FlushTimer is in milliseconds (Windows 8 and later)

    // EVENT_TRACE_LOGFILEW (448 bytes) and the two fields set in it.
    private const int LogfileSize = 448;
    private const int LogfileLoggerName = 8;
    private const int LogfileProcessTraceMode = 28;
    private const int LogfileEventRecordCallback = 424;
    private const uint ProcessTraceModeRealTime = 0x0000_0100;
    private const uint ProcessTraceModeEventRecord = 0x1000_0000;
    private const uint ProcessTraceModeRawTimestamp = 0x0000_1000; // EVENT_HEADER.TimeStamp stays the raw QueryPerformanceCounter count

    // EVENT_RECORD (112 bytes): the parts of the header and the user data the meter reads.
    private const int RecordHeaderFlags = 4;
    private const int RecordTimeStamp = 16;
    private const int RecordProviderId = 24;
    private const int RecordEventId = 40;
    private const int RecordEventVersion = 42;
    private const int RecordUserDataLength = 86;
    private const int RecordUserData = 96;
    private const ushort HeaderFlag32Bit = 0x0020;

    // TRACE_EVENT_INFO: 112 bytes of header, then 24 bytes per EVENT_PROPERTY_INFO.
    private const int InfoHeaderSize = 112;
    private const int InfoPropertyCount = 100;
    private const int InfoTopLevelPropertyCount = 104;
    private const int PropertyInfoSize = 24;
    private const int MaxTopLevelProperties = 256;
    private const uint MaxInfoSize = 1 << 20;

    private static readonly long LayoutRetryTicks = 10 * Stopwatch.Frequency;

    // The callback is a static function, so the one session's state is static too.
    private static NetMeterCounters? s_counters;
    private static readonly Dictionary<int, LayoutEntry> s_layouts = [];

    private readonly object _gate = new();
    private byte* _properties;
    private byte* _logfile;
    private char* _loggerName;
    private ulong _session;
    private ulong _trace = Native.INVALID_PROCESSTRACE_HANDLE;
    private Thread? _thread;
    private uint _startError;
    private volatile bool _stopping;
    private volatile bool _threadEnded;
    private uint _threadResult;

    /// <summary>The Win32 error that kept the session from starting, or 0. Reported to the front end through the pipe.</summary>
    public uint StartError => _startError;

    public bool Started => _session != 0;

    /// <summary>Starts the session; the result is 0 or the Win32 error, which is also kept as <see cref="StartError"/>.</summary>
    public uint Start(string sessionName, NetMeterCounters counters)
    {
        s_counters = counters;
        _properties = (byte*)NativeMemory.AllocZeroed(BlockSize);
        _logfile = (byte*)NativeMemory.AllocZeroed(LogfileSize);
        _loggerName = (char*)NativeMemory.AllocZeroed((nuint)((sessionName.Length + 1) * sizeof(char)));
        sessionName.AsSpan().CopyTo(new Span<char>(_loggerName, sessionName.Length));

        _startError = StartCore(sessionName);
        if (_startError != 0)
        {
            Stop();
        }

        return _startError;
    }

    /// <summary>Asks the session how many events and buffers it has lost since it started, and whether it is still running.</summary>
    public EtwStatus Query()
    {
        if (_session == 0)
        {
            return new EtwStatus(0, 0, false, _startError != 0 ? _startError : Native.ERROR_OPERATION_ABORTED);
        }

        ulong eventsLost = 0, buffersLost = 0;
        uint error = 0;
        lock (_gate)
        {
            if (_properties is not null && _session != 0)
            {
                Prepare(_properties);
                error = Native.ControlTrace(_session, null, _properties, Native.EVENT_TRACE_CONTROL_QUERY);
                if (error == 0)
                {
                    eventsLost = *(uint*)(_properties + PropEventsLost);
                    buffersLost = *(uint*)(_properties + PropRealTimeBuffersLost);
                }
            }
        }

        if (error == 0 && _threadEnded && !_stopping)
        {
            // The trace stopped delivering although we did not stop it: somebody stopped the session.
            error = _threadResult != 0 ? _threadResult : Native.ERROR_OPERATION_ABORTED;
        }

        return new EtwStatus(eventsLost, buffersLost, error == 0, error);
    }

    /// <summary>Stops this session (never another one), closes the trace and waits for the processing thread. Safe to repeat.</summary>
    public void Stop()
    {
        _stopping = true;
        lock (_gate)
        {
            if (_session != 0 && _properties is not null)
            {
                Prepare(_properties);
                _ = Native.ControlTrace(_session, null, _properties, Native.EVENT_TRACE_CONTROL_STOP);
                _session = 0;
            }
        }

        // Stopping the session makes ProcessTrace drain what is left and return.
        if (_thread is not null)
        {
            _ = _thread.Join(3000);
            _thread = null;
        }

        if (_trace != Native.INVALID_PROCESSTRACE_HANDLE)
        {
            _ = Native.CloseTrace(_trace);
            _trace = Native.INVALID_PROCESSTRACE_HANDLE;
        }

        lock (_gate)
        {
            // The thread has ended (or 3 s have gone by and it is about to): the callback no longer needs the counters.
            s_counters = null;
            NativeMemory.Free(_properties);
            NativeMemory.Free(_logfile);
            NativeMemory.Free(_loggerName);
            _properties = null;
            _logfile = null;
            _loggerName = null;
        }
    }

    /// <summary>Whether a session of this exact name exists (any user, any process). For the tests and diagnostics only.</summary>
    internal static bool Exists(string sessionName)
    {
        var block = (byte*)NativeMemory.AllocZeroed(BlockSize);
        try
        {
            Prepare(block);
            return Native.ControlTrace(0, sessionName, block, Native.EVENT_TRACE_CONTROL_QUERY) == 0;
        }
        finally
        {
            NativeMemory.Free(block);
        }
    }

    private uint StartCore(string sessionName)
    {
        ulong handle = 0;
        Prepare(_properties);
        var error = Native.StartTrace(&handle, sessionName, _properties);
        if (error == Native.ERROR_ALREADY_EXISTS)
        {
            // Our own name only: a meter of this user that was killed left its session behind. Stop that one and start fresh.
            Prepare(_properties);
            _ = Native.ControlTrace(0, sessionName, _properties, Native.EVENT_TRACE_CONTROL_STOP);
            Prepare(_properties);
            error = Native.StartTrace(&handle, sessionName, _properties);
        }

        if (error != 0)
        {
            return error;
        }

        _session = handle;

        var provider = NetMeterEvents.ProviderGuid;
        error = Native.EnableTraceEx2(_session, &provider, Native.EVENT_CONTROL_CODE_ENABLE_PROVIDER, Native.TRACE_LEVEL_VERBOSE, NetMeterEvents.Keywords, 0, 0, 0);
        if (error != 0)
        {
            return error;
        }

        *(nint*)(_logfile + LogfileLoggerName) = (nint)_loggerName;
        *(uint*)(_logfile + LogfileProcessTraceMode) = ProcessTraceModeRealTime | ProcessTraceModeEventRecord | ProcessTraceModeRawTimestamp;
        *(nint*)(_logfile + LogfileEventRecordCallback) = (nint)(delegate* unmanaged<byte*, void>)&OnEvent;
        _trace = Native.OpenTrace(_logfile);
        if (_trace == Native.INVALID_PROCESSTRACE_HANDLE)
        {
            var open = (uint)Marshal.GetLastWin32Error();
            return open != 0 ? open : Native.ERROR_OPERATION_ABORTED;
        }

        _thread = new Thread(ProcessLoop) { IsBackground = true, Name = "ETW consumer" };
        _thread.Start();
        return 0;
    }

    private void ProcessLoop()
    {
        ulong handle = _trace;
        _threadResult = Native.ProcessTrace(&handle, 1, null, null);
        _threadEnded = true;
    }

    /// <summary>Fills the properties block for StartTrace, ControlTrace(QUERY) and ControlTrace(STOP) alike.</summary>
    private static void Prepare(byte* block)
    {
        NativeMemory.Clear(block, BlockSize);
        var processors = Math.Clamp(Environment.ProcessorCount, 1, 32);
        var minimum = Math.Max(4, 2 * processors);
        *(uint*)(block + WnodeBufferSize) = BlockSize;
        *(uint*)(block + WnodeClientContext) = 1; // QueryPerformanceCounter timestamps
        *(uint*)(block + WnodeFlags) = WnodeFlagTracedGuid;
        *(uint*)(block + PropBufferSize) = 64; // KB per buffer
        *(uint*)(block + PropMinimumBuffers) = (uint)minimum;
        *(uint*)(block + PropMaximumBuffers) = (uint)(minimum + 32);
        *(uint*)(block + PropLogFileMode) = EventTraceRealTimeMode | EventTraceUseMsFlushTimer;
        *(uint*)(block + PropFlushTimer) = NetMeterEvents.FlushTimerMilliseconds; // the delivery latency NetMeterCounters.LagTicks covers
        *(uint*)(block + PropLoggerNameOffset) = PropertiesSize;
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(byte* record)
    {
        try
        {
            Handle(record);
        }
        catch (Exception)
        {
            // An exception must never leave an UnmanagedCallersOnly method. The event counts as undecodable.
            try
            {
                s_counters?.NoteUndecodable();
            }
            catch (Exception)
            {
            }
        }
    }

    private static void Handle(byte* record)
    {
        var counters = s_counters;
        if (counters is null || *(Guid*)(record + RecordProviderId) != NetMeterEvents.ProviderGuid)
        {
            return;
        }

        var id = *(ushort*)(record + RecordEventId);
        var version = record[RecordEventVersion];
        if (!NetMeterEvents.TryClassify(id, out var direction))
        {
            return;
        }

        var pointerSize = (*(ushort*)(record + RecordHeaderFlags) & HeaderFlag32Bit) != 0 ? 4 : 8;
        var length = *(ushort*)(record + RecordUserDataLength);
        var data = *(byte**)(record + RecordUserData);
        if (data is null
            || !TryGetLayout(record, id, version, pointerSize, out var layout)
            || !layout.TryRead(new ReadOnlySpan<byte>(data, length), out var pid, out var size))
        {
            counters.NoteUndecodable();
            return;
        }

        // The event's own time (the raw counter, see ProcessTraceModeRawTimestamp), never the moment it is delivered.
        var eventTime = MeterClock.FromQpc(*(long*)(record + RecordTimeStamp));
        counters.Count(direction, pid, size, eventTime);
    }

    private static bool TryGetLayout(byte* record, ushort id, byte version, int pointerSize, out NetMeterPayloadLayout layout)
    {
        var key = (id << 16) | (version << 8) | pointerSize;
        var now = Stopwatch.GetTimestamp();
        if (s_layouts.TryGetValue(key, out var cached))
        {
            if (cached.Ok)
            {
                layout = cached.Layout;
                return true;
            }

            if (now < cached.RetryAt)
            {
                layout = default;
                return false;
            }
        }

        var ok = TryLoadLayout(record, pointerSize, out layout);
        s_layouts[key] = new LayoutEntry(ok, layout, ok ? 0 : now + LayoutRetryTicks);
        return ok;
    }

    private static bool TryLoadLayout(byte* record, int pointerSize, out NetMeterPayloadLayout layout)
    {
        layout = default;
        uint size = 0;
        if (Native.TdhGetEventInformation(record, 0, 0, null, &size) != Native.ERROR_INSUFFICIENT_BUFFER
            || size < InfoHeaderSize
            || size > MaxInfoSize)
        {
            return false;
        }

        var info = (byte*)NativeMemory.Alloc(size);
        try
        {
            if (Native.TdhGetEventInformation(record, 0, 0, info, &size) != 0)
            {
                return false;
            }

            var total = *(uint*)(info + InfoPropertyCount);
            var top = *(uint*)(info + InfoTopLevelPropertyCount);
            if (top > total || top > MaxTopLevelProperties || InfoHeaderSize + ((long)top * PropertyInfoSize) > size)
            {
                return false;
            }

            var properties = new List<NetMeterProperty>((int)top);
            for (var i = 0; i < top; i++)
            {
                var p = info + InfoHeaderSize + (i * PropertyInfoSize);
                var flags = *(uint*)p;
                var nameOffset = *(uint*)(p + 4);
                var inType = *(ushort*)(p + 8);
                var count = *(ushort*)(p + 16);
                var length = *(ushort*)(p + 18);
                properties.Add(new NetMeterProperty(ReadName(info, size, nameOffset), NetMeterTdh.FixedSize(flags, inType, length, count, pointerSize)));
            }

            return NetMeterPayloadLayout.TryCreate(properties, out layout);
        }
        finally
        {
            NativeMemory.Free(info);
        }
    }

    private static string ReadName(byte* info, uint size, uint offset)
    {
        if (offset == 0 || offset >= size)
        {
            return string.Empty;
        }

        var start = (char*)(info + offset);
        var max = (int)((size - offset) / sizeof(char));
        var length = 0;
        while (length < max && start[length] != '\0')
        {
            length++;
        }

        return new string(start, 0, length);
    }

    private readonly record struct LayoutEntry(bool Ok, NetMeterPayloadLayout Layout, long RetryAt);
}
