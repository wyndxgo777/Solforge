using System.Text.RegularExpressions;

namespace SunriseStudio.Missions;

/** Inputs is empty and Editable false when the list is custom Lua. */
public sealed record MissionStepInputs(IReadOnlyList<MissionSdkReference> Inputs, bool Editable);

/** Scene inputs are sent to a scene; scene outputs are reported by it. Both come from the generated SDK. */
public sealed partial class MissionVisualAuthoring
{
    /** A dotted Lua path such as mission.Slot.X; it fills PATH in the patterns below. */
    private const string PathPattern = @"[A-Za-z_][A-Za-z_0-9]*(?:\.[A-Za-z_][A-Za-z_0-9]*)+";
    private const string SceneSendPattern =
        @"context\s*:\s*scene\s*\(\s*(?<scene>PATH)\s*\)\s*:\s*send_event\s*\{\s*key\s*=\s*(?<key>PATH)\s*\}";
    private const string SceneOutputPattern = @"(?:context\s*:\s*slot\s*\(\s*(?<scene>PATH)\s*\)\s*\.\s*id"
        + @"|is_slot\s*\(\s*context\s*,\s*event\s*,\s*(?<scene>PATH)\s*\))\s*and\s*event\s*\.\s*key\s*==\s*(?<key>PATH)";

    /** @return The key a scene has in mission.Scene, mission.Slot, mission.SceneInput and mission.SceneOutput. */
    public static string SceneKey(MissionSdkReference scene) => scene.Expression[(scene.Expression.LastIndexOf('.') + 1)..];

    /** The authored scene for a key, or null. */
    public MissionSdkReference? AuthoredScene(string key) =>
        Choices(MissionReferenceKind.AuthoredScene).FirstOrDefault(value => SceneKey(value) == key);

    /** The scene slot for a key, or null. */
    public MissionSdkReference? SceneSlot(string key) => Choices(MissionReferenceKind.Scene).FirstOrDefault(value => SceneKey(value) == key);

    /** @param scene A mission.Scene or a scene slot reference; null gives no inputs. */
    public IReadOnlyList<MissionSdkReference> SceneInputs(MissionSdkReference? scene) => scene is null ? []
        : Choices(MissionReferenceKind.SceneInput).Where(value => value.Scene == SceneKey(scene)).ToArray();

    /** @param scene A mission.Scene or a scene slot reference; null gives no outputs. */
    public IReadOnlyList<MissionSdkReference> SceneOutputs(MissionSdkReference? scene) => scene is null ? []
        : Choices(MissionReferenceKind.SceneOutput).Where(value => value.Scene == SceneKey(scene)).ToArray();

    /** @return The mission.Scene entries that have at least one input. */
    public IReadOnlyList<MissionSdkReference> ScenesWithInputs()
    {
        var keys = Choices(MissionReferenceKind.SceneInput).Select(value => value.Scene).ToHashSet(StringComparer.Ordinal);
        return Choices(MissionReferenceKind.AuthoredScene).Where(value => keys.Contains(SceneKey(value))).ToArray();
    }

    /** @return The scene slots that have at least one output; an output report names the slot. */
    public IReadOnlyList<MissionSdkReference> ScenesWithOutputs()
    {
        var keys = Choices(MissionReferenceKind.SceneOutput).Select(value => value.Scene).ToHashSet(StringComparer.Ordinal);
        return Choices(MissionReferenceKind.Scene).Where(value => keys.Contains(SceneKey(value))).ToArray();
    }

    /** Scene inputs a step lists in its inputs field, and whether the list is plain enough to edit. */
    public MissionStepInputs StepInputs(string node)
    {
        if (graph.Field(node, "inputs") is not { } field) return new([], true);
        if (ListItems(field.Value.Read(graph.Source)) is not { } items) return new([], false);
        var inputs = new List<MissionSdkReference>();
        foreach (string item in items)
            if (Resolve(item) is { } input && Accepts(input, MissionReferenceKind.SceneInput)) inputs.Add(input);
            else return new([], false);
        return new(inputs, true);
    }

    /** Clearing the scene also clears the inputs; a new scene keeps only its own inputs. */
    public MissionGraphDocument SetStepScene(string node, MissionSdkReference? scene)
    {
        RequireCampaignStep(node);
        var current = StepInputs(node);
        var next = SetReference(node, "scene", MissionReferenceKind.AuthoredScene, scene);
        if (scene is null) return next.Field(node, "inputs") is null ? next : next.RemoveField(node, "inputs");
        if (!current.Editable || current.Inputs.All(value => value.Scene == SceneKey(scene))) return next;
        return new MissionVisualAuthoring(next, references).SetStepInputs(node,
            current.Inputs.Where(value => value.Scene == SceneKey(scene)).ToArray());
    }

    /** Writes inputs = lib.list(...); lib.list refuses a missing constant where a plain table would drop it. */
    public MissionGraphDocument SetStepInputs(string node, IReadOnlyList<MissionSdkReference> inputs)
    {
        RequireCampaignStep(node);
        if (!StepInputs(node).Editable) throw new NotSupportedException("These inputs are custom Lua; edit them in Lua.");
        if (inputs.Count == 0) return graph.Field(node, "inputs") is null ? graph : graph.RemoveField(node, "inputs");
        var scene = Current(node, "scene") is { } value && Accepts(value, MissionReferenceKind.AuthoredScene)
            ? value : throw new InvalidDataException("Choose the step's scene first.");
        foreach (var input in inputs)
        {
            Require(input, MissionReferenceKind.SceneInput);
            if (input.Scene != SceneKey(scene)) throw new InvalidDataException(input.Name + " is not an input of this scene.");
        }
        var (next, lib) = Import(graph, "lib");
        next = next.SetField(node, "inputs", lib + ".list(" + string.Join(", ", inputs.Select(Bindings.Reference)) + ")");
        next.RequireValid(); return next;
    }

    /** @return The scene slot and output of ends.output; either is null when absent or custom Lua. */
    public (MissionSdkReference? Scene, MissionSdkReference? Output) StepOutput(string node) =>
        (Current(node, "ends.output.slot"), Current(node, "ends.output.key"));

    /** The step ends when its scene reports this output; the slot comes from the output's scene. */
    public MissionGraphDocument SetSceneOutput(string node, MissionSdkReference? output)
    {
        RequireCampaignStep(node);
        if (output is null) return graph.Field(node, "ends.output") is null ? graph : graph.RemoveField(node, "ends.output");
        Require(output, MissionReferenceKind.SceneOutput);
        var slot = SceneSlot(output.Scene) ?? throw new InvalidDataException("This scene has no slot in the installed SDK.");
        var next = graph.SetField(node, "ends.output", "{ slot = " + Bindings.Reference(slot) + ", key = " + Bindings.Reference(output) + " }");
        next.RequireValid(); return next;
    }

    /**
     * Checks that need the SDK: keys must belong to their scene, and inputs need a scene.
     * @param sends False when typed API checks already cover send_event keys.
     */
    public IReadOnlyList<MissionGraphDiagnostic> SceneDiagnostics(bool sends = true)
    {
        var result = new List<MissionGraphDiagnostic>();
        foreach (var group in graph.Groups.Where(value => value.IsCampaignSteps))
            foreach (var node in group.Nodes.Where(value => value.Table is not null))
            {
                var scene = graph.Field(node.Key, "scene"); var inputs = graph.Field(node.Key, "inputs");
                if (inputs is not null && scene is null) result.Add(new($"{node.Id}: has scene inputs but no scene.", node.Key));
                if (scene is not null && Resolve(scene.Value.Read(graph.Source)) is { } named && !Accepts(named, MissionReferenceKind.AuthoredScene))
                    result.Add(new($"{node.Id}: scene must be a mission.Scene entry.", node.Key));
                if (inputs is not null && scene is not null)
                    foreach (string item in ListItems(inputs.Value.Read(graph.Source)) ?? [])
                        Check(node, scene.Value.Read(graph.Source), item, true);
                if (graph.Field(node.Key, "ends.output.slot") is { } slot && graph.Field(node.Key, "ends.output.key") is { } key)
                    Check(node, slot.Value.Read(graph.Source), key.Value.Read(graph.Source), false);
            }
        // Sends and output tests in any callback, in the forms Studio writes.
        var comments = graph.Script.Comments;
        (string Pattern, bool Input)[] patterns = sends
            ? [(SceneSendPattern, true), (SceneOutputPattern, false)] : [(SceneOutputPattern, false)];
        foreach (var (pattern, input) in patterns)
            foreach (Match match in Regex.Matches(graph.Source, pattern.Replace("PATH", PathPattern, StringComparison.Ordinal),
                RegexOptions.CultureInvariant))
                if (!comments.Any(span => span.Start <= match.Index && match.Index < span.End))
                    Check(graph.Nodes.LastOrDefault(node => node.Entry.Span.Start <= match.Index && match.Index < node.Entry.Span.End),
                        match.Groups["scene"].Value, match.Groups["key"].Value, input);
        // A flow hears a scene output only through this callback.
        if (!graph.Script.Tokens.Any(token => token.Kind != LuaTokenKind.String && token.Text == "on_event_scene_output"))
            foreach (var fact in graph.Groups.Where(value => value.IsFlowFacts).SelectMany(value => value.Nodes))
                if (graph.Field(fact.Key, "observe")?.Value.Read(graph.Source).Contains("SCENE_OUTPUT", StringComparison.Ordinal) == true)
                    result.Add(new($"{fact.Id}: waits for a scene output, but the script has no on_event_scene_output callback.", fact.Key));
        return result.DistinctBy(value => value.Message).ToArray();

        void Check(MissionGraphNode? node, string sceneExpression, string keyExpression, bool input)
        {
            string where = node is null ? "" : node.Id + ": ", expanded = Bindings.Expand(keyExpression);
            if (Resolve(keyExpression) is not { } key)
            {
                if (expanded.StartsWith("mission.SceneInput.", StringComparison.Ordinal)
                    || expanded.StartsWith("mission.SceneOutput.", StringComparison.Ordinal))
                    result.Add(new(where + expanded + " is not in the installed SDK.", node?.Key));
                return;
            }
            if (Resolve(sceneExpression) is not { } scene
                || !Accepts(scene, MissionReferenceKind.AuthoredScene) && !Accepts(scene, MissionReferenceKind.Scene)) return;
            if (Accepts(key, input ? MissionReferenceKind.SceneInput : MissionReferenceKind.SceneOutput) && key.Scene == SceneKey(scene)) return;
            result.Add(new($"{where}{expanded} is not {(input ? "an input" : "an output")} of scene {SceneKey(scene)}.", node?.Key));
        }
    }

    /** @return The entries of a {a, b} table or a lib.list(a, b) call; null for any other expression. */
    private IReadOnlyList<string>? ListItems(string expression)
    {
        LuaExpression value;
        try { value = LuaScript.ParseExpression(expression); }
        catch (InvalidDataException) { return null; }
        if (value is LuaTable table)
            return table.Fields.Any(field => field.Name is not null) ? null : table.Fields.Select(field => field.Value.Read(expression)).ToArray();
        if (value is LuaCall { Method: null, Arguments: { Form: LuaArgumentsForm.List, Values: var items } } call
            && Bindings.Expand(call.Target.Span.Read(expression)) == "mission_lib.list")
            return items.Select(item => item.Span.Read(expression)).ToArray();
        return null;
    }

    private MissionSdkReference? Current(string node, string path) =>
        graph.Field(node, path) is { } field ? Resolve(field.Value.Read(graph.Source)) : null;

    private void RequireCampaignStep(string node)
    {
        if (!graph.GroupOf(node).IsCampaignSteps)
            throw new InvalidOperationException("Scene fields belong to campaign steps.");
    }

    /**
     * Finds the script's binding for EventKind or lib, adding the require after the mission require when missing.
     * @return The document, changed only by the added require, and the name to write.
     */
    private static (MissionGraphDocument Document, string Name) Import(MissionGraphDocument document, string name)
    {
        var bindings = new MissionScriptBindings(document.Script);
        string? bound = name == "EventKind"
            ? bindings.Alias("activity_sdk.EventKind") ?? (bindings.Alias("activity_sdk") is { } sdk ? sdk + ".EventKind" : null)
            : bindings.Alias("mission_lib");
        if (bound is not null) return (document, bound);
        var tokens = document.Script.Tokens;
        if (tokens.Where((token, index) => token.Kind != LuaTokenKind.String && token.Text == name
            && (index == 0 || tokens[index - 1].Text is not ("." or ":"))).Any())
            throw new NotSupportedException($"The script uses {name} for something else; write this field in Lua.");
        int at = bindings.MissionEnd ?? throw new NotSupportedException("This script has no plain mission SDK binding.");
        string expression = name == "EventKind" ? "require(\"sunrise.activity_sdk\").EventKind" : "require(\"lib.mission_lib\")";
        return (MissionGraphDocument.Parse(document.Source.Insert(at, document.Newline + "local " + name + " = " + expression)), name);
    }

    private static string Label(MissionReferenceKind kind) => kind switch
    {
        MissionReferenceKind.AuthoredScene => "scene", MissionReferenceKind.SceneInput => "scene input",
        MissionReferenceKind.SceneOutput => "scene output", _ => kind.ToString()
    };
}
