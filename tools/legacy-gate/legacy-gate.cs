// legacy-gate: may tools/measure.ps1 start the old Parts Life (v0.7.4) from this folder?  (C36, DESIGN-C 7 and 8 step 7)
//
// The old app reads and writes the owner's real save unless its Debug-only test hook is compiled in. A text search for the hook name proved nothing (C35 review): this reads the IL.
//
//   dotnet run --file tools/legacy-gate/legacy-gate.cs -- <folder that holds PartsLife.dll and PartsLife.Update.dll>
//
// It passes only when, in PartsLife.dll, the body of PartsLife.Updates.AppData.get_TestDataDir
//   - has an ldstr "PARTSLIFE_TEST_DATA_DIR" followed by a call to System.Environment.GetEnvironmentVariable and a call to System.IO.Path.IsPathFullyQualified, and
//   - is more than the two instructions "ldnull; ret" of the Release build,
// and when, in PartsLife.Update.dll, PartsLife.Update.UpdateEndpoints.TestFeedAllowed is a constant (literal field) whose value is true.
// It refuses everything else: a missing or unreadable file, a body it cannot decode instruction by instruction, an opcode it does not know.
// One line on stdout, "legacy-gate ok" (exit 0) or "legacy-gate refuse: <why>" (exit 1). It loads nothing of the old app and starts nothing: metadata reading only.

using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

return Gate.Run(args);

static class Gate
{
    const string HookText = "PARTSLIFE_TEST_DATA_DIR";

    public static int Run(string[] args)
    {
        string? why;
        try
        {
            why = args.Length == 1 ? Check(args[0]) : "usage: legacy-gate <folder>";
        }
        catch (Exception e)
        {
            why = "unreadable: " + e.GetType().Name + ": " + e.Message;
        }

        if (why is null)
        {
            Console.WriteLine("legacy-gate ok");
            return 0;
        }

        Console.WriteLine("legacy-gate refuse: " + why.Replace('\r', ' ').Replace('\n', ' '));
        return 1;
    }

    static string? Check(string folder)
    {
        var main = Path.Combine(folder, "PartsLife.dll");
        var update = Path.Combine(folder, "PartsLife.Update.dll");
        if (!File.Exists(main)) return "PartsLife.dll is missing in " + folder;
        if (!File.Exists(update)) return "PartsLife.Update.dll is missing in " + folder;
        return CheckGetter(main) ?? CheckConstant(update);
    }

    static string? CheckGetter(string path)
    {
        using var stream = File.OpenRead(path);
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

    static string? CheckConstant(string path)
    {
        using var stream = File.OpenRead(path);
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
