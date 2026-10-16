namespace Solforge.Graphs;

public enum DiagnosticSeverity { Info, Warning, Error }

/** A problem shown on a node (NodeId) or on the graph; Scope is the function id when it is inside one. */
public sealed record GraphDiagnostic(DiagnosticSeverity Severity, string Message, string? NodeId = null, string? Scope = null)
{
    public override string ToString() => $"{Severity}: {(NodeId is null ? "" : $"[{(Scope is null ? "" : Scope + "/")}{NodeId}] ")}{Message}";
}

/** Live validation: structure and types for every graph, plus the rules of each graph kind. */
public static class GraphValidation
{
    public static IReadOnlyList<GraphDiagnostic> Validate(GraphDocument document)
    {
        var result = new List<GraphDiagnostic>();
        CheckScope(document, null, result);
        foreach (var function in document.Functions) CheckScope(document, function, result);
        CheckFunctions(document, result);
        switch (document.Kind)
        {
            case GraphKind.Material: CheckMaterial(document, result); break;
            case GraphKind.Weapon: CheckWeapon(document, result); break;
            case GraphKind.Mission: CheckMission(document, result); break;
        }
        return result;
    }

    public static bool HasErrors(IEnumerable<GraphDiagnostic> diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static void CheckScope(GraphDocument document, GraphFunction? scope, List<GraphDiagnostic> result)
    {
        var nodes = scope?.Nodes ?? document.Nodes;
        var links = scope?.Links ?? document.Links;
        string? scopeId = scope?.Id;
        var byId = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!byId.TryAdd(node.Id, node)) result.Add(new(DiagnosticSeverity.Error, $"Duplicate node id {node.Id}.", node.Id, scopeId));
            if (NodeLibrary.Find(node.Type) is not { } type)
                result.Add(new(DiagnosticSeverity.Error, NodeLibrary.RemovedMissionNodes.TryGetValue(node.Type, out var removed)
                    ? $"'{node.Type}' was removed: {removed}" : $"Unknown node type '{node.Type}'.", node.Id, scopeId));
            else if ((type.Kinds & document.Kind) == 0)
                result.Add(new(DiagnosticSeverity.Error, $"{type.Title} does not belong in a {document.Kind} graph.", node.Id, scopeId));
        }
        foreach (var link in links)
        {
            if (!byId.TryGetValue(link.FromNode, out var from) || !byId.TryGetValue(link.ToNode, out var to))
            { result.Add(new(DiagnosticSeverity.Error, $"Link to a missing node ({link.FromNode} -> {link.ToNode}).", null, scopeId)); continue; }
            var output = document.Pin(from, link.FromPin, true, scope);
            var input = document.Pin(to, link.ToPin, false, scope);
            if (output is null || input is null)
            { result.Add(new(DiagnosticSeverity.Error, $"Link to a missing pin ({link.FromPin} -> {link.ToPin}).", to.Id, scopeId)); continue; }
            if (!PinTypes.Compatible(output.Type, input.Type))
                result.Add(new(DiagnosticSeverity.Error,
                    $"{PinTypes.Label(output.Type)} does not connect to {PinTypes.Label(input.Type)} input '{input.Name}'"
                    + (PinTypes.Conversion(output.Type, input.Type) is null ? "." : "; relink it to insert a conversion."), to.Id, scopeId));
        }
        foreach (var node in nodes)
        {
            if (NodeLibrary.Find(node.Type) is null) continue;
            foreach (var pin in document.PinsOf(node, scope))
            {
                int count = pin.Output ? links.Count(link => link.FromNode == node.Id && link.FromPin == pin.Id)
                    : links.Count(link => link.ToNode == node.Id && link.ToPin == pin.Id);
                if (!pin.Output && pin.Type != PinType.Exec && !pin.Multi && count > 1)
                    result.Add(new(DiagnosticSeverity.Error, $"Input '{pin.Name}' has {count} links; it takes one.", node.Id, scopeId));
                if (pin.Output && pin.Type == PinType.Exec && count > 1)
                    result.Add(new(DiagnosticSeverity.Warning, $"Exec output '{Display(pin)}' has {count} links; use a Sequence for order.", node.Id, scopeId));
                if (!pin.Output && pin.Required && count == 0 && !node.Properties.ContainsKey(pin.Id))
                    result.Add(new(DiagnosticSeverity.Error, $"Input '{Display(pin)}' needs a link.", node.Id, scopeId));
            }
        }
        // Data loops.
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        bool Visit(string id)
        {
            if (state.TryGetValue(id, out int mark)) return mark == 1;
            state[id] = 1;
            foreach (var link in links.Where(link => link.FromNode == id))
            {
                if (!byId.TryGetValue(id, out var node) || document.Pin(node, link.FromPin, true, scope)?.Type == PinType.Exec) continue;
                if (Visit(link.ToNode)) { result.Add(new(DiagnosticSeverity.Error, "Data loop: a value depends on itself.", link.ToNode, scopeId)); state[id] = 2; return false; }
            }
            state[id] = 2;
            return false;
        }
        foreach (var node in nodes) Visit(node.Id);
        if (scope is not null)
        {
            if (nodes.Count(node => node.Type == NodeLibrary.FunctionInput) != 1 || nodes.Count(node => node.Type == NodeLibrary.FunctionOutput) != 1)
                result.Add(new(DiagnosticSeverity.Error, $"Function {scope.Name} needs exactly one Function Inputs and one Function Outputs node.", null, scopeId));
        }
        foreach (var call in nodes.Where(node => node.Type == NodeLibrary.FunctionCall))
            if (document.Function(call.Get("function")) is null)
                result.Add(new(DiagnosticSeverity.Error, $"Calls a missing function '{call.Get("function")}'.", call.Id, scopeId));
    }

    private static string Display(PinDef pin) => pin.Name.Length > 0 ? pin.Name : pin.Id;

    /** Functions may call each other but not recursively (they are inlined). */
    private static void CheckFunctions(GraphDocument document, List<GraphDiagnostic> result)
    {
        foreach (var function in document.Functions)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>(function.Nodes.Where(node => node.Type == NodeLibrary.FunctionCall).Select(node => node.Get("function")));
            while (stack.TryPop(out var id))
            {
                if (id == function.Id) { result.Add(new(DiagnosticSeverity.Error, $"Function {function.Name} calls itself.", null, function.Id)); break; }
                if (!seen.Add(id) || document.Function(id) is not { } inner) continue;
                foreach (var node in inner.Nodes.Where(node => node.Type == NodeLibrary.FunctionCall)) stack.Push(node.Get("function"));
            }
        }
    }

    private static void CheckMaterial(GraphDocument document, List<GraphDiagnostic> result)
    {
        var flat = GraphFlatten.Flatten(document);
        var outputs = flat.OfType(NodeLibrary.MaterialOutput).ToList();
        if (outputs.Count != 1) result.Add(new(DiagnosticSeverity.Error, $"A material graph needs one Material Output (has {outputs.Count})."));
        else if (TagText(outputs[0].Get("target")) is null)
            result.Add(new(DiagnosticSeverity.Warning, "Material Output has no target material tag; it previews but cannot apply.", outputs[0].Id));
        foreach (var node in flat.OfType(NodeLibrary.SetConstant))
        {
            string mask = node.Get("mask", "xyzw");
            if (mask.Length == 0 || mask.Any(c => "xyzwrgba".IndexOf(char.ToLowerInvariant(c)) < 0))
                result.Add(new(DiagnosticSeverity.Error, $"Mask '{mask}' must use x, y, z, w (or r, g, b, a).", node.Id));
            if (!int.TryParse(node.Get("vector", "0"), out int vector) || vector < 0)
                result.Add(new(DiagnosticSeverity.Error, "Vector must be a whole number from 0.", node.Id));
        }
        foreach (var node in flat.OfType("mat.texture"))
            if (TagText(node.Get("tag")) is null) result.Add(new(DiagnosticSeverity.Warning, "Texture has no tag.", node.Id));
    }

    private static void CheckWeapon(GraphDocument document, List<GraphDiagnostic> result)
    {
        var flat = GraphFlatten.Flatten(document);
        var outputs = flat.OfType(NodeLibrary.WeaponOutput).ToList();
        if (outputs.Count != 1) { result.Add(new(DiagnosticSeverity.Error, $"A weapon graph needs one Weapon Output (has {outputs.Count}).")); return; }
        var output = outputs[0];
        if (output.Get("target").Trim().Length == 0)
            result.Add(new(DiagnosticSeverity.Warning, "Weapon Output has no target item; it cannot apply.", output.Id));
        var feeding = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pin in new[] { "fields", "stats", "perks", "name", "description" })
            foreach (var (node, _) in flat.Sources(output.Id, pin)) feeding.Add(node.Id);
        foreach (var node in flat.Nodes.Where(node => node.Type is "item.stat" or "item.remove_stat" or "item.perk" or "item.remove_perk"
                     or "item.field" or "item.visuals" or "item.archetype" or "item.rarity"))
            if (!feeding.Contains(node.Id))
                result.Add(new(DiagnosticSeverity.Warning, "Not linked to Weapon Output; it is ignored.", node.Id));
        foreach (var node in flat.Nodes.Where(node => node.Type is "item.perk" or "item.remove_perk"))
            if (node.Get("perk").Trim().Length == 0) result.Add(new(DiagnosticSeverity.Error, "Name the perk.", node.Id));
        foreach (var node in flat.Nodes.Where(node => node.Type == "item.field"))
            if (node.Get("field").Trim().Length == 0) result.Add(new(DiagnosticSeverity.Error, "Name the field (get_item lists them).", node.Id));
        foreach (var perk in flat.OfType(NodeLibrary.CustomPerk))
            if (!flat.Targets(perk.Id, "perk").Any(target => NodeLibrary.Find(target.Node.Type)?.IsEvent == true))
                result.Add(new(DiagnosticSeverity.Warning, "Custom Perk has no event; link it to On Hit, On Kill and so on.", perk.Id));
    }

    private static void CheckMission(GraphDocument document, List<GraphDiagnostic> result)
    {
        var flat = GraphFlatten.Flatten(document);
        var events = flat.Nodes.Where(node => NodeLibrary.Find(node.Type)?.IsEvent == true).ToList();
        if (events.Count == 0) { result.Add(new(DiagnosticSeverity.Error, "A mission graph needs an event (Mission Start, Activity Event or On Signal).")); return; }
        // Everything with an exec input should be reachable from an event (or from a Delay / Wait continuation, which is reachable itself).
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<GraphNode>(events);
        while (stack.TryPop(out var node))
        {
            if (!reached.Add(node.Id)) continue;
            foreach (var pin in NodeLibrary.Find(node.Type)?.Outputs.Where(pin => pin.Type == PinType.Exec) ?? Enumerable.Empty<PinDef>())
                foreach (var (target, _) in flat.Targets(node.Id, pin.Id)) stack.Push(target);
        }
        foreach (var node in flat.Nodes)
            if (NodeLibrary.Find(node.Type) is { } type && type.Inputs.Any(pin => pin.Type == PinType.Exec) && !reached.Contains(node.Id))
                result.Add(new(DiagnosticSeverity.Warning, "Never runs: no event leads here.", node.Id));
        foreach (var node in flat.OfType("mission.call"))
            if (!NodeLibrary.MissionContextFunctions.Contains(node.Get("function"), StringComparer.Ordinal))
                result.Add(new(DiagnosticSeverity.Error, $"The server's mission context has no function '{node.Get("function")}'.", node.Id));
        foreach (var node in flat.Nodes.Where(node => node.Type is "mission.set_directive"))
            if (!LuaName(node.Get("module")) || node.Get("directive").Length == 0)
                result.Add(new(DiagnosticSeverity.Error, "Set Directive needs the mission module (missions.lua key) and a directive name.", node.Id));
        if (flat.OfType("flow.delay").Count() > MissionGraphCompiler.MaxTimers)
            result.Add(new(DiagnosticSeverity.Warning, $"The server keeps at most {MissionGraphCompiler.MaxTimers} mission timers; more Delays than that cannot all wait at once."));
        foreach (var node in flat.OfType("flow.finish_branch"))
            if (flat.Node(node.Get("parallel")) is not { Type: "flow.parallel_all" })
                result.Add(new(DiagnosticSeverity.Error, "Parallel node id must name a Parallel All node.", node.Id));
    }

    internal static bool LuaName(string text) =>
        text.Length > 0 && (char.IsAsciiLetter(text[0]) || text[0] == '_') && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /** A tag written as 0x... hex, a decimal hash or a name; null when empty. */
    public static string? TagText(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
