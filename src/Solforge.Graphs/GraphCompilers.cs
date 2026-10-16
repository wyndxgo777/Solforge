using System.Globalization;
using System.Text.Json.Nodes;

namespace Solforge.Graphs;

/** One float4 constant write: stage and bank as MaterialInterchange names them, the vector index and the masked components. */
public sealed record MaterialConstantWrite(string Stage, string Bank, int Vector, double[] Values, string Mask, string NodeId)
{
    /** Component indexes the mask writes (x/r = 0 .. w/a = 3). */
    public IEnumerable<int> Components => Mask.ToLowerInvariant().Select(c => "xyzw".IndexOf(c) is >= 0 and var i ? i : "rgba".IndexOf(c)).Where(i => i >= 0).Distinct();
}

public sealed record MaterialTextureWrite(string Stage, int Slot, string Texture, string NodeId);

/** What the preview swatch shows. */
public sealed record MaterialPreview(double[] BaseColor, double Roughness, double Metalness, double[] Emissive, double Opacity);

public sealed record MaterialCompileResult(string Target, IReadOnlyList<MaterialConstantWrite> Constants,
    IReadOnlyList<MaterialTextureWrite> Textures, MaterialPreview Preview, IReadOnlyList<(string Name, string Value)> Parameters,
    IReadOnlyList<GraphDiagnostic> Diagnostics);

/** One apply_item_edit call against the target item. */
public sealed record ItemEditOp(string Op, string? Field = null, string? Value = null, string? Stat = null, string? Perk = null, string NodeId = "")
{
    /** The apply_item_edit arguments. */
    public JsonObject ToArguments(string item)
    {
        var args = new JsonObject { ["item"] = item, ["op"] = Op };
        if (Field is not null) args["field"] = Field;
        if (Value is not null) args["value"] = Value;
        if (Stat is not null) args["stat"] = Stat;
        if (Perk is not null) args["perk"] = Perk;
        return args;
    }
}

/** A step of a custom perk rule: condition or action type (node type without "perk.") and its values. */
public sealed record PerkStep(string Type, IReadOnlyDictionary<string, string> Values);

/** A custom perk behaviour (Studio.md Phase 26): event, conditions that must hold, then actions in order. */
public sealed record PerkRule(string Perk, string Description, string Event, IReadOnlyList<PerkStep> Conditions, IReadOnlyList<PerkStep> Actions);

/**
 * Ops are apply_item_edit calls; set_field reaches only the editable investment detail fields
 * (InvestmentDetails.ReadItem: Maximum stack, Inventory bucket, Rarity). Name, Description and the icon live
 * in the display row the item creation workflow writes (ItemWorkflow labels), so they are WorkflowFields.
 */
public sealed record WeaponCompileResult(string Target, IReadOnlyList<ItemEditOp> Ops, IReadOnlyList<PerkRule> CustomPerks,
    IReadOnlyList<GraphDiagnostic> Diagnostics)
{
    public IReadOnlyList<KeyValuePair<string, string>> WorkflowFields { get; init; } = [];
    /** One definition per Custom Perk node, with its native lowering (Studio.md Phase 27). */
    public IReadOnlyList<CustomPerkDefinition> Definitions { get; init; } = [];
}

public static class MaterialGraphCompiler
{
    public static MaterialCompileResult Compile(GraphDocument document)
    {
        var diagnostics = GraphValidation.Validate(document).ToList();
        var flat = GraphFlatten.Flatten(document);
        var evaluator = new GraphEvaluator(flat);
        var output = flat.OfType(NodeLibrary.MaterialOutput).FirstOrDefault();
        string target = output?.Get("target").Trim() ?? "";
        var preview = output is null ? new MaterialPreview([.5, .5, .5, 1], .5, 0, [0, 0, 0, 1], 1)
            : new MaterialPreview(Parts(evaluator.Input(output, "base_color").As(PinType.Color)), evaluator.Input(output, "roughness").X,
                evaluator.Input(output, "metalness").X, Parts(evaluator.Input(output, "emissive").As(PinType.Color)), evaluator.Input(output, "opacity").X);
        var constants = new List<MaterialConstantWrite>();
        var claimed = new Dictionary<(string, string, int, int), string>();
        foreach (var node in flat.OfType(NodeLibrary.SetConstant))
        {
            var value = evaluator.Input(node, "value").As(PinType.Color);
            int.TryParse(node.Get("vector", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int vector);
            var write = new MaterialConstantWrite(node.Get("stage", "Pixel"), node.Get("bank", NodeLibrary.Banks[0]), Math.Max(0, vector),
                Parts(value), node.Get("mask", "xyzw"), node.Id);
            foreach (int component in write.Components)
                if (!claimed.TryAdd((write.Stage, write.Bank, write.Vector, component), node.Id))
                    diagnostics.Add(new(DiagnosticSeverity.Error, $"{write.Stage} {write.Bank} vector {write.Vector}.{"xyzw"[component]} is also written by {claimed[(write.Stage, write.Bank, write.Vector, component)]}.", node.Id));
            constants.Add(write);
        }
        var textures = new List<MaterialTextureWrite>();
        foreach (var node in flat.OfType(NodeLibrary.SetTexture))
        {
            string texture = evaluator.Input(node, "texture").Text ?? "";
            int.TryParse(node.Get("slot", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot);
            if (texture.Length == 0) { diagnostics.Add(new(DiagnosticSeverity.Error, "Texture slot has no texture tag.", node.Id)); continue; }
            if (textures.Any(other => other.Stage == node.Get("stage", "Pixel") && other.Slot == slot))
                diagnostics.Add(new(DiagnosticSeverity.Error, $"Texture slot {slot} is set twice.", node.Id));
            textures.Add(new(node.Get("stage", "Pixel"), slot, texture, node.Id));
        }
        var parameters = flat.Nodes.Where(node => node.Type is "mat.scalar_param" or "mat.vector_param")
            .Select(node => (node.Get("name"), evaluator.Output(node, "value").ToString())).ToList();
        if (constants.Count == 0 && textures.Count == 0)
            diagnostics.Add(new(DiagnosticSeverity.Info, "Nothing writes the material yet; add Set Shader Constant or Set Texture Slot."));
        return new(target, constants, textures, preview, parameters, diagnostics);
    }

    private static double[] Parts(GraphValue value) => [value.X, value.Y, value.Z, value.W];

    public static JsonObject ToJson(MaterialCompileResult result) => new()
    {
        ["target"] = result.Target,
        ["constants"] = new JsonArray(result.Constants.Select(write => (JsonNode)new JsonObject
        {
            ["stage"] = write.Stage, ["bank"] = write.Bank, ["vector"] = write.Vector, ["mask"] = write.Mask,
            ["values"] = new JsonArray(write.Values.Select(value => (JsonNode)JsonValue.Create(Math.Round(value, 6))).ToArray()), ["node"] = write.NodeId
        }).ToArray()),
        ["textures"] = new JsonArray(result.Textures.Select(write => (JsonNode)new JsonObject
            { ["stage"] = write.Stage, ["slot"] = write.Slot, ["texture"] = write.Texture, ["node"] = write.NodeId }).ToArray()),
        ["preview"] = new JsonObject
        {
            ["baseColor"] = new JsonArray(result.Preview.BaseColor.Select(value => (JsonNode)JsonValue.Create(Math.Round(value, 4))).ToArray()),
            ["roughness"] = result.Preview.Roughness, ["metalness"] = result.Preview.Metalness, ["opacity"] = result.Preview.Opacity,
            ["emissive"] = new JsonArray(result.Preview.Emissive.Select(value => (JsonNode)JsonValue.Create(Math.Round(value, 4))).ToArray())
        },
        ["parameters"] = new JsonArray(result.Parameters.Select(parameter => (JsonNode)new JsonObject { ["name"] = parameter.Name, ["value"] = parameter.Value }).ToArray())
    };
}

public static class WeaponGraphCompiler
{
    public static WeaponCompileResult Compile(GraphDocument document)
    {
        var diagnostics = GraphValidation.Validate(document).ToList();
        var flat = GraphFlatten.Flatten(document);
        var evaluator = new GraphEvaluator(flat);
        var ops = new List<ItemEditOp>();
        var rules = new List<PerkRule>();
        var definitions = new List<CustomPerkDefinition>();
        if (flat.OfType(NodeLibrary.WeaponOutput).FirstOrDefault() is not { } output) return new("", ops, rules, diagnostics);
        string target = output.Get("target").Trim();

        var workflow = new List<KeyValuePair<string, string>>();
        foreach (var (pin, field) in new[] { ("name", "Name"), ("description", "Description") })
            if (evaluator.Linked(output, pin) || output.Properties.ContainsKey(pin))
                workflow.Add(new(field, evaluator.Input(output, pin).Text ?? evaluator.Input(output, pin).ToString()));
        foreach (var (node, pin) in flat.Sources(output.Id, "fields"))
            switch (node.Type)
            {
                case "item.field":
                {
                    string field = node.Get("field").Trim();
                    if (!EditableFields.Contains(field, StringComparer.OrdinalIgnoreCase))
                        diagnostics.Add(new(DiagnosticSeverity.Warning, $"apply_item_edit sets only {string.Join(", ", EditableFields)}; '{field}' will be refused.", node.Id));
                    ops.Add(new("set_field", field, evaluator.Input(node, "value").ToString(), NodeId: node.Id)); break;
                }
                case "item.rarity":
                {
                    string rarity = node.Get("rarity", "Legendary");
                    int index = Array.IndexOf(RarityNames, rarity);
                    if (index < 0) { diagnostics.Add(new(DiagnosticSeverity.Error, $"Unknown rarity '{rarity}'.", node.Id)); break; }
                    ops.Add(new("set_field", "Rarity", index.ToString(CultureInfo.InvariantCulture), NodeId: node.Id)); break;
                }
                case "item.visuals":
                    if (node.Get("icon").Trim() is { Length: > 0 } icon) workflow.Add(new("Icon from item", icon));
                    break;
                default:
                    diagnostics.Add(new(DiagnosticSeverity.Warning, $"Fields takes field nodes; {node.Type} pin {pin} is ignored.", node.Id)); break;
            }
        var stats = flat.Sources(output.Id, "stats").ToList();
        foreach (var (node, _) in stats.Where(source => source.Node.Type == "item.remove_stat"))
            ops.Add(new("remove_stat", Stat: node.Get("stat", "Impact"), NodeId: node.Id));
        foreach (var (node, _) in stats.Where(source => source.Node.Type == "item.stat"))
        {
            double value = evaluator.Input(node, "value").X;
            ops.Add(new("add_stat", Value: Math.Round(value).ToString(CultureInfo.InvariantCulture), Stat: node.Get("stat", "Impact"), NodeId: node.Id));
        }
        var perks = flat.Sources(output.Id, "perks").ToList();
        foreach (var (node, _) in perks.Where(source => source.Node.Type == "item.remove_perk"))
            ops.Add(new("remove_perk", Perk: node.Get("perk").Trim(), NodeId: node.Id));
        foreach (var (node, _) in perks)
            switch (node.Type)
            {
                case "item.perk": ops.Add(new("add_perk", Perk: node.Get("perk").Trim(), NodeId: node.Id)); break;
                case "item.archetype": ops.Add(new("add_perk", Perk: node.Get("frame", "Adaptive") + " Frame", NodeId: node.Id)); break;
                case NodeLibrary.CustomPerk:
                {
                    var perkRules = CustomPerkRules(flat, evaluator, node, diagnostics).ToList();
                    rules.AddRange(perkRules);
                    var definition = CustomPerk(flat, evaluator, node, perkRules, diagnostics);
                    definition = definition with { Native = PerkLowering.Lower(definition) };
                    diagnostics.AddRange(definition.Native.Diagnostics);
                    definitions.Add(definition);
                    break;
                }
            }
        if (definitions.GroupBy(definition => definition.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } twice)
            diagnostics.Add(new(DiagnosticSeverity.Error, $"Two Custom Perks are called '{twice.Key}'; perk names must differ.", twice.Last().NodeId));
        return new(target, ops, rules, diagnostics) { WorkflowFields = workflow, Definitions = definitions };
    }

    /** Display text, icon, socket and the plug stats linked to the node's Stats pin. */
    private static CustomPerkDefinition CustomPerk(FlatGraph flat, GraphEvaluator evaluator, GraphNode perk, IReadOnlyList<PerkRule> rules,
        List<GraphDiagnostic> diagnostics)
    {
        var stats = new List<(string Stat, int Value)>();
        foreach (var (source, _) in flat.Sources(perk.Id, "stats"))
        {
            if (source.Type != "item.stat") { diagnostics.Add(new(DiagnosticSeverity.Warning, "A Custom Perk's Stats take Stat nodes; this one is ignored.", source.Id)); continue; }
            stats.Add((source.Get("stat", "Impact"), (int)Math.Round(evaluator.Input(source, "value").X)));
        }
        if (!int.TryParse(perk.Get("socket", "-1").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int socket)) socket = -1;
        return new(perk.Get("name", "New Perk").Trim(), perk.Get("description").Trim(), perk.Get("icon").Trim(), socket, stats, rules, perk.Id);
    }

    /** InvestmentDetails.ReadItem's editable fields: what apply_item_edit set_field accepts. */
    public static readonly string[] EditableFields = ["Maximum stack", "Inventory bucket", "Rarity"];

    /** Tier bytes at investment +186 by index (InvestmentItemKinds.TierName): 0 None ... 5 Exotic. */
    public static readonly string[] RarityNames = ["None", "Common", "Uncommon", "Rare", "Legendary", "Exotic"];

    private static IEnumerable<PerkRule> CustomPerkRules(FlatGraph flat, GraphEvaluator evaluator, GraphNode perk, List<GraphDiagnostic> diagnostics)
    {
        foreach (var (eventNode, _) in flat.Targets(perk.Id, "perk"))
        {
            if (NodeLibrary.Find(eventNode.Type) is not { IsEvent: true }) continue;
            var conditions = new List<PerkStep>();
            var actions = new List<PerkStep>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            GraphNode? current = flat.Targets(eventNode.Id, "then").FirstOrDefault().Node;
            while (current is not null && seen.Add(current.Id))
            {
                var type = NodeLibrary.Find(current.Type);
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var pin in type?.Inputs.Where(pin => pin.Type != PinType.Exec) ?? Enumerable.Empty<PinDef>())
                    values[pin.Id] = evaluator.Input(current, pin.Id).ToString();
                foreach (var property in type?.Properties ?? Array.Empty<PropertyDef>()) values[property.Id] = current.Get(property.Id, property.Default);
                var step = new PerkStep(current.Type.StartsWith("perk.", StringComparison.Ordinal) ? current.Type[5..] : current.Type, values);
                if (type?.Category == "Perk Conditions") conditions.Add(step);
                else if (type?.Category == "Perk Actions") actions.Add(step);
                else diagnostics.Add(new(DiagnosticSeverity.Warning, "Only perk conditions and actions run in a perk chain.", current.Id));
                current = flat.Targets(current.Id, "then").FirstOrDefault().Node;
            }
            if (actions.Count == 0) diagnostics.Add(new(DiagnosticSeverity.Warning, "This perk event has no actions.", eventNode.Id));
            yield return new(perk.Get("name", "New Perk"), perk.Get("description"), eventNode.Type[5..], conditions, actions);
        }
    }

    public static JsonObject ToJson(WeaponCompileResult result) => new()
    {
        ["target"] = result.Target,
        ["ops"] = new JsonArray(result.Ops.Select(op => (JsonNode)op.ToArguments(result.Target)).ToArray()),
        ["workflowFields"] = new JsonObject(result.WorkflowFields.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value))),
        ["customPerks"] = new JsonArray(result.CustomPerks.Select(rule => (JsonNode)new JsonObject
        {
            ["perk"] = rule.Perk, ["description"] = rule.Description, ["event"] = rule.Event,
            ["conditions"] = new JsonArray(rule.Conditions.Select(Step).ToArray()),
            ["actions"] = new JsonArray(rule.Actions.Select(Step).ToArray())
        }).ToArray()),
        ["perkDefinitions"] = new JsonArray(result.Definitions.Select(definition => (JsonNode)PerkLowering.ToJson(definition)).ToArray())
    };

    private static JsonNode Step(PerkStep step)
    {
        var values = new JsonObject();
        foreach (var (key, value) in step.Values) values[key] = value;
        return new JsonObject { ["type"] = step.Type, ["values"] = values };
    }
}
