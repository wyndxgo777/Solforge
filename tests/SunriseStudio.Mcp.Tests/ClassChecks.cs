// Class-layer checks: MSVC x64 RTTI (type descriptors, complete object locators, hierarchy descriptors) in a tiny memory
// image, vtable splitting at referenced starts, virtual-function owners (Class::vfN names), constructors, inferred fields,
// the class cache, the demangler, and the get_class / get_vtables / get_struct / decomp_status tools.
using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Solforge.Mcp;
using Solforge.Mcp.Decomp;

static class ClassChecks
{
    private const ulong ImageBase = 0x140000000;

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("classes: " + message);
    }

    private static string Function(uint rva, string signature, params string[] body) =>
        $"// ==== RVA {rva:X8} ====\n\n{signature}\n\n{{\n" + string.Concat(body.Select(line => "  " + line + "\n")) + "  return;\n}\n\n";

    /** An analysis folder with seven functions and an image holding RTTI for game::Base and game::Derived plus one pointer run. */
    public static string Fixture(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "decomp", "all"));
        Directory.CreateDirectory(Path.Combine(root, "full"));
        Directory.CreateDirectory(Path.Combine(root, "image"));
        File.WriteAllText(Path.Combine(root, "decomp", "all", "decomp_00100000.c"),
            "// destiny2.exe build 86657, decompiled functions RVA 00100000-00200000\n\n" +
            Function(0x100010, "void FUN_00100010(char *param_1)", "*(int *)(param_1 + 0x10) = 0;", "if (*(uint *)(lVar1 + 4) == 0x80809fbd) return;") +
            Function(0x100100, "int FUN_00100100(char *param_1)", "*(float *)(param_1 + 0x20) = 1.0;", "iVar1 = *(int *)(param_1 + 0x10);") +
            Function(0x100200, "void FUN_00100200(char *param_1)", "*(longlong *)(param_1 + 0x28) = 0;") +
            Function(0x100300, "void FUN_00100300(char *param_1)", "*(undefined **)param_1 = &UNK_00000328;", "*(int *)(param_1 + 0x30) = 5;") +
            Function(0x100400, "void FUN_00100400(void)") +
            Function(0x100500, "void FUN_00100500(void)") +
            Function(0x100600, "void FUN_00100600(void)") +
            Function(0x100700, "void FUN_00100700(char *param_1)", "*(undefined **)param_1 = &UNK_00000410;"));
        File.WriteAllText(Path.Combine(root, "full", "functions.csv"), "start,end,size\n" + string.Concat(
            new uint[] { 0x100010, 0x100100, 0x100200, 0x100300, 0x100400, 0x100500, 0x100600, 0x100700 }.Select(rva => $"{rva:X8},{rva + 0x40:X8},64\n")));

        // No PE header: the whole file is one data section, and code is "a known function start".
        var image = new byte[0x600];
        void Q(int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(at), value);
        void D(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), value);
        void Descriptor(int at, string name) { Q(at, ImageBase + 0x10); Encoding.ASCII.GetBytes(name).CopyTo(image, at + 16); }
        void BaseDescriptor(int at, uint type, uint contained, uint hierarchy) { D(at, type); D(at + 4, contained); D(at + 12, uint.MaxValue); D(at + 20, 0x40); D(at + 24, hierarchy); }
        void Locator(int at, uint type, uint hierarchy) { D(at, 1); D(at + 12, type); D(at + 16, hierarchy); D(at + 20, (uint)at); }
        Descriptor(0x100, ".?AVBase@game@@");
        Descriptor(0x140, ".?AVDerived@game@@");
        D(0x188, 1); D(0x18C, 0x190); D(0x190, 0x1A0);                 // Base hierarchy: one entry (itself)
        BaseDescriptor(0x1A0, 0x100, 0, 0x180);
        D(0x1C8, 2); D(0x1CC, 0x1D0); D(0x1D0, 0x1E0); D(0x1D4, 0x1A0); // Derived hierarchy: itself, then Base
        BaseDescriptor(0x1E0, 0x140, 1, 0x1C0);
        Locator(0x200, 0x100, 0x180);
        Locator(0x220, 0x140, 0x1C0);
        Q(0x300, ImageBase + 0x200); Q(0x308, ImageBase + 0x100010); Q(0x310, ImageBase + 0x100100);  // Base vtable at 0x308
        Q(0x320, ImageBase + 0x220); Q(0x328, ImageBase + 0x100010); Q(0x330, ImageBase + 0x100200);  // Derived vtable at 0x328
        // A run without RTTI holding two vtables; FUN_00100700 stores the second (0x410).
        Q(0x400, ImageBase + 0x100400); Q(0x408, ImageBase + 0x100500); Q(0x410, ImageBase + 0x100600); Q(0x418, ImageBase + 0x100500);
        File.WriteAllBytes(Path.Combine(root, "image", "destiny2_image.bin"), image);
        File.WriteAllText(Path.Combine(root, "image", "image.json"), "{\"base\":\"0x140000000\"}");
        return root;
    }

    public static async Task Run(string scratch, FixtureHost host)
    {
        Expect(RttiNames.Demangle(".?AVhkpEntity@@") == "hkpEntity", "plain name");
        Expect(RttiNames.Demangle(".?AUAllocator@hkContainerHeapAllocator@@") == "hkContainerHeapAllocator::Allocator", "nested name");
        Expect(RttiNames.Demangle(".?AV?$hkSingleton@VhkTraceStream@@@@") == "hkSingleton<hkTraceStream>", "template: " + RttiNames.Demangle(".?AV?$hkSingleton@VhkTraceStream@@@@"));
        string nested = RttiNames.Demangle(".?AU?$ConstFunction@U?$IFunction@VhkVector4@@M@hkGeometryProcessing@@@hkGeometryProcessing@@");
        Expect(nested == "hkGeometryProcessing::ConstFunction<hkGeometryProcessing::IFunction<hkVector4,float>>", "nested template: " + nested);
        Expect(RttiNames.Demangle(".?AV?$Weird@$$Q@@") == "?$Weird@$$Q@@", "unknown encoding falls back to the raw name");

        string root = Fixture(Path.Combine(scratch, "analysis"));
        string cache = Path.Combine(scratch, "cache", "index.bin");
        var index = DecompIndex.Load(root, cache);
        Expect(index.Functions.Count == 8, "eight functions");
        var rtti = ClassIndex.ScanRtti(index.Image!);
        Expect(rtti.Count == 2 && rtti[0].Mangled == ".?AVBase@game@@" && rtti[0].VTables.Single().Rva == 0x308 && rtti[0].VTables[0].Locator == 0x200,
            "RTTI base: " + string.Join(";", rtti.Select(t => t.Mangled)));
        Expect(rtti[1].Bases.Single() is { Name: "game::Base", Depth: 1, TypeDescriptor: 0x100 } && rtti[1].VTables.Single().Entries.SequenceEqual([0x100010u, 0x100200u]),
            "RTTI derived: bases and vtable");

        var classes = index.Classes!;
        Expect(classes.RttiClasses == 2 && classes.Classes.Count == 4 && classes.VTableCount == 4, "two RTTI classes plus two split vtables: "
            + string.Join(";", classes.Classes.Select(c => c.Name)));
        Expect(classes.VTableAt(0x400)?.Class.VTables[0].Entries.Length == 2 && classes.VTableAt(0x410)?.Class.Name == "vtable_00000410", "run split at a referenced slot");
        Expect(index.NameOf(0x100010) == "game::Base::vf0" && index.NameOf(0x100100) == "game::Base::vf1" && index.NameOf(0x100200) == "game::Derived::vf1",
            "virtual names by owning class: " + index.NameOf(0x100010));
        Expect(index.NameOf(0x100500) == "vtable_00000400::vf1" && index.NameSource(0x100500) == "class" && index.VirtualNameCount == 6, "vtable-only owners");
        Expect(index.Resolve("game::Derived::vf1") == 0x100200 && index.Search("game::Base").Count() == 2, "resolve and search virtual names");
        var derived = classes.Search("Derived").First();
        var baseClass = classes.Search("game::Base").First();
        Expect(classes.DerivedFrom(baseClass).Single() == derived && derived.Constructors.SequenceEqual([0x100300u]), "derived classes and constructors");
        Expect(derived.Fields.Select(f => f.Offset).SequenceEqual([0, 0x28, 0x30]) && derived.Fields[1].Type == "longlong", "derived fields: " + string.Join(";", derived.Fields.Select(field => field.ToString())));
        Expect(baseClass.Fields is [{ Offset: 0x10, Type: "int", Uses: 2 }, { Offset: 0x20, Type: "float" }] && baseClass.TagClasses.SequenceEqual([0x80809FBDu]),
            "base fields and tag classes: " + string.Join(";", baseClass.Fields.Select(field => field.ToString())));

        var reloaded = DecompIndex.Load(root, cache).Classes!;
        Expect(reloaded.Classes.Count == 4 && reloaded.Search("Derived").First().Fields.Length == 3 && reloaded.Virtuals[0x100010].Name == "game::Base::vf0"
            && reloaded.Search("Derived").First().Bases.Single().Name == "game::Base", "class cache round trip");
        Expect(File.Exists(Path.Combine(scratch, "cache", "index.classes.bin")), "class cache file beside the index");

        string? previous = Environment.GetEnvironmentVariable("SOLFORGE_DECOMP"), previousCache = Environment.GetEnvironmentVariable("SOLFORGE_DECOMP_CACHE");
        Environment.SetEnvironmentVariable("SOLFORGE_DECOMP", root);
        Environment.SetEnvironmentVariable("SOLFORGE_DECOMP_CACHE", Path.Combine(scratch, "cache-tools"));
        try
        {
            var tools = SolforgeTools.Create(host);
            async Task<JsonNode> Call(string name, JsonObject args)
            {
                var result = await tools.Single(tool => tool.Name == name).Handler(args, CancellationToken.None);
                Expect(!result.IsError, name + ": " + result.Texts[0]);
                return JsonNode.Parse(result.Texts[0])!;
            }
            var status = await Call("decomp_status", new JsonObject());
            Expect(status["classes"]!["rtti_classes"]!.GetValue<int>() == 2 && status["classes"]!["vtables"]!.GetValue<int>() == 4, "decomp_status classes");
            var info = await Call("get_class", new JsonObject { ["class"] = "Derived", ["source"] = false });
            var methods = info["methods"]!.AsArray();
            Expect(info["name"]!.ToString() == "game::Derived" && info["bases"]![0]!["name"]!.ToString() == "game::Base"
                && methods[0]!["inherited"]!.GetValue<bool>() && !methods[1]!["inherited"]!.GetValue<bool>()
                && info["fields"]!.AsArray().Count == 3 && info["constructors"]![0]!.ToString().Contains("00100300"), "get_class: " + info.ToJsonString());
            var byMethod = await Call("get_class", new JsonObject { ["class"] = "0x100100", ["source"] = false });
            Expect(byMethod["name"]!.ToString() == "game::Base" && byMethod["derived"]![0]!.ToString() == "game::Derived", "get_class by method");
            var listing = await Call("get_class", new JsonObject { ["rtti"] = true });
            Expect(listing["total"]!.GetValue<int>() == 2, "get_class listing");
            var table = await Call("get_vtables", new JsonObject { ["address"] = "0x328" });
            Expect(table["class"]!.ToString() == "game::Derived" && table["rows"]![0]!["owner"]!.ToString() == "game::Base", "get_vtables class and owners");
            var holding = await Call("get_vtables", new JsonObject { ["function"] = "0x100010" });
            Expect(holding["total"]!.GetValue<int>() == 2, "get_vtables holding");
            var layout = await Call("get_struct", new JsonObject { ["name"] = "Derived" });
            Expect(layout["rows"]![0]!["kind"]!.ToString() == "inferred" && layout["rows"]![0]!["fields"]!.AsArray().Count == 3, "get_struct inferred class layout");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOLFORGE_DECOMP", previous);
            Environment.SetEnvironmentVariable("SOLFORGE_DECOMP_CACHE", previousCache);
        }
    }
}
