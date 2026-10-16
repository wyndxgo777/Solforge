// Deep-data checks: the decompiled-code index (functions, names, calls, data refs, tag classes, cache, grep, rename),
// struct inference, the memory image (strings, vtables), generic tag layouts and schemas, cursor paging, CSV, and the
// decomp/data/export MCP tools end to end against a small fixture analysis folder. No game install or real decomp needed.
using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Solforge.Mcp;
using Solforge.Mcp.Data;
using Solforge.Mcp.Decomp;

static class DecompChecks
{
    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("decomp: " + message);
    }

    /** Creates a small decompiled-code folder under the root and returns its path. */
    public static string Fixture(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "decomp", "all"));
        Directory.CreateDirectory(Path.Combine(root, "full"));
        Directory.CreateDirectory(Path.Combine(root, "listings"));
        File.WriteAllText(Path.Combine(root, "decomp", "all", "decomp_00100000.c"),
            "// destiny2.exe build 86657, decompiled functions RVA 00100000-00200000\n\n" +
            "// ==== RVA 00100010 ====\n\n/* WARNING: Globals starting with '_' overlap smaller symbols at the same address */\n\n" +
            "ulonglong FUN_00100010(undefined8 param_1,uint param_2)\n\n{\n  puVar7 = &UNK_01BCDC1C;\n  lVar3 = FUN_00100200(0);\n" +
            "  cVar1 = func_0x00100300(param_2);\n  if (*(uint *)(lVar3 + 4) == 0x80809fbd) return 1;\n  if (iVar2 == -0x7f7f6043) return 2;\n" +
            "  return *(ulonglong *)(&DAT_01C277D0 + (longlong)iVar2 * 8);\n}\n\n" +
            "// ==== RVA 00100200 ====\n\nint FUN_00100200(char *param_1)\n\n{\n  if (*param_1 == '\\0') return -1;\n" +
            "  *(int *)(param_1 + 0x390) = 1;\n  lVar1 = *(longlong *)(param_1 + 8);\n  return *(int *)(param_1 + 0x390);\n}\n");
        File.WriteAllText(Path.Combine(root, "decomp", "func_00E2DEA0.c"),
            "// destiny2.exe build 86657, function at RVA 00E2DEA0 (64 bytes)\n\nvoid FUN_00E2DEA0(void)\n\n{\n  FUN_00100010(0,1);\n  return;\n}\n");
        File.WriteAllText(Path.Combine(root, "decomp", "all", "FAILED.txt"), "# failed\n00294260\n");
        File.WriteAllText(Path.Combine(root, "full", "functions.csv"), "start,end,size\n00100010,00100074,100\n00100200,00100240,64\n");
        File.WriteAllText(Path.Combine(root, "NOTES.md"), "| RVA | Name | Signature | Notes |\n|---|---|---|---|\n| `0x100010` | `goal_allowed` | `bool (int)` | test |\n");
        File.WriteAllText(Path.Combine(root, "listings", "world_controller_00100200.asm"), "; listing\n");
        return root;
    }

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static async Task Run(string scratch, FixtureHost host)
    {
        string root = Fixture(Path.Combine(scratch, "analysis"));
        string cache = Path.Combine(scratch, "decomp-cache", "index.bin");
        var index = DecompIndex.Load(root, cache);
        Expect(index.Functions.Count == 3, "three functions (two from decomp_*.c, one from func_*.c)");
        Expect(index.Failed.Contains(0x00294260u), "FAILED.txt");
        var a = index.Get(0x00100010)!;
        var b = index.Get(0x00100200)!;
        Expect(a.Size == 100 && a.Signature == "ulonglong FUN_00100010(undefined8 param_1,uint param_2)", "size and signature: " + a.Signature);
        Expect(index.NameOf(0x00100010) == "goal_allowed" && index.NameOf(0x00100200) == "world_controller", "names from NOTES.md and listings");
        Expect(index.CallsFrom(a).SequenceEqual([0x00100200u, 0x00100300u]), "calls: FUN_ and func_0x, not itself");
        Expect(index.RefsFrom(a).SequenceEqual([0x01BCDC1Cu, 0x01C277D0u]), "data refs");
        Expect(index.TagClassesOf(a).SequenceEqual([0x80809FBDu]), "tag class, hex and negative forms fold");
        Expect(index.CallersOf(0x00100200).Single().Rva == 0x00100010 && index.CallersOf(0x00100010).Single().Rva == 0x00E2DEA0, "callers");
        Expect(index.UsersOf(0x01C277D0).Single() == a && index.UsersOfTagClass(0x80809FBD).Single() == a, "reverse maps");
        Expect(index.Containing(0x00100050) == a && index.Containing(0x00100080) is null, "containing uses size");
        Expect(index.Resolve("goal_allowed") == 0x00100010 && index.Resolve("FUN_00100200") == 0x00100200 && index.Resolve("0x100200") == 0x00100200, "resolve");
        Expect(index.Search("world").First() == b, "search by name");
        Expect(index.Source(b).Contains("param_1 + 0x390") && !index.Source(b).Contains("FUN_00100010"), "source slice");
        Expect(index.Grep("0X390", CancellationToken.None).All(hit => hit.Function == b) && index.Grep("0x390", CancellationToken.None).Count() == 2, "grep");
        var cached = DecompIndex.Load(root, cache);
        Expect(cached.Functions.Count == 3 && cached.CallersOf(0x00100200).Count() == 1 && cached.Get(0x00100010)!.Signature == a.Signature, "cache round trip");
        index.Rename(0x00100300, "helper_three");
        Expect(index.NameOf(0x00100300) == "helper_three" && File.ReadAllText(Path.Combine(root, DecompIndex.NamesFile)).Contains("00100300,helper_three"), "rename");
        Expect(DecompIndex.Locate([Path.Combine(root, "decomp", "all")]) == Path.GetFullPath(root), "locate walks up");

        var fields = StructLayouts.Infer(index.Source(b), "param_1");
        Expect(fields.Count == 3 && fields[0].Offset == 0 && fields[1] is { Offset: 8, Type: "longlong" } && fields[2] is { Offset: 0x390, Type: "int", Uses: 2 },
            "inferred struct: " + string.Join(";", fields));
        Expect(StructLayouts.Variables(index.Source(b)).First() == "param_1", "variables");
        string headers = Path.Combine(scratch, "headers");
        Directory.CreateDirectory(headers);
        File.WriteAllText(Path.Combine(headers, "world.h"), "#pragma once\nstruct WorldController : Base {\n  int goal; // +0x390\n  struct { int x; } inner;\n};\nstruct Other { int y; };\n");
        var found = StructLayouts.FindDefinitions([headers], "WorldController", CancellationToken.None).ToList();
        Expect(found.Count == 1 && found[0].Line == 2 && found[0].Text.EndsWith("};"), "struct definition with nested braces");
        Expect(StructLayouts.Grep([headers], ["OTHER"], CancellationToken.None).Single().Line == 6, "source grep");

        // Memory image: no PE header, so the whole file is one data section.
        const ulong imageBase = 0x140000000;
        var image = new byte[0x200];
        Encoding.ASCII.GetBytes("hello world").CopyTo(image, 0x10);
        Encoding.Unicode.GetBytes("wide text").CopyTo(image, 0x40);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x100), imageBase + 0x00100010);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x108), imageBase + 0x00100200);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x110), imageBase + 0x00E2DEA0);
        var binary = BinaryImage.FromBytes(image, "{\"base\":\"0x140000000\"}", index.IsFunctionStart);
        Expect(binary.StringAt(0x10)?.Text == "hello world" && binary.StringAt(0x40) is { Wide: true, Text: "wide text" }, "ascii and utf-16 strings");
        Expect(binary.VTables.Count == 1 && binary.VTables[0].Rva == 0x100 && binary.VTables[0].Entries.Length == 3 && binary.VTablesWith(0x00100200).Count == 1, "vtable");

        // Generic tag layout: f32, u32, then a typed array of two 8-byte elements.
        static byte[] Tag(float value, uint count)
        {
            var bytes = new byte[0x3C];
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0), value);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), count);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 2);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), 0x1C - 16);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x18), TagLayout.ArrayMarker);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x1C), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x24), 0x80801234);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x2C), 2.5f);
            return bytes;
        }
        var layout = TagLayout.Describe(Tag(1.5f, 7), null);
        var root0 = layout["root"]!.AsArray();
        Expect(layout["root_size"]!.GetValue<int>() == 0x18 && root0.Count == 3, "root fields: " + layout.ToJsonString());
        Expect(root0[0]!["kind"]!.ToString() == "f32" && root0[0]!["value"]!.ToString() == "1.5" && root0[1]!["kind"]!.ToString() == "u32", "scalars");
        Expect(root0[2]!["kind"]!.ToString() == "array" && root0[2]!["count"]!.GetValue<int>() == 2 && root0[2]!["stride"]!.GetValue<int>() == 8
            && root0[2]!["class"]!.ToString() == "0x80801234" && root0[2]!["elements"]!.AsArray().Count == 2, "typed array");
        var summary = new TagSummary(1, null, 0x80800001, "x", "p", 0x3C);
        var schema = TagLayout.InferSchema([(summary, Tag(1.5f, 7)), (summary, Tag(3f, 9))], null);
        var schemaFields = schema["fields"]!.AsArray();
        Expect(schema["samples"]!.GetValue<int>() == 2 && schemaFields.Count == 3 && schemaFields.All(field => field!["stable"]!.GetValue<bool>())
            && schemaFields[2]!["array_classes"]!.AsArray().Single()!.ToString() == "0x80801234", "class schema: " + schema.ToJsonString());

        // Paging and CSV.
        var page = DataPages.Page(Enumerable.Range(0, 120), new JsonObject(), number => number);
        Expect(page["total"]!.GetValue<int>() == 120 && page["next_cursor"]!.ToString() == "50", "first page");
        var last = DataPages.Page(Enumerable.Range(0, 120), new JsonObject { ["cursor"] = "100" }, number => number);
        Expect(last["count"]!.GetValue<int>() == 20 && last["next_cursor"] is null, "last page");
        string csv = DataPages.Csv([new JsonObject { ["a"] = "x,y", ["b"] = 1 }, new JsonObject { ["b"] = 2, ["c"] = new JsonArray(1) }]);
        Expect(csv.Replace("\r\n", "\n") == "a,b,c\n\"x,y\",1,\n,2,[1]\n", "csv: " + csv);

        // The MCP tools over the fixture.
        string? previous = Environment.GetEnvironmentVariable("SOLFORGE_DECOMP"), previousCache = Environment.GetEnvironmentVariable("SOLFORGE_DECOMP_CACHE");
        Environment.SetEnvironmentVariable("SOLFORGE_DECOMP", root);
        Environment.SetEnvironmentVariable("SOLFORGE_DECOMP_CACHE", Path.Combine(scratch, "decomp-cache-tools"));
        try
        {
            var tools = SolforgeTools.Create(host);
            async Task<McpToolResult> Call(string name, JsonObject args) => await tools.Single(tool => tool.Name == name).Handler(args, CancellationToken.None);
            var search = await Call("search_functions", new JsonObject { ["query"] = "goal" });
            Expect(!search.IsError && JsonNode.Parse(search.Texts[0])!["rows"]![0]!["rva"]!.ToString() == "0x00100010", "search_functions");
            var source = await Call("get_function", new JsonObject { ["function"] = "world_controller", ["max_lines"] = 3 });
            Expect(source.Texts[0].Contains("next_cursor: 3") && source.Texts[0].Contains("signature: int FUN_00100200(char *param_1)"), "get_function pages: " + source.Texts[0]);
            var rest = await Call("get_function", new JsonObject { ["function"] = "0x100200", ["cursor"] = "3", ["max_lines"] = 400 });
            Expect(rest.Texts[0].Contains("(end)") && rest.Texts[0].Contains("0x390"), "get_function next page");
            var failed = await Call("get_function", new JsonObject { ["function"] = "0x294260" });
            Expect(failed.IsError && failed.Texts[0].Contains("FAILED.txt"), "failed decompile explained");
            var to = JsonNode.Parse((await Call("xrefs_to", new JsonObject { ["address"] = "0x01C277D0" })).Texts[0])!;
            Expect(to["rows"]!.AsArray().Single()!["kind"]!.ToString() == "data", "xrefs_to data");
            var from = JsonNode.Parse((await Call("xrefs_from", new JsonObject { ["function"] = "goal_allowed", ["kind"] = "tag_classes" })).Texts[0])!;
            Expect(from["rows"]![0]!["class"]!.ToString() == "0x80809FBD", "xrefs_from tag classes");
            var handlers = JsonNode.Parse((await Call("tag_type_handlers", new JsonObject { ["class"] = "0x80809FBD", ["source"] = false })).Texts[0])!;
            Expect(handlers["rows"]![0]!["rva"]!.ToString() == "0x00100010", "tag_type_handlers");
            var inferred = JsonNode.Parse((await Call("get_struct", new JsonObject { ["function"] = "0x100200", ["variable"] = "param_1" })).Texts[0])!;
            Expect(inferred["total"]!.GetValue<int>() == 3, "get_struct inference");
            var grep = JsonNode.Parse((await Call("grep_decomp", new JsonObject { ["text"] = "0x390", ["limit"] = 1 })).Texts[0])!;
            Expect(grep["count"]!.GetValue<int>() == 1 && grep["next_cursor"]!.ToString() == "1", "grep_decomp cursor");
            var noData = await Call("get_string", new JsonObject { ["hash"] = "0x12345678" });
            Expect(noData.IsError, "data tools need a data host");
            var export = await Call("export_data", new JsonObject { ["tool"] = "search_functions", ["args"] = new JsonObject { ["query"] = "FUN" }, ["format"] = "csv", ["file"] = "fns" });
            var exported = JsonNode.Parse(export.Texts[0])!;
            string path = exported["path"]!.ToString();
            Expect(exported["rows"]!.GetValue<int>() == 3 && path.EndsWith("fns.csv") && File.ReadAllLines(path).Length == 4
                && path.StartsWith(SolforgeTools.ExportFolder(host.ProjectRoot)), "export_data csv: " + export.Texts[0]);
            var refused = await Call("export_data", new JsonObject { ["tool"] = "rename_function" });
            Expect(refused.IsError, "export_data refuses action tools");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOLFORGE_DECOMP", previous);
            Environment.SetEnvironmentVariable("SOLFORGE_DECOMP_CACHE", previousCache);
        }
    }
}
