using System.Text.Json.Nodes;
using Solforge.Graphs;

// Solforge.Graphs checks: typed pins and conversions, the add menu, editing with undo, clipboard, align, functions,
// validation and the material, weapon and mission compilers. Needs no game.
var failures = new List<string>();
int passed = 0;
void Expect(bool condition, string what) { if (condition) passed++; else failures.Add(what); }
bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

// Pins and values.
Expect(PinTypes.Compatible(PinType.Float, PinType.Numeric) && PinTypes.Compatible(PinType.Color, PinType.Numeric), "numeric wildcard accepts numbers");
Expect(!PinTypes.Connectable(PinType.Exec, PinType.Float), "exec never feeds data");
Expect(PinTypes.Conversion(PinType.Int, PinType.Float) == "conv.int_float" && PinTypes.Conversion(PinType.Texture, PinType.Float) is null, "conversion table");
Expect(GraphValue.Parse(PinType.Color, "#FF000080") is { X: 1, Y: 0, Z: 0 } hexRed && Near(hexRed.W, 128 / 255.0), "hex colour parse");
Expect(GraphValue.Parse(PinType.Vector3, "2") is { X: 2, Y: 2, Z: 2 }, "scalar splats to vector");
Expect(GraphValue.Float(.5).As(PinType.Color) is { X: .5, W: 1 }, "float to colour keeps alpha 1");
foreach (var (from, to, id) in PinTypes.Conversions) Expect(NodeLibrary.Find(id) is not null, $"conversion node {id} exists");

// Add menu.
var floatTargets = NodeLibrary.Search(GraphKind.Material, "", PinType.Float, true);
Expect(floatTargets.Count > 0 && floatTargets.All(result => result.PinId is not null), "dragged float offers linkable types");
Expect(floatTargets.All(result => result.Type.Kinds.HasFlag(GraphKind.Material)), "search stays in graph kind");
Expect(!NodeLibrary.Search(GraphKind.Material, "", PinType.Texture, true).Any(result => result.Type.Id == "math.add"), "texture does not offer Add");
Expect(NodeLibrary.Search(GraphKind.Mission, "branch").FirstOrDefault()?.Type.Id == "flow.branch", "search ranks title match first");
Expect(NodeLibrary.Search(GraphKind.Mission, "", PinType.Exec, true).All(result => result.Type.Inputs.Any(pin => pin.Type == PinType.Exec)), "exec drag offers exec inputs");
Expect(!NodeLibrary.Search(GraphKind.Weapon, "set shader").Any(), "material nodes not in weapon menu");

// Editing, conversions, undo.
var material = GraphDocument.Create(GraphKind.Material, "Red", "0x80AB1234");
var editor = new GraphEditor(material);
var integer = editor.AddNode("const.int", 0, 0, new Dictionary<string, string> { ["value"] = "3" });
var multiply = editor.AddNode("math.multiply", 250, 0);
Expect(editor.Connect(integer.Id, "value", multiply.Id, "a") is null, "int feeds numeric directly");
var scalar = editor.AddNode("const.float", 0, 120, new Dictionary<string, string> { ["value"] = "0.25" });
var setConstant = editor.AddNode(NodeLibrary.SetConstant, 500, 0, new Dictionary<string, string> { ["vector"] = "2", ["mask"] = "xyz" });
Expect(editor.Connect(scalar.Id, "value", setConstant.Id, "value") is null, "float to colour links");
Expect(editor.Nodes.Any(node => node.Type == "conv.float_color"), "conversion node inserted");
Expect(editor.Connect(multiply.Id, "result", multiply.Id, "b") is not null, "self link refused");
Expect(editor.Connect(setConstant.Id, "value", integer.Id, "value") is not null, "input to output refused");
int before = editor.Nodes.Count;
editor.Remove([scalar.Id]);
Expect(editor.Nodes.Count == before - 1 && editor.Undo() && editor.Nodes.Count == before, "undo restores deleted node");
Expect(editor.Redo() && editor.Nodes.Count == before - 1, "redo deletes again");
editor.Undo();
// Single input replaces its link.
var other = editor.AddNode("const.float", 0, 240, new Dictionary<string, string> { ["value"] = "2" });
editor.Connect(other.Id, "value", multiply.Id, "a");
Expect(editor.Links.Count(link => link.ToNode == multiply.Id && link.ToPin == "a") == 1, "single input keeps one link");
// Data loop refused.
var add = editor.AddNode("math.add", 400, 300);
editor.Connect(multiply.Id, "result", add.Id, "a");
Expect(editor.Connect(add.Id, "result", multiply.Id, "b") is not null, "data loop refused");

// Clipboard, duplicate, align, distribute.
editor.Selection.Clear(); editor.Selection.UnionWith([multiply.Id, add.Id]);
string clip = editor.Copy();
var pasted = editor.Paste(clip, 1000, 1000);
Expect(pasted.Count == 2 && editor.Links.Any(link => pasted.Contains(link.FromNode) && pasted.Contains(link.ToNode)), "paste keeps inner link");
Expect(editor.Paste("not a graph", 0, 0).Count == 0, "paste ignores foreign text");
var row = new[] { editor.AddNode("const.float", 0, 10), editor.AddNode("const.float", 100, 50), editor.AddNode("const.float", 900, 90) };
editor.Selection.Clear(); editor.Selection.UnionWith(row.Select(node => node.Id));
editor.Align(AlignMode.Top);
Expect(row.All(node => node.Y == 10), "align top");
editor.Distribute(true);
float gap1 = row[1].X - (row[0].X + GraphLayout.Width), gap2 = row[2].X - (row[1].X + GraphLayout.Width);
Expect(Math.Abs(gap1 - gap2) < .01f && row[0].X == 0 && row[2].X == 900, "distribute evens gaps");
var knotLink = editor.Links.First(link => link.ToNode == multiply.Id);
var knot = editor.InsertReroute(knotLink, 120, 40);
Expect(editor.Links.Any(link => link.ToNode == knot.Id) && editor.PinsOf(knot)[0].Type != PinType.Any, "reroute takes the wire's type");
var comment = editor.AddComment(0, 0, "Row");
Expect(comment.Width > GraphLayout.Width, "comment wraps selection");
float rowX = row[0].X;
editor.Move([comment.Id], 50, 0);
Expect(row[0].X == rowX + 50, "comment drags its nodes");

// Round trip.
var reloaded = GraphDocument.FromJson(editor.Document.ToJson());
Expect(reloaded.Nodes.Count == editor.Document.Nodes.Count && reloaded.Links.Count == editor.Document.Links.Count
    && reloaded.Comments.Count == 1 && reloaded.Kind == GraphKind.Material, "json round trip");

// Material compile: (3 * 2) feeds nothing; 0.25 splat into vector 2 xyz; preview colour from a parameter.
var red = GraphDocument.Create(GraphKind.Material, "Red", "0x80AB1234");
var redEditor = new GraphEditor(red);
var tint = redEditor.AddNode("mat.vector_param", 0, 0, new Dictionary<string, string> { ["name"] = "Tint", ["value"] = "#FF0000FF" });
var half = redEditor.AddNode("const.float", 0, 100, new Dictionary<string, string> { ["value"] = "0.5" });
var scaled = redEditor.AddNode("math.multiply", 200, 0);
redEditor.Connect(tint.Id, "value", scaled.Id, "a");
redEditor.Connect(half.Id, "value", scaled.Id, "b");
var write = redEditor.AddNode(NodeLibrary.SetConstant, 400, 0, new Dictionary<string, string> { ["vector"] = "3", ["mask"] = "rgb" });
redEditor.Connect(scaled.Id, "result", write.Id, "value");
redEditor.Connect(tint.Id, "value", red.Nodes[0].Id, "base_color");
var texture = redEditor.AddNode("mat.texture", 0, 300, new Dictionary<string, string> { ["tag"] = "0x80001111" });
var slot = redEditor.AddNode(NodeLibrary.SetTexture, 400, 300, new Dictionary<string, string> { ["slot"] = "1" });
redEditor.Connect(texture.Id, "texture", slot.Id, "texture");
var compiled = MaterialGraphCompiler.Compile(red);
Expect(!GraphValidation.HasErrors(compiled.Diagnostics), "material compiles: " + string.Join("; ", compiled.Diagnostics));
Expect(compiled.Target == "0x80AB1234" && compiled.Constants.Count == 1 && compiled.Constants[0] is { Vector: 3, Stage: "Pixel" } constant
    && Near(constant.Values[0], .5) && Near(constant.Values[1], 0) && constant.Components.SequenceEqual([0, 1, 2]), "constant folded to 0.5,0,0 mask rgb");
Expect(compiled.Textures.Single() is { Slot: 1, Texture: "0x80001111" }, "texture slot write");
Expect(Near(compiled.Preview.BaseColor[0], 1) && Near(compiled.Preview.BaseColor[1], 0), "preview base colour");
Expect(compiled.Parameters.Single().Name == "Tint", "parameter list");
var beforeClash = red.Nodes.Select(node => node.Id).ToHashSet();
var clash = redEditor.AddNode(NodeLibrary.SetConstant, 400, 150, new Dictionary<string, string> { ["vector"] = "3", ["mask"] = "x" });
redEditor.Connect(half.Id, "value", clash.Id, "value");
Expect(GraphValidation.HasErrors(MaterialGraphCompiler.Compile(red).Diagnostics), "overlapping constant writes are errors");

// Function collapse and expand keep the result.
// The clash link may insert a converter node; remove everything the clash added.
redEditor.Remove(red.Nodes.Select(node => node.Id).Where(id => !beforeClash.Contains(id)).ToList());
redEditor.Selection.Clear(); redEditor.Selection.UnionWith([half.Id, scaled.Id]);
var function = redEditor.CollapseToFunction("Darken");
Expect(red.Functions.Count == 1 && function.Inputs.Count == 1 && function.Outputs.Count == 1, "collapse makes a function with one input and output");
var viaFunction = MaterialGraphCompiler.Compile(red);
Expect(!GraphValidation.HasErrors(viaFunction.Diagnostics) && Near(viaFunction.Constants.Single().Values[0], .5), "function inlines when compiling: "
    + string.Join("; ", viaFunction.Diagnostics));
string callId = redEditor.Selection.Single();
Expect(redEditor.Search("darken").Any(result => result.Type.Title == "Call Darken"), "functions appear in the add menu");
redEditor.ExpandFunctionCall(callId);
Expect(red.Nodes.All(node => node.Type != NodeLibrary.FunctionCall) && Near(MaterialGraphCompiler.Compile(red).Constants.Single().Values[0], .5), "expand restores the body");
redEditor.Undo();
Expect(redEditor.Document.Nodes.Any(node => node.Type == NodeLibrary.FunctionCall), "undo restores the call");

// Weapon compile.
var weapon = GraphDocument.Create(GraphKind.Weapon, "Sol Fang", "0x80CC0001");
var weaponEditor = new GraphEditor(weapon);
string output = weapon.Nodes[0].Id;
var name = weaponEditor.AddNode("const.string", 0, 0, new Dictionary<string, string> { ["value"] = "Sol Fang" });
weaponEditor.Connect(name.Id, "value", output, "name");
var impact = weaponEditor.AddNode("item.stat", 0, 100, new Dictionary<string, string> { ["stat"] = "Impact" });
var base_ = weaponEditor.AddNode("const.float", -200, 100, new Dictionary<string, string> { ["value"] = "40" });
var plus = weaponEditor.AddNode("math.add", -100, 100, new Dictionary<string, string> { ["b"] = "4.6" });
weaponEditor.Connect(base_.Id, "value", plus.Id, "a");
weaponEditor.Connect(plus.Id, "result", impact.Id, "value");
weaponEditor.Connect(impact.Id, "stat", output, "stats");
var frame = weaponEditor.AddNode("item.archetype", 0, 200, new Dictionary<string, string> { ["frame"] = "Aggressive" });
var perk = weaponEditor.AddNode("item.perk", 0, 300, new Dictionary<string, string> { ["perk"] = "Rampage" });
weaponEditor.Connect(frame.Id, "perk", output, "perks");
weaponEditor.Connect(perk.Id, "perk", output, "perks");
Expect(weapon.Links.Count(link => link.ToNode == output && link.ToPin == "perks") == 2, "multi input keeps every link");
var custom = weaponEditor.AddNode(NodeLibrary.CustomPerk, 0, 400, new Dictionary<string, string> { ["name"] = "Sun Bite" });
var onKill = weaponEditor.AddNode("perk.on_kill", 200, 400);
var magazineGate = weaponEditor.AddNode("perk.magazine_percentage", 400, 400, new Dictionary<string, string> { ["percent"] = "50" });
var damageBuff = weaponEditor.AddNode("perk.add_damage_scalar", 600, 400, new Dictionary<string, string> { ["scalar"] = "1.25", ["duration"] = "6" });
var perkStat = weaponEditor.AddNode("item.stat", -200, 400, new Dictionary<string, string> { ["stat"] = "Handling", ["value"] = "10" });
weaponEditor.Connect(perkStat.Id, "stat", custom.Id, "stats");
weaponEditor.Connect(custom.Id, "perk", output, "perks");
weaponEditor.Connect(custom.Id, "perk", onKill.Id, "perk");
weaponEditor.Connect(onKill.Id, "then", magazineGate.Id, "exec");
weaponEditor.Connect(magazineGate.Id, "then", damageBuff.Id, "exec");
var weaponResult = WeaponGraphCompiler.Compile(weapon);
Expect(!GraphValidation.HasErrors(weaponResult.Diagnostics), "weapon compiles: " + string.Join("; ", weaponResult.Diagnostics));
Expect(weaponResult.WorkflowFields.Any(pair => pair is { Key: "Name", Value: "Sol Fang" }) && !weaponResult.Ops.Any(op => op.Field == "Name"),
    "name goes to the item workflow, not set_field");
var rarityNode = weaponEditor.AddNode("item.rarity", 0, 500, new Dictionary<string, string> { ["rarity"] = "Exotic" });
weaponEditor.Connect(rarityNode.Id, "field", output, "fields");
var visualsNode = weaponEditor.AddNode("item.visuals", 0, 550, new Dictionary<string, string> { ["icon"] = "0x80AA0001" });
weaponEditor.Connect(visualsNode.Id, "field", output, "fields");
var withRarity = WeaponGraphCompiler.Compile(weapon);
Expect(withRarity.Ops.Any(op => op is { Op: "set_field", Field: "Rarity", Value: "5" }), "rarity is the tier byte");
Expect(withRarity.WorkflowFields.Any(pair => pair is { Key: "Icon from item", Value: "0x80AA0001" }), "icon goes to the workflow");
Expect(WeaponGraphCompiler.RarityNames.Select((rarity, tier) => SunriseStudio.Packages.InvestmentItemKinds.TierName((byte)tier) == rarity).All(same => same),
    "rarity names match the investment tier table");
Expect(weaponResult.Ops.Any(op => op is { Op: "add_stat", Stat: "Impact", Value: "45" }), "stat value folds and rounds");
Expect(weaponResult.Ops.Any(op => op is { Op: "add_perk", Perk: "Aggressive Frame" }) && weaponResult.Ops.Any(op => op is { Op: "add_perk", Perk: "Rampage" }), "perks become add_perk");
Expect(weaponResult.CustomPerks.Single() is { Perk: "Sun Bite", Event: "on_kill" } rule && rule.Conditions.Single().Type == "magazine_percentage"
    && rule.Actions.Single().Type == "add_damage_scalar", "custom perk rule");
// Custom perk lowering (Phase 27): the rule becomes a native sandbox perk program.
var sunBite = weaponResult.Definitions.Single();
Expect(sunBite is { Name: "Sun Bite", Socket: -1 } && sunBite.Stats.Single() == ("Handling", 10), "custom perk keeps its plug stats");
Expect(!GraphValidation.Validate(weapon).Any(item => item.NodeId == perkStat.Id && item.Severity == DiagnosticSeverity.Warning), "perk stats count as linked");
Expect(sunBite.Native is { Ok: true, IsPassive: false } && sunBite.Native.Activation.Single() is { Primitive: "state-kill", StateProperty: PerkLowering.MagazineProperty } gate
    && Near(gate.Values["maximum"], .5) && Near(gate.Values["minimum"], 0) && gate.InnerValues["this_weapon"] == 1, "magazine gate lowers to state-kill");
Expect(sunBite.Native.Effects.Single() is { Primitive: "damage-multiplier" } buff && Near(buff.Values["bonus"], .25), "damage scalar lowers to a bonus");
Expect(Near(sunBite.Native.Deactivation.Single().Values["seconds"], 6), "buff duration is the deactivation timer");
Expect(WeaponGraphCompiler.ToJson(weaponResult)["perkDefinitions"]?.AsArray().Count == 1, "json lists perk definitions");
Expect(weaponResult.Ops.First().ToArguments("0x80CC0001")["item"]?.ToString() == "0x80CC0001", "ops carry the item");
var stray = weaponEditor.AddNode("item.perk", 0, 600, new Dictionary<string, string> { ["perk"] = "Outlaw" });
Expect(GraphValidation.Validate(weapon).Any(item => item.NodeId == stray.Id && item.Severity == DiagnosticSeverity.Warning), "unlinked perk warns");

// Custom perk lowering: precision, reload refill, Guardian kills and unsupported nodes.
CustomPerkDefinition LabPerk(string name, params PerkRule[] rules)
{
    var lab = new CustomPerkDefinition(name, "", "", -1, [], rules, "n1");
    return lab with { Native = PerkLowering.Lower(lab) };
}
PerkStep Step(string type, params (string Key, string Value)[] values) => new(type, values.ToDictionary(pair => pair.Key, pair => pair.Value));
var precise = LabPerk("Headhunter", new PerkRule("Headhunter", "", "on_precision_hit", [], [Step("grant_super_energy", ("percent", "2"))]));
Expect(precise.Native.Ok && precise.Native.Activation.Single() is { Primitive: "damage" } hit && hit.AnyLabels["source"].Single() == PerkLowering.PrecisionLabel
    && precise.Native.Effects.Single() is { Primitive: "ability-energy" } energy && Near(energy.Values["amount"], .02) && energy.Values["slot"] == 1,
    "precision hit filters on the precision label");
Expect(Near(precise.Native.Deactivation.Single().Values["seconds"], PerkLowering.RearmSeconds), "instant perks re-arm");
var reload = LabPerk("Quick Refill", new PerkRule("Quick Refill", "", "on_reload", [], [Step("refill_magazine", ("percent", "40"))]));
Expect(reload.Native.Ok && reload.Native.Activation.Single().Primitive == "reload" && Near(reload.Native.Effects.Single().Values["this_fraction"], .4), "reload refill");
var hunter = LabPerk("Duelist", new PerkRule("Duelist", "", "on_kill", [Step("target_type", ("target", "Guardian"))], [Step("spawn_orb", ("count", "2"))]));
Expect(hunter.Native.Ok && hunter.Native.Activation.Single().AnyLabels["victim"].Single() == PerkLowering.PlayerLabel
    && hunter.Native.Effects.Single().Values["count"] == 2, "Guardian kills filter the victim");
var twoEvents = LabPerk("Twin", new PerkRule("Twin", "", "on_kill", [], [Step("spawn_orb")]), new PerkRule("Twin", "", "on_precision_kill", [], [Step("spawn_orb")]));
Expect(twoEvents.Native.Ok && twoEvents.Native.Activation.Count == 2, "events sharing actions share one program");
var mixed = LabPerk("Mixed", new PerkRule("Mixed", "", "on_kill", [], [Step("spawn_orb")]), new PerkRule("Mixed", "", "on_reload", [], [Step("refill_magazine")]));
Expect(!mixed.Native.Ok, "events with different actions are refused");
var lifeSteal = LabPerk("Life Steal", new PerkRule("Life Steal", "", "on_kill", [Step("health_below", ("percent", "50"))], [Step("heal")]));
Expect(!lifeSteal.Native.Ok && lifeSteal.Native.Diagnostics.Count(item => item.Severity == DiagnosticSeverity.Error) == 2, "health and heal cannot be packed");
var passive = LabPerk("Steady");
Expect(!passive.Native.Ok, "a perk with no event and no stat is refused");
var statsOnly = new CustomPerkDefinition("Steady", "", "", -1, [("Stability", 20)], [], "n2");
Expect(PerkLowering.Lower(statsOnly) is { Ok: true, IsPassive: true }, "a stats-only perk packs as a plain plug");
Expect(LabPerk("Huge", new PerkRule("Huge", "", "on_kill", [], [Step("add_damage_scalar", ("scalar", "50"))])).Native.Ok == false, "scalar range is checked");

// Mission compile.
var mission = GraphDocument.Create(GraphKind.Mission, "Sol Test");
var missionEditor = new GraphEditor(mission);
string start = mission.Nodes[0].Id;
var objective = missionEditor.AddNode("mission.set_directive", 250, 0, new Dictionary<string, string> { ["module"] = "EDZ_FREEROAM", ["directive"] = "Defeat the \"Wardens\"" });
var delay = missionEditor.AddNode("flow.delay", 500, 0, new Dictionary<string, string> { ["seconds"] = "5" });
var signal = missionEditor.AddNode("flow.send_signal", 750, 0, new Dictionary<string, string> { ["signal"] = "BossVulnerable" });
var onBoss = missionEditor.AddNode("mission.on_signal", 0, 200, new Dictionary<string, string> { ["signal"] = "BossVulnerable" });
var branch = missionEditor.AddNode("flow.branch", 250, 200);
var flag = missionEditor.AddNode("state.get_flag", 0, 300, new Dictionary<string, string> { ["name"] = "boss_seen" });
var log = missionEditor.AddNode("mission.log", 500, 200, new Dictionary<string, string> { ["text"] = "boss" });
missionEditor.Connect(start, "then", objective.Id, "exec");
missionEditor.Connect(objective.Id, "then", delay.Id, "exec");
missionEditor.Connect(delay.Id, "completed", signal.Id, "exec");
missionEditor.Connect(onBoss.Id, "then", branch.Id, "exec");
missionEditor.Connect(flag.Id, "value", branch.Id, "condition");
missionEditor.Connect(branch.Id, "false", log.Id, "exec");
var lua = MissionGraphCompiler.Compile(mission);
Expect(!GraphValidation.HasErrors(lua.Diagnostics), "mission compiles: " + string.Join("; ", lua.Diagnostics));
Expect(lua.Handlers.Contains("on_start") && lua.Handlers.Contains("on_event_timer_elapsed"), "handlers include start and the delay tick");
Expect(lua.Lua.Contains("mission_modules.EDZ_FREEROAM = require(missions.EDZ_FREEROAM)")
    && lua.Lua.Contains("mission_modules.EDZ_FREEROAM.Directive[\"Defeat the \\\"Wardens\\\"\"]")
    && lua.Lua.Contains("context:slot(directive.sensor):set_directive{directive = directive}"), "objective is a directive on its sensor");
Expect(lua.Lua.Contains("sf.signal(context, state, event, \"BossVulnerable\")") && lua.Lua.Contains("signal_handlers[\"BossVulnerable\"]"), "signals wired");
Expect(lua.Lua.Contains("if (state:variable(\"sf.f.boss_seen\") == true) then") && lua.Lua.Contains("else"), "branch with flag");
// Delay is a named server timer; timer_elapsed dispatches by its name, so a reload resumes it.
string delayKey = MissionGraphCompiler.Key("sf.d.", delay.Id);
Expect(lua.Lua.Contains($"context:start_timer(\"{delayKey}\", sf.ms(5))") && lua.Lua.Contains($"delays[\"{delayKey}\"] = \"{delay.Id}\"")
    && lua.Lua.Contains("local id = delays[event.timer_name]"), "delay uses start_timer and timer_name dispatch");
Expect(lua.Lua.Contains("context:log(string.sub(tostring(\"boss\"), 1, 400))"), "log goes to context:log");
// Only what the server sandbox and context bind.
foreach (string banned in new[] { "print(", "os.", "pcall", "__mode", "pairs(", "setmetatable", "sf.call", "get_time", "set_objective" })
    Expect(!lua.Lua.Replace("ipairs(", "").Contains(banned), "generated Lua avoids " + banned);
var contextCalls = System.Text.RegularExpressions.Regex.Matches(lua.Lua, @"context:([a-z_]+)\(|context:([a-z_]+)\{").Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Distinct().ToList();
Expect(contextCalls.Count > 0 && contextCalls.All(call => NodeLibrary.MissionContextFunctions.Contains(call)), "every context call is a bound function: " + string.Join(",", contextCalls));
// Save and load keep the delay and compile the same.
var reloadedMission = GraphDocument.FromJson(mission.ToJson());
Expect(reloadedMission.Nodes.Single(node => node.Id == delay.Id).Get("seconds") == "5" && MissionGraphCompiler.Compile(reloadedMission).Lua == lua.Lua, "delay survives save and load");
Expect(MissionGraphCompiler.Key("sf.v.", "a b!") == "sf.v.a_b_" && MissionGraphCompiler.Key("sf.d.", new string('x', 100)).Length == 63, "variable keys follow the server key rule");
// Removed guesses report why.
var oldGraph = GraphDocument.FromJson(mission.ToJson());
oldGraph.Nodes.Add(new GraphNode { Id = "old1", Type = "mission.set_objective" });
Expect(GraphValidation.Validate(oldGraph).Any(item => item.NodeId == "old1" && item.Message.Contains("directive")), "removed node explains the replacement");
var badCall = missionEditor.AddNode("mission.call", 900, 0, new Dictionary<string, string> { ["function"] = "spawn_squad" });
missionEditor.Connect(signal.Id, "then", badCall.Id, "exec");
Expect(GraphValidation.Validate(mission).Any(item => item.NodeId == badCall.Id && item.Severity == DiagnosticSeverity.Error), "unbound SDK function is an error");
missionEditor.Document.Nodes.Single(node => node.Id == badCall.Id).Properties["function"] = "set_phase";
Expect(MissionGraphCompiler.Compile(mission).Lua.Contains($"values[\"{badCall.Id}.result\"] = context:set_phase()"), "bound SDK function is called directly");
Expect(lua.Lua.TrimEnd().EndsWith('}'), "module returns the handler table");
var orphan = missionEditor.AddNode("mission.log", 0, 600);
Expect(GraphValidation.Validate(mission).Any(item => item.NodeId == orphan.Id && item.Message.StartsWith("Never runs")), "unreachable action warns");

// JSON ops (the MCP edit path).
var scripted = new GraphEditor(GraphDocument.Create(GraphKind.Material, "Scripted", "0x80AB0001"));
var result = GraphCommands.Apply(scripted, JsonNode.Parse("""
    [
      {"op": "add_node", "type": "const.color", "ref": "c", "properties": {"value": "#00FF00FF"}},
      {"op": "add_node", "type": "mat.set_constant", "ref": "w", "x": 400, "y": 0, "properties": {"vector": "1"}},
      {"op": "connect", "from": "$c", "fromPin": "value", "to": "$w", "toPin": "value"},
      {"op": "add_node", "type": "item.perk"},
      {"op": "explode"}
    ]
    """)!.AsArray());
Expect((int?)result["applied"] == 3 && result["errors"]!.AsArray().Count == 2, "ops apply and report errors: " + result.ToJsonString());
var scriptedCompile = GraphCommands.Compile(scripted.Document);
Expect((bool?)scriptedCompile["ok"] == true && Near((double)scriptedCompile["constants"]![0]!["values"]![1]!, 1), "ops result compiles: " + scriptedCompile.ToJsonString());
Expect(GraphCommands.Describe(scripted.Document)["nodes"]!.AsArray().Count == 3, "describe lists nodes");
Expect(GraphCommands.NodeTypes(GraphKind.Weapon, "perk", null, true).Count > 3, "node type listing");

// Live material preview: the debounce, fields still being typed and what a compile writes.
var debounce = new LiveDebounce(.05, .1);
Expect(!debounce.Update("a", 0) && !debounce.Update("a", .04) && debounce.Update("a", .06) && debounce.Sent == "a", "debounce waits until the value is still");
Expect(!debounce.Update("a", .5), "debounce does not send the same value twice");
Expect(!debounce.Update("b", 1) && !debounce.Update("c", 1.04) && !debounce.Update("d", 1.08) && debounce.Update("e", 1.11) && debounce.Sent == "e",
    "debounce still sends during a long drag");
debounce.Reset();
Expect(debounce.Sent is null && !debounce.Update("e", 2) && debounce.Update("e", 2.1), "reset sends the same value again");
var liveColor = scripted.Document.Nodes.First(node => node.Type == "const.color");
var liveDrafts = new Dictionary<string, string> { [liveColor.Id + "/value"] = "#FF0000FF", ["missing/value"] = "1", ["no-slash"] = "2" };
var drafted = MaterialLivePreview.WithDrafts(scripted.Document, liveDrafts);
var draftedCompile = MaterialGraphCompiler.Compile(drafted);
Expect(!ReferenceEquals(drafted, scripted.Document) && liveColor.Get("value") == "#00FF00FF", "drafts leave the graph unchanged");
Expect(!GraphValidation.HasErrors(draftedCompile.Diagnostics) && Near(draftedCompile.Constants[0].Values[0], 1) && Near(draftedCompile.Constants[0].Values[1], 0),
    "drafts reach the live compile");
Expect(ReferenceEquals(MaterialLivePreview.WithDrafts(scripted.Document, new Dictionary<string, string>()), scripted.Document), "no drafts, no copy");
Expect(MaterialLivePreview.DraftKey(new Dictionary<string, string> { ["b"] = "1", ["a"] = "2" })
    == MaterialLivePreview.DraftKey(new Dictionary<string, string> { ["a"] = "2", ["b"] = "1" }), "draft key ignores order");
string liveSignature = MaterialLivePreview.Signature(MaterialGraphCompiler.Compile(scripted.Document));
liveColor.X += 80;
Expect(MaterialLivePreview.Signature(MaterialGraphCompiler.Compile(scripted.Document)) == liveSignature, "moving a node sends nothing new");
Expect(MaterialLivePreview.Signature(draftedCompile) != liveSignature, "a new value changes the signature");

Console.WriteLine($"Solforge.Graphs checks: {passed} passed, {failures.Count} failed.");
foreach (string failure in failures) Console.WriteLine("FAIL: " + failure);
return failures.Count == 0 ? 0 : 1;
