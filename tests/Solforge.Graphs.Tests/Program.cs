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
var clash = redEditor.AddNode(NodeLibrary.SetConstant, 400, 150, new Dictionary<string, string> { ["vector"] = "3", ["mask"] = "x" });
redEditor.Connect(half.Id, "value", clash.Id, "value");
Expect(GraphValidation.HasErrors(MaterialGraphCompiler.Compile(red).Diagnostics), "overlapping constant writes are errors");

// Function collapse and expand keep the result.
redEditor.Remove([clash.Id]);
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
var healthBelow = weaponEditor.AddNode("perk.health_below", 400, 400);
var heal = weaponEditor.AddNode("perk.heal", 600, 400);
weaponEditor.Connect(custom.Id, "perk", output, "perks");
weaponEditor.Connect(custom.Id, "perk", onKill.Id, "perk");
weaponEditor.Connect(onKill.Id, "then", healthBelow.Id, "exec");
weaponEditor.Connect(healthBelow.Id, "then", heal.Id, "exec");
var weaponResult = WeaponGraphCompiler.Compile(weapon);
Expect(!GraphValidation.HasErrors(weaponResult.Diagnostics), "weapon compiles: " + string.Join("; ", weaponResult.Diagnostics));
Expect(weaponResult.Ops.Any(op => op is { Op: "set_field", Field: "Name", Value: "Sol Fang" }), "name becomes set_field");
Expect(weaponResult.Ops.Any(op => op is { Op: "add_stat", Stat: "Impact", Value: "45" }), "stat value folds and rounds");
Expect(weaponResult.Ops.Any(op => op is { Op: "add_perk", Perk: "Aggressive Frame" }) && weaponResult.Ops.Any(op => op is { Op: "add_perk", Perk: "Rampage" }), "perks become add_perk");
Expect(weaponResult.CustomPerks.Single() is { Perk: "Sun Bite", Event: "on_kill" } rule && rule.Conditions.Single().Type == "health_below"
    && rule.Actions.Single().Type == "heal", "custom perk rule");
Expect(weaponResult.Ops.First().ToArguments("0x80CC0001")["item"]?.ToString() == "0x80CC0001", "ops carry the item");
var stray = weaponEditor.AddNode("item.perk", 0, 600, new Dictionary<string, string> { ["perk"] = "Outlaw" });
Expect(GraphValidation.Validate(weapon).Any(item => item.NodeId == stray.Id && item.Severity == DiagnosticSeverity.Warning), "unlinked perk warns");

// Mission compile.
var mission = GraphDocument.Create(GraphKind.Mission, "Sol Test");
var missionEditor = new GraphEditor(mission);
string start = mission.Nodes[0].Id;
var objective = missionEditor.AddNode("mission.set_objective", 250, 0, new Dictionary<string, string> { ["text"] = "Defeat the \"Wardens\"" });
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
Expect(lua.Lua.Contains("sf.call(context, \"set_objective\", \"Defeat the \\\"Wardens\\\"\")"), "objective call quoted");
Expect(lua.Lua.Contains("sf.signal(context, state, event, \"BossVulnerable\")") && lua.Lua.Contains("signal_handlers[\"BossVulnerable\"]"), "signals wired");
Expect(lua.Lua.Contains("if (sf.vars(state)[\"boss_seen\"] == true) then") && lua.Lua.Contains("else"), "branch with flag");
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

Console.WriteLine($"Solforge.Graphs checks: {passed} passed, {failures.Count} failed.");
foreach (string failure in failures) Console.WriteLine("FAIL: " + failure);
return failures.Count == 0 ? 0 : 1;
