using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using SunriseStudio.Missions;

/** A temp install with a bad, late or missing SDK file: the catalog says why and keeps working. */
internal static class InstallChecks
{
    private const string MissingApi = "mission_api.json is missing. Start the game once, then reload.";

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string temporary)
    {
        int shapes = WrongSchema(TestInstall.Write(Path.Combine(temporary, "install-wrong-schema")));
        LateSchema(TestInstall.Write(Path.Combine(temporary, "install-late-schema")));
        string rows = DamagedModule(TestInstall.Write(Path.Combine(temporary, "install-damaged")));
        MissingFiles(Path.Combine(temporary, "install-missing"));
        UnreadableScripts(TestInstall.Write(Path.Combine(temporary, "install-unreadable")));
        ModuleCache(TestInstall.Write(Path.Combine(temporary, "install-cache")));
        Console.WriteLine($"mission_install wrong_schema={shapes}_status lua_blocks=pass late_schema=read damaged_rows={rows} "
            + "missing_sdk=noted unreadable_script=listed module_cache=evicts");
    }

    /** Valid JSON of the wrong shape gives a status, never a throw, and the script opens with Lua blocks. */
    private static int WrongSchema(string game)
    {
        var catalog = new MissionScriptCatalog(game);
        string path = MissionApiSchema.InstalledPath(catalog.SdkRoot);
        var fixture = JsonNode.Parse(File.ReadAllText(ApiNodeChecks.Fixture))!;
        const string shape = "mission_api.json has the wrong shape: ";
        var cases = new (string Name, string Json, string Status)[]
        {
            ("not json", "{", "mission_api.json is not valid JSON: "),
            ("array root", "[]", shape),
            ("schema name", Changed(fixture, root => root["schema"] = "sunrise-mission-lua-api-v0"), "mission_api.json has schema "),
            ("receivers object", Changed(fixture, root => root["receivers"] = new JsonObject()), shape),
            ("number kinds", Changed(fixture, root => root["value_kinds"] = new JsonArray(1, 2)), shape),
            ("string flag", Changed(fixture, root => root["members"]![0]!["colon"] = "yes"), shape),
            ("duplicate receiver", Changed(fixture, root => root["receivers"]!.AsArray().Add(root["receivers"]![0]!.DeepClone())), shape),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var (index, (name, json, expected)) in cases.Index())
        {
            File.WriteAllText(path, json);
            // The catalog re-reads only on a new write time.
            File.SetLastWriteTimeUtc(path, stamp.AddMinutes(index));
            var (schema, status) = catalog.ApiState;
            Check(schema is null && status.StartsWith(expected, StringComparison.Ordinal), $"{name}: status was \"{status}\".");
        }
        var entry = catalog.Scripts.Single();
        var references = catalog.ReadReferences(entry, catalog.Api?.MissionTables);
        var document = MissionGraphDocument.Parse(MissionScriptCatalog.ReadSource(entry.FullPath));
        var plain = MissionScriptGraph.Parse(document.Source, document.Groups);
        Check(entry.SdkModule == TestInstall.CleanModule && references.Count > 0 && plain.Api is null && StartCall(plain) is not MissionScriptNodeKind.ApiCall,
            "A script did not open as Lua blocks without a schema.");
        File.Copy(ApiNodeChecks.Fixture, path, true);
        File.SetLastWriteTimeUtc(path, stamp.AddHours(1));
        var typed = MissionScriptGraph.Parse(document.Source, document.Groups, new MissionApiContext(catalog.Api!, references));
        Check(catalog.ApiStatus.Length == 0 && StartCall(typed) == MissionScriptNodeKind.ApiCall, "The repaired schema did not type the call.");
        return cases.Length;
    }

    /** A schema the game writes after the catalog was built is read without a new catalog. */
    private static void LateSchema(string game)
    {
        var catalog = new MissionScriptCatalog(game);
        Check(catalog.ApiState == (null, MissingApi), "A missing schema did not say so: " + catalog.ApiStatus);
        string path = MissionApiSchema.InstalledPath(catalog.SdkRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(ApiNodeChecks.Fixture, path);
        Check(catalog.Api is not null && catalog.ApiStatus.Length == 0, "A schema written later was not read: " + catalog.ApiStatus);
    }

    /**
     * A cut-short module gives the rows before the cut and names itself; a repaired one clears the problem.
     * @return The rows read, as "damaged/full".
     */
    private static string DamagedModule(string game)
    {
        var catalog = new MissionScriptCatalog(game);
        var clean = catalog.Scripts.Single();
        var damaged = catalog.DescribeSource("test_damaged", "test_damaged/test_damaged.lua", TestInstall.Script(TestInstall.DamagedKey));
        var full = catalog.ReadReferences(clean);
        var rows = catalog.ReadReferences(damaged);
        string problem = catalog.SdkProblem(TestInstall.DamagedModule);
        Check(damaged.SdkModule == TestInstall.DamagedModule && rows.Count > 0 && rows.Count < full.Count
            && problem.StartsWith($"SDK file {TestInstall.DamagedModule} is damaged (", StringComparison.Ordinal)
            && catalog.SdkProblem(TestInstall.CleanModule).Length == 0,
            $"A damaged module did not keep its rows or name itself: rows={rows.Count} of {full.Count}, problem=\"{problem}\".");
        File.WriteAllText(TestInstall.ModuleFile(game, TestInstall.DamagedModule), TestInstall.Module("test_damaged", "80aa0002"));
        Check(catalog.ReadReferences(damaged).Count == full.Count && catalog.SdkProblem(TestInstall.DamagedModule).Length == 0,
            "A repaired module kept its problem.");
        return $"{rows.Count}/{full.Count}";
    }

    /** A missing index, module or lib/flow.lua each gives its own message. */
    private static void MissingFiles(string root)
    {
        string game = TestInstall.Write(Path.Combine(root, "no-activities"));
        File.Delete(Path.Combine(TestInstall.Sdk(game), "activities.lua"));
        var catalog = new MissionScriptCatalog(game);
        Check(catalog.Activities.Count == 0 && catalog.Scripts.Single().SdkModule == TestInstall.CleanModule
            && catalog.SdkProblem(null) == "SDK file activities.lua is missing. Start the game to rebuild it.",
            "A missing activities.lua was not noted: " + catalog.SdkProblem(null));
        const string noFlow = "scripts/lib/flow.lua is missing. Update the mission scripts.";
        Check(MissionScriptFactory.FlowRuntimeProblem(catalog) == noFlow, "A missing lib/flow.lua was not noted.");
        string flow = Path.Combine(TestInstall.Scripts(game), "lib", "flow.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(flow)!);
        File.WriteAllText(flow, "return {}\n");
        Check(MissionScriptFactory.FlowRuntimeProblem(catalog).Length == 0, "A present lib/flow.lua was still missing.");

        game = TestInstall.Write(Path.Combine(root, "no-modules"));
        File.Delete(TestInstall.ModuleFile(game, TestInstall.CleanModule));
        File.Delete(TestInstall.ModuleFile(game, "activities.test_damaged_0002"));
        catalog = new MissionScriptCatalog(game);
        Check(catalog.ReadReferences(catalog.Scripts.Single()).Count == 0
            && catalog.SdkProblem(TestInstall.CleanModule) == $"SDK file {TestInstall.CleanModule} is missing. Start the game to rebuild it.",
            "A missing module was not noted: " + catalog.SdkProblem(TestInstall.CleanModule));
        var activity = catalog.Activities.Single(value => value.Key == TestInstall.DamagedKey);
        string refused = "";
        try { catalog.EntryForActivity(activity); }
        catch (InvalidDataException error) { refused = error.Message; }
        Check(refused == "SDK file activities.test_damaged_0002 is missing. Start the game to rebuild it.",
            "A missing activity module was not refused by name: " + refused);
    }

    /** A script that does not read stays listed without a module; the others still read. */
    private static void UnreadableScripts(string game)
    {
        string scripts = TestInstall.Scripts(game);
        string text = Path.Combine(scripts, "bad_text", "bad_text.lua"), large = Path.Combine(scripts, "large", "large.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(text)!);
        File.WriteAllBytes(text, [(byte)'-', (byte)'-', (byte)' ', 0xC3, 0x28, (byte)'\n']);
        Directory.CreateDirectory(Path.GetDirectoryName(large)!);
        // One byte over the 8 MiB script cap.
        var bytes = new byte[8 * 1024 * 1024 + 1];
        Array.Fill(bytes, (byte)' ');
        bytes[0] = bytes[1] = (byte)'-';
        File.WriteAllBytes(large, bytes);
        var catalog = new MissionScriptCatalog(game);
        Check(catalog.Scripts.Select(value => value.Name).SequenceEqual(["bad_text", "large", "test_mission"])
            && catalog.Scripts[0].SdkModule is null && catalog.Scripts[1].SdkModule is null
            && catalog.Scripts[2].SdkModule == TestInstall.CleanModule && catalog.ReadReferences(catalog.Scripts[2]).Count > 0,
            "An unreadable script hid the others: " + string.Join(", ", catalog.Scripts.Select(value => value.Name + "=" + value.SdkModule)));
        Reject(() => MissionScriptCatalog.ReadSource(text), "invalid UTF-8");
        Reject(() => MissionScriptCatalog.ReadSource(large), "over the size cap");
    }

    /** The module cache drops its oldest key past the cap and re-reads a file whose stamp changed. */
    private static void ModuleCache(string game)
    {
        var catalog = new MissionScriptCatalog(game);
        var entry = catalog.Scripts.Single();
        int cap = (int)typeof(MissionScriptCatalog).GetField("CachedModules", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        int Count() => ((ICollection)typeof(MissionScriptCatalog).GetField("referenceCache", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(catalog)!).Count;
        // Each extra table list is its own cache key.
        IReadOnlyList<MissionSdkReference> Read(int key) => catalog.ReadReferences(entry, key < 0 ? null : ["mission.Extra" + key]);
        var first = Read(-1);
        Check(ReferenceEquals(first, Read(-1)), "A repeated read missed the cache.");
        var others = Enumerable.Range(0, cap).Select(Read).ToArray();
        Check(Count() == cap && ReferenceEquals(others[^1], Read(cap - 1)), "The newest key left the cache.");
        var again = Read(-1);
        Check(!ReferenceEquals(first, again) && again.SequenceEqual(first) && Count() == cap, "The oldest key was not evicted.");
        File.AppendAllText(TestInstall.ModuleFile(game, TestInstall.CleanModule), "-- regenerated\n");
        var fresh = Read(-1);
        Check(!ReferenceEquals(fresh, again) && fresh.SequenceEqual(first) && Count() == cap, "A stale stamp was served from the cache.");
    }

    /** The kind of the first node in on_start. */
    private static MissionScriptNodeKind StartCall(MissionScriptGraph view) =>
        view.Body(view.Node(MissionScriptGraph.Root + "/field/on_start")).Nodes[0].Kind;

    private static string Changed(JsonNode fixture, Action<JsonNode> change)
    {
        var copy = fixture.DeepClone();
        change(copy);
        return copy.ToJsonString();
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Reject(Action action, string what)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException) { return; }
        throw new InvalidDataException("A script was read " + what + ".");
    }
}
