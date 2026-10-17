using Solforge.Graphs;
using SunriseStudio.Missions;

/**
 * Mission-library calls (lib.mission_lib: transitions, cutscenes, boss phases, ordered endings): the content
 * templates use them, the script graph shows them as titled nodes with their values, and an edit round-trips.
 */
internal static class LibraryChecks
{
    public static void Synthetic()
    {
        // Every call renders and parses back to the same values.
        foreach (var call in MissionLibrary.Calls)
        {
            var values = MissionLibrary.WithDefaults(call, null);
            foreach (var parameter in call.Parameters.Where(parameter => parameter.Required)) values[parameter.Name] = "Slot.REQUIRED";
            Check(MissionLibrary.Check(call, values).Count == 0, call.Function + " defaults with required slots have problems.");
            string lua = MissionLibrary.Render(call, values);
            int open = lua.IndexOf('(');
            var match = MissionLibrary.Match(lua[..open], lua[(open + 1)..^1]);
            Check(match is not null && match.Call == call && match.Unknown.Count == 0
                && values.Where(pair => pair.Value.Length > 0).All(pair => match.Values[pair.Key] == pair.Value), call.Function + " does not parse back.");
            Check(MissionLibrary.Check(call, new Dictionary<string, string>()).Count == call.Parameters.Count(parameter => parameter.Required),
                call.Function + " required fields are not reported.");
        }
        Check(MissionLibrary.Match("campaign.transition", "context, {}") is null && MissionLibrary.Match("lib.transition", "context, {}") is not null,
            "Only mission_lib/lib callees match.");
        Check(MissionLibrary.Check(MissionLibrary.Find("boss_phase")!, new Dictionary<string, string> { ["boss"] = "Slot.B", ["at_health"] = "half" }).Count == 1,
            "A bad number is not reported.");

        // The content templates call the library and pass the SDK names.
        foreach (var type in new[] { MissionActivityType.CampaignCinematic, MissionActivityType.StrikeBoss, MissionActivityType.RaidEncounter })
        {
            Check(MissionTemplates.UsesMissionLibrary(type), type + " is not a library template.");
            string lua = MissionTemplates.Create(type, "M", "k", TemplateChecks.Sdk);
            Check(lua.Contains(MissionLibrary.Require), type + " does not require lib.mission_lib.");
            var calls = Calls(lua);
            Check(calls.Count >= 2 && calls.Contains("ending"), type + " lacks its library calls: " + string.Join(",", calls));
            Check(MissionTemplates.Check(lua, TemplateChecks.Sdk).Count == 0, type + " has findings with a full SDK.");
            Check(MissionTemplates.Check(MissionTemplates.Create(type, "M", "k", []), []).Count > 0, type + " lists no placeholders without an SDK.");
            MissionGraphDocument.Parse(lua);
        }
        string cinematic = MissionTemplates.Create(MissionActivityType.CampaignCinematic, "M", "k", TemplateChecks.Sdk);
        Check(cinematic.Contains("cinematic = Slot.CINEMATIC_1") && cinematic.Contains("teleport = Slot.SPAWN_2"), "Cinematic and spawn slots not picked.");
        string strike = MissionTemplates.Create(MissionActivityType.StrikeBoss, "M", "k", TemplateChecks.Sdk);
        Check(strike.Contains("boss = Slot.SQ_BOSS") && strike.Contains("pylons = {Slot.PYLON_1, Slot.PYLON_2}") && strike.Contains("barrier = true"),
            "Strike boss phase lacks its boss, pylons or barrier.");
        Check(MissionTemplates.Parse("strike_boss") == MissionActivityType.StrikeBoss && MissionTemplates.Parse("raid-encounter") == MissionActivityType.RaidEncounter
            && MissionTemplates.Parse("campaign cinematic") == MissionActivityType.CampaignCinematic, "Library template ids do not parse.");

        // Script graph: a library call in a callback is a titled node with its values; editing it keeps the rest.
        string source = MissionLibrary.InsertIntoLua("local x = 1\nreturn {\n    on_start = function(context, state) end,\n}\n", "on_start",
            MissionLibrary.Render(MissionLibrary.Find("transition")!, new Dictionary<string, string> { ["teleport"] = "Slot.SPAWN_2", ["signal"] = "moved" }));
        Check(source.Contains(MissionLibrary.Require) && source.Contains("    on_start = function(context, state)\n        mission_lib.transition(context, {")
            && source.Contains("\n    end,\n"), "InsertIntoLua did not open the one-line callback: " + source);
        var graph = MissionScriptGraph.Parse(source);
        var node = graph.LibraryCalls().SingleOrDefault();
        Check(node is not null && node.Title == "Fade Transition" && node.LibCall!.Values["teleport"] == "Slot.SPAWN_2"
            && node.LibCall.Values["signal"] == "moved", "Script graph does not show the transition node.");
        var arguments = node!.Inputs.Single(input => input.Name == "Arguments");
        string edited = graph.SetInput(node, "Arguments", arguments.Span.Read(source).Replace("Slot.SPAWN_2", "Slot.SPAWN_3"));
        var again = MissionScriptGraph.Parse(edited).LibraryCalls().Single();
        Check(again.LibCall!.Values["teleport"] == "Slot.SPAWN_3" && edited.StartsWith(MissionLibrary.Require + "\nlocal x = 1\n", StringComparison.Ordinal)
            && edited.Replace("SPAWN_3", "SPAWN_2") == source, "Editing a library node does not round-trip.");
        Reject(() => MissionLibrary.InsertIntoLua(source, "missing_function", "x()"));
    }

    /** Library function names called in a script, in order. */
    private static List<string> Calls(string lua) => lua.Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.StartsWith(MissionLibrary.Alias + ".", StringComparison.Ordinal) && line.Contains('('))
        .Select(line => MissionLibrary.Match(line[..line.IndexOf('(')], line[(line.IndexOf('(') + 1)..line.LastIndexOf(')')])?.Call.Function ?? "?")
        .ToList();

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return; }
        throw new InvalidDataException("Bad input was accepted.");
    }
}
