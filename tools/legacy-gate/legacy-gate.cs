// legacy-gate: may tools/measure.ps1 start the old Parts Life (v0.7.4) from this exe?  (C36, C37; DESIGN-C 7 and 8 step 7)
//
// The old app reads and writes the owner's real save unless its Debug-only test hook is compiled in. A text search for the hook name proved nothing (C35 review): this reads the IL.
//
//   dotnet run --file tools/legacy-gate/legacy-gate.cs -- <PartsLife.exe, or the folder that holds PartsLife.dll and PartsLife.Update.dll>
//
// It passes only when, in PartsLife.dll, the body of PartsLife.Updates.AppData.get_TestDataDir
//   - has an ldstr "PARTSLIFE_TEST_DATA_DIR" followed by a call to System.Environment.GetEnvironmentVariable and a call to System.IO.Path.IsPathFullyQualified, and
//   - is more than the two instructions "ldnull; ret" of the Release build,
// and when, in PartsLife.Update.dll, PartsLife.Update.UpdateEndpoints.TestFeedAllowed is a constant (literal field) whose value is true.
// Two forms of the old app are read:
//   - the folder form: PartsLife.exe (a small apphost) with PartsLife.dll and PartsLife.Update.dll beside it. Prints "legacy-gate ok".
//   - the single-file form (C37, the shape v0.7.4 was shipped in): the apphost holds the 32-byte bundle signature preceded by the 8-byte offset of the bundle header.
//     The manifest is parsed (bundle version 6 only), exactly one PartsLife.dll and one PartsLife.Update.dll entry are taken (inflated with raw Deflate when the
//     entry's compressed size is not 0), and the same two checks run on those bytes. Prints "legacy-gate ok (single-file bundle)".
// It refuses everything else: a missing or unreadable file, a bundle it does not fully understand (version, offsets, sizes, entry kinds), a missing or duplicated
// entry, a body it cannot decode instruction by instruction, an opcode it does not know. A folder whose PartsLife.exe is a bundle is refused too (give it the exe).
// One line on stdout, "legacy-gate ok..." (exit 0) or "legacy-gate refuse: <why>" (exit 1). It loads nothing of the old app and starts nothing: metadata reading only.

using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

return Gate.Run(args);

static class Gate
{
    const string HookText = "PARTSLIFE_TEST_DATA_DIR";

    // The 32 bytes the host writer (HostModel Bundler) puts after the 8-byte header offset in the apphost.
    static readonly byte[] BundleSignature =
    {
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
    };

    const int BundleMajor = 6;
    const long MaxPartBytes = 256L * 1024 * 1024;

    public static int Run(string[] args)
    {
        string? why;
        var mode = "folder";
        try
        {
            why = args.Length == 1 ? Check(args[0], out mode) : "usage: legacy-gate <PartsLife.exe or its folder>";
        }
        catch (Exception e)
        {
            why = "unreadable: " + e.GetType().Name + ": " + e.Message;
        }

        if (why is null)
        {
            Console.WriteLine(mode == "bundle" ? "legacy-gate ok (single-file bundle)" : "legacy-gate ok");
            return 0;
        }

        Console.WriteLine("legacy-gate refuse: " + why.Replace('\r', ' ').Replace('\n', ' '));
        return 1;
    }

    static string? Check(string target, out string mode)
    {
        mode = "folder";
        string folder;
        if (Directory.Exists(target))
        {
            folder = target;
            var exe = Path.Combine(folder, "PartsLife.exe");
            if (File.Exists(exe))
            {
                var probe = ProbeBundle(File.ReadAllBytes(exe), out _);
                if (probe != "none") return "PartsLife.exe in the folder is a single-file bundle or has an unreadable bundle marker: give the exe itself (" + probe + ")";
            }
        }
        else if (File.Exists(target))
        {
            var data = File.ReadAllBytes(target);
            var probe = ProbeBundle(data, out var headerOffset);
            if (probe == "bundle")
            {
                mode = "bundle";
                var why = ReadBundle(data, headerOffset, out var bundledMain, out var bundledUpdate);
                return why ?? CheckGetter(bundledMain) ?? CheckConstant(bundledUpdate);
            }

            if (probe != "none") return "the exe has an unreadable bundle marker (" + probe + ")";
            folder = Path.GetDirectoryName(Path.GetFullPath(target)) ?? "";
        }
        else return "not found: " + target;

        var main = Path.Combine(folder, "PartsLife.dll");
        var update = Path.Combine(folder, "PartsLife.Update.dll");
        if (!File.Exists(main)) return "PartsLife.dll is missing in " + folder;
        if (!File.Exists(update)) return "PartsLife.Update.dll is missing in " + folder;
        return CheckGetter(File.ReadAllBytes(main)) ?? CheckConstant(File.ReadAllBytes(update));
    }

    // "none" = the signature is not in the file, or it sits there with offset 0 (an apphost that was never bundled); "bundle" = exactly one signature with a plausible
    // offset; anything else is the reason this file cannot be read as either.
    static string ProbeBundle(byte[] data, out long headerOffset)
    {
        headerOffset = 0;
        var at = -1;
        var count = 0;
        var from = 0;
        while (from <= data.Length - BundleSignature.Length)
        {
            var hit = data.AsSpan(from).IndexOf(BundleSignature);
            if (hit < 0) break;
            at = from + hit;
            count++;
            from = at + 1;
        }

        if (count == 0) return "none";
        if (count > 1) return "the bundle signature is in the file " + count + " times";
        if (at < 8) return "the bundle signature has no room for the header offset";
        headerOffset = BitConverter.ToInt64(data, at - 8);
        if (headerOffset == 0) return "none";
        if (headerOffset < 0 || headerOffset > data.Length - 40) return "the bundle header offset is outside the file";
        return "bundle";
    }

    sealed record Entry(long Offset, long Size, long Compressed, byte Type, string Path);

    static string? ReadBundle(byte[] data, long headerOffset, out byte[] main, out byte[] update)
    {
        main = Array.Empty<byte>();
        update = Array.Empty<byte>();
        using var ms = new MemoryStream(data, false) { Position = headerOffset };
        using var r = new BinaryReader(ms, Encoding.UTF8);
        var major = r.ReadUInt32();
        var minor = r.ReadUInt32();
        if (major != BundleMajor) return "an unfamiliar bundle version " + major + "." + minor + " (this gate reads " + BundleMajor + ".x)";
        var count = r.ReadInt32();
        if (count < 1 || count > 20000) return "an implausible number of bundle entries: " + count;
        var id = r.ReadString();
        if (id.Length == 0 || id.Length > 256) return "an unfamiliar bundle id";
        // deps.json offset and size, runtimeconfig.json offset and size, flags (header version 2 and later)
        r.ReadInt64(); r.ReadInt64(); r.ReadInt64(); r.ReadInt64(); r.ReadUInt64();

        var wantMain = new List<Entry>();
        var wantUpdate = new List<Entry>();
        for (var n = 0; n < count; n++)
        {
            var offset = r.ReadInt64();
            var size = r.ReadInt64();
            var compressed = r.ReadInt64();
            var type = r.ReadByte();
            var path = r.ReadString();
            if (type > 5) return "an unfamiliar bundle entry kind " + type + " for " + path;
            if (offset < 0 || size < 0 || compressed < 0) return "a negative offset or size in the entry " + path;
            var stored = compressed != 0 ? compressed : size;
            if (offset > data.Length || stored > data.Length - offset) return "the entry " + path + " reaches outside the file";
            var e = new Entry(offset, size, compressed, type, path);
            if (string.Equals(path, "PartsLife.dll", StringComparison.OrdinalIgnoreCase)) wantMain.Add(e);
            else if (string.Equals(path, "PartsLife.Update.dll", StringComparison.OrdinalIgnoreCase)) wantUpdate.Add(e);
        }

        if (wantMain.Count != 1) return "the bundle has " + wantMain.Count + " entries named PartsLife.dll (exactly one is needed)";
        if (wantUpdate.Count != 1) return "the bundle has " + wantUpdate.Count + " entries named PartsLife.Update.dll (exactly one is needed)";
        var why = Take(data, wantMain[0], out main);
        return why ?? Take(data, wantUpdate[0], out update);
    }

    static string? Take(byte[] data, Entry e, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (e.Type != 1) return "the bundle entry " + e.Path + " is not an assembly";
        if (e.Size > MaxPartBytes || e.Size == 0) return "the bundle entry " + e.Path + " has an implausible size";
        if (e.Compressed == 0)
        {
            bytes = data.AsSpan((int)e.Offset, (int)e.Size).ToArray();
            return null;
        }

        if (e.Compressed > MaxPartBytes) return "the bundle entry " + e.Path + " has an implausible compressed size";
        using var packed = new MemoryStream(data, (int)e.Offset, (int)e.Compressed, false);
        using var inflate = new DeflateStream(packed, CompressionMode.Decompress);
        var buffer = new byte[e.Size + 1];
        var got = 0;
        while (got < buffer.Length)
        {
            var n = inflate.Read(buffer, got, buffer.Length - got);
            if (n == 0) break;
            got += n;
        }

        if (got != e.Size) return "the bundle entry " + e.Path + " inflates to " + got + " bytes, not the " + e.Size + " it says";
        bytes = buffer.AsSpan(0, got).ToArray();
        return null;
    }

    static string? CheckGetter(byte[] image)
    {
        using var stream = new MemoryStream(image, false);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return "PartsLife.dll is not a managed assembly";
        var md = pe.GetMetadataReader();

        MethodDefinition? found = null;
        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            if (md.GetString(td.Namespace) != "PartsLife.Updates" || md.GetString(td.Name) != "AppData") continue;
            foreach (var mh in td.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                if (md.GetString(m.Name) != "get_TestDataDir") continue;
                if (found is not null) return "get_TestDataDir is there twice";
                found = m;
            }
        }

        if (found is not { } getter) return "PartsLife.Updates.AppData.get_TestDataDir was not found";
        if (getter.RelativeVirtualAddress == 0) return "get_TestDataDir has no body";
        var il = pe.GetMethodBody(getter.RelativeVirtualAddress).GetILBytes();
        if (il is null || il.Length == 0) return "get_TestDataDir has an empty body";

        var ops = new Dictionary<int, OpCode>();
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.GetValue(null) is OpCode oc) ops[(ushort)oc.Value] = oc;
        }

        var count = 0;
        var sawHook = false;
        var sawEnv = false;
        var sawQualified = false;
        var i = 0;
        while (i < il.Length)
        {
            int code = il[i++];
            if (code == 0xFE)
            {
                if (i >= il.Length) return "the body ends inside an opcode";
                code = 0xFE00 | il[i++];
            }

            if (!ops.TryGetValue(code, out var op)) return "an opcode this gate does not know: 0x" + code.ToString("X");
            count++;
            int size;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: size = 0; break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: size = 1; break;
                case OperandType.InlineVar: size = 2; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: size = 8; break;
                case OperandType.InlineSwitch:
                    if (i + 4 > il.Length) return "the body ends inside a switch";
                    size = 4 + (4 * BitConverter.ToInt32(il, i));
                    break;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR: size = 4; break;
                default: return "an operand kind this gate does not know: " + op.OperandType;
            }

            if (size < 0 || i + size > il.Length) return "the body ends inside an operand";
            if (op == OpCodes.Ldstr)
            {
                var token = BitConverter.ToInt32(il, i);
                if ((token >>> 24) != 0x70) return "an ldstr whose token is not a user string";
                var text = md.GetUserString(MetadataTokens.UserStringHandle(token & 0xFFFFFF));
                if (text == HookText) sawHook = true;
            }
            else if ((op == OpCodes.Call || op == OpCodes.Callvirt) && sawHook)
            {
                var handle = MetadataTokens.EntityHandle(BitConverter.ToInt32(il, i));
                if (handle.Kind == HandleKind.MemberReference)
                {
                    var mr = md.GetMemberReference((MemberReferenceHandle)handle);
                    if (mr.Parent.Kind == HandleKind.TypeReference)
                    {
                        var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                        var type = md.GetString(tr.Namespace) + "." + md.GetString(tr.Name);
                        var name = md.GetString(mr.Name);
                        if (type == "System.Environment" && name == "GetEnvironmentVariable") sawEnv = true;
                        if (type == "System.IO.Path" && name == "IsPathFullyQualified") sawQualified = true;
                    }
                }
            }

            i += size;
        }

        if (count <= 2) return "get_TestDataDir is " + count + " instruction(s): the hook is compiled out (a Release build)";
        if (!sawHook) return "get_TestDataDir does not load the text " + HookText;
        if (!sawEnv) return "get_TestDataDir does not call Environment.GetEnvironmentVariable after loading the text";
        if (!sawQualified) return "get_TestDataDir does not check Path.IsPathFullyQualified";
        return null;
    }

    static string? CheckConstant(byte[] image)
    {
        using var stream = new MemoryStream(image, false);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return "PartsLife.Update.dll is not a managed assembly";
        var md = pe.GetMetadataReader();
        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            if (md.GetString(td.Namespace) != "PartsLife.Update" || md.GetString(td.Name) != "UpdateEndpoints") continue;
            foreach (var fh in td.GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                if (md.GetString(f.Name) != "TestFeedAllowed") continue;
                if ((f.Attributes & FieldAttributes.Literal) == 0) return "UpdateEndpoints.TestFeedAllowed is not a constant";
                var handle = f.GetDefaultValue();
                if (handle.IsNil) return "UpdateEndpoints.TestFeedAllowed has no value";
                var c = md.GetConstant(handle);
                if (c.TypeCode != ConstantTypeCode.Boolean) return "UpdateEndpoints.TestFeedAllowed is not a bool";
                var value = md.GetBlobReader(c.Value).ReadBoolean();
                return value ? null : "UpdateEndpoints.TestFeedAllowed is false: the test feed is compiled out";
            }
        }

        return "PartsLife.Update.UpdateEndpoints.TestFeedAllowed was not found";
    }
}
