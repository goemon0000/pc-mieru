using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Collection;

/// <summary>A graphics adapter as DXGI lists it: who it is and how much dedicated memory it has.</summary>
internal sealed record GpuAdapter(ulong Luid, string Name, long DedicatedBytes, uint VendorId, bool IsSoftware)
{
    public string Id => GpuCounterName.AdapterId(Luid);

    /// <summary>
    /// The PCI bus number from the kernel (D3DKMT), asked once per enumeration. Null when the adapter has no PCI address (a virtual copy) or the kernel
    /// could not be asked. Two cards of one model differ only by this (and by a LUID that changes at every boot).
    /// </summary>
    public uint? PciBus { get; init; }

    /// <summary>
    /// A virtual copy of a card that another DXGI entry already stands for (a virtual display driver lists the card again under a LUID of its own, with no PCI
    /// address). It is not a GPU anyone looks at: <see cref="GpuSampler.Describe"/> leaves it out.
    /// </summary>
    public bool IsAlias { get; init; }
}

/// <summary>
/// Adapter identity (name, LUID, dedicated memory) from DXGI, read once when GPU sampling first starts: the PDH counters
/// only know a LUID. The LUID DXGI reports is the one in the PDH instance names (checked on the real machine); anything
/// PDH reports that DXGI does not list is shown under its LUID instead of being guessed at.
/// </summary>
internal static unsafe partial class GpuAdapters
{
    private const uint SoftwareFlag = 2;
    private const int Desc1Size = 312;
    private const int NameChars = 128;
    private const int VendorOffset = 256;
    private const int DedicatedOffset = 272;
    private const int LuidOffset = 296;
    private const int FlagsOffset = 304;
    private const int NotFound = unchecked((int)0x887A0002);

    private static readonly Guid FactoryIid = new(0x770aae78, 0xf26f, 0x4dba, 0xa8, 0x29, 0x25, 0x3c, 0x83, 0xd1, 0xb3, 0x87);

    [LibraryImport("dxgi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

    /// <summary>All adapters DXGI lists. Empty (never an exception) when DXGI is missing or fails.</summary>
    public static IReadOnlyList<GpuAdapter> Enumerate() => EnumerateChecked(out _);

    /// <summary>
    /// <see cref="Enumerate"/>, and why it came back short. <paramref name="failure"/> is null when DXGI answered to the end (an empty list then really
    /// means "no adapter"); otherwise it says what failed, so a failed enumeration is not taken for a machine with no GPU.
    /// </summary>
    internal static IReadOnlyList<GpuAdapter> EnumerateChecked(out string? failure)
    {
        failure = null;
        var result = new List<GpuAdapter>();
        nint factory = 0;
        try
        {
            var created = CreateDXGIFactory1(FactoryIid, out factory);
            if (created < 0 || factory == 0)
            {
                failure = $"CreateDXGIFactory1 failed (0x{created:X8})";
                return result;
            }

            // IDXGIFactory1 vtable: IUnknown (0..2), IDXGIObject (3..6), IDXGIFactory (7..11), EnumAdapters1 = 12.
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)factory)[12];
            Span<byte> desc = stackalloc byte[Desc1Size];
            for (uint i = 0; i < 64; i++)
            {
                nint adapter;
                var hr = enumAdapters1(factory, i, &adapter);
                if (hr == NotFound)
                {
                    break;
                }

                if (hr < 0)
                {
                    failure = $"IDXGIFactory1::EnumAdapters1({i}) failed (0x{hr:X8})";
                    break;
                }

                try
                {
                    // IDXGIAdapter1 vtable: IUnknown (0..2), IDXGIObject (3..6), IDXGIAdapter (7..9), GetDesc1 = 10.
                    var getDesc1 = (delegate* unmanaged[Stdcall]<nint, byte*, int>)(*(nint**)adapter)[10];
                    fixed (byte* d = desc)
                    {
                        var described = getDesc1(adapter, d);
                        if (described >= 0)
                        {
                            result.Add(ParseDesc1(desc));
                        }
                        else
                        {
                            failure ??= $"IDXGIAdapter1::GetDesc1 failed for adapter {i} (0x{described:X8})";
                        }
                    }
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            result.Clear();
            failure = $"DXGI could not be loaded ({ex.GetType().Name})";
        }
        finally
        {
            if (factory != 0)
            {
                Release(factory);
            }
        }

        return Resolve(result, static a => Dxgi.QueryKmt((uint)a.Luid, (int)(a.Luid >> 32)));
    }

    /// <summary>
    /// Asks the kernel once per adapter, then does two things with the one answer: puts the bus on the adapter (only when the kernel gave a real PCI
    /// address) and marks the aliases (<see cref="MarkAliases"/>). One question per adapter per enumeration, never one per tick.
    /// </summary>
    internal static List<GpuAdapter> Resolve(IReadOnlyList<GpuAdapter> listed, Func<GpuAdapter, KmtAdapterInfo?> query)
    {
        var address = new bool?[listed.Count];
        var withBus = new List<GpuAdapter>(listed.Count);
        for (var i = 0; i < listed.Count; i++)
        {
            var kmt = query(listed[i]);
            address[i] = kmt?.HasPciAddress;
            withBus.Add(kmt is { HasPciAddress: true } ? listed[i] with { PciBus = kmt.Bus } : listed[i]);
        }

        return MarkAliases(withBus, address);
    }

    /// <summary>
    /// Marks the adapters that are only a virtual copy of another one: of the adapters with no PCI address, one whose dedicated memory is the same as an adapter that
    /// has one, and whose name is the same (or empty), is an alias. Two cards of one model that are both plugged in both have an address, so neither is marked.
    /// The same rule the configuration check uses for the graphics cards (PcMieru.Checks), kept as its own copy for the same reason: what the recorder lists must not
    /// change when a screen elsewhere changes its mind.
    /// </summary>
    /// <param name="hasPciAddress">True or false from the kernel driver; null when it could not be read. Unknown is never taken for "no address": an adapter that cannot be asked is not called an alias.</param>
    internal static List<GpuAdapter> MarkAliases(IReadOnlyList<GpuAdapter> adapters, Func<GpuAdapter, bool?> hasPciAddress)
    {
        var address = new bool?[adapters.Count];
        for (var i = 0; i < adapters.Count; i++)
        {
            address[i] = hasPciAddress(adapters[i]);
        }

        return MarkAliases(adapters, address);
    }

    private static List<GpuAdapter> MarkAliases(IReadOnlyList<GpuAdapter> adapters, bool?[] address)
    {
        var result = new List<GpuAdapter>(adapters.Count);
        for (var i = 0; i < adapters.Count; i++)
        {
            var a = adapters[i];
            var alias = false;
            if (address[i] == false)
            {
                for (var j = 0; j < adapters.Count && !alias; j++)
                {
                    var b = adapters[j];
                    alias = j != i
                        && address[j] == true
                        && b.DedicatedBytes == a.DedicatedBytes
                        && (a.Name.Length == 0 || string.Equals(a.Name, b.Name, StringComparison.Ordinal));
                }
            }

            result.Add(alias ? a with { IsAlias = true } : a);
        }

        return result;
    }

    /// <summary>DXGI_ADAPTER_DESC1 (x64): Description[128] WCHAR, VendorId at 256, DedicatedVideoMemory at 272, AdapterLuid at 296, Flags at 304.</summary>
    internal static GpuAdapter ParseDesc1(ReadOnlySpan<byte> desc)
    {
        var chars = MemoryMarshal.Cast<byte, char>(desc[..(NameChars * 2)]);
        var end = chars.IndexOf('\0');
        var name = (end < 0 ? chars : chars[..end]).ToString().Trim();
        var vendor = BinaryPrimitives.ReadUInt32LittleEndian(desc[VendorOffset..]);
        var dedicated = BinaryPrimitives.ReadUInt64LittleEndian(desc[DedicatedOffset..]);
        var low = BinaryPrimitives.ReadUInt32LittleEndian(desc[LuidOffset..]);
        var high = BinaryPrimitives.ReadUInt32LittleEndian(desc[(LuidOffset + 4)..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(desc[FlagsOffset..]);
        return new GpuAdapter(((ulong)high << 32) | low, name, (long)Math.Min(dedicated, long.MaxValue), vendor, (flags & SoftwareFlag) != 0);
    }

    private static void Release(nint instance)
    {
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)instance)[2];
        _ = release(instance);
    }
}
