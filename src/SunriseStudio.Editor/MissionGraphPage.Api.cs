using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Missions;
using SunriseStudio.Viewport;
using static SunriseStudio.Editor.StudioUi;

namespace SunriseStudio.Editor;

/** Typed Sunrise API nodes: parameter widgets from the generated schema, and palettes for new calls and callbacks. */
public sealed partial class MissionGraphPage
{
    private static readonly Vector4 ProblemColor = new(1, .55f, .45f, 1);
    private MissionApiSchema? apiSchema;
    private string apiStatus = "";
    private MissionApiContext? apiContext;
    // Parameters the user switched to the Lua expression editor, by node, call and path.
    private readonly HashSet<string> apiLua = new(StringComparer.Ordinal);
    // Text typed into a number or string field and not yet applied, by the same key.
    private readonly Dictionary<string, string> apiInput = new(StringComparer.Ordinal);
    private string apiFilter = "";
    // ApiChecks() result for the current analysis.
    private (MissionApiAnalysis? Analysis, IReadOnlyList<string> Lines) apiChecks = (null, []);
    /** Typed views of the selected node's calls; rebuilt when the script view or the selection changes. */
    private (MissionApiAnalysis? Analysis, string Key, MissionApiNode[] Nodes) apiNodes = (null, "", []);

    /** The schema comes with the installation; a missing or unreadable one leaves every call a Lua block. */
    private void AdoptApi(MissionApiSchema? schema, string status)
    {
        apiSchema = schema;
        apiStatus = status;
        apiLua.Clear(); apiInput.Clear();
    }

    private void RefreshApi()
    {
        if (apiSchema is null) { apiContext = null; return; }
        if (apiContext is null || !ReferenceEquals(apiContext.Schema, apiSchema) || !ReferenceEquals(apiContext.References, references))
            apiContext = new(apiSchema, references);
    }

    /** Why the open script's SDK module read short; empty when it read cleanly. */
    private string SdkProblem => catalog?.SdkProblem(script?.SdkModule) ?? "";

    /** Problems with this script's typing; a missing schema shows on the toolbar instead. */
    private void DrawApiStatus()
    {
        if (SdkProblem is { Length: > 0 } sdk) Problem(sdk);
        if (apiSchema is not null && scriptGraph?.ApiError is { Length: > 0 } error) ImGui.TextWrapped("Typed nodes: " + error);
    }

    /** Says why there is no schema, on the item drawn last. */
    private void ApiStatusTip() => Tip(apiStatus);

    private static string ApiKey(string node, int ordinal, string path) => node + "\n" + ordinal + "\n" + path;

    /** The runtime's argument checks for every typed call, as "line: call: problem"; cached per script view. */
    private IReadOnlyList<string> ApiChecks()
    {
        if (scriptGraph?.Api is not { } analysis) return [];
        if (ReferenceEquals(apiChecks.Analysis, analysis)) return apiChecks.Lines;
        var lines = new List<string>();
        foreach (var call in analysis.Calls.Where(value => value.Outcome == MissionApiOutcome.Typed))
            foreach (var finding in MissionApiNode.Build(analysis, call).Findings)
                lines.Add($"Line {analysis.Source.AsSpan(0, call.Call.Span.Start).Count('\n') + 1}: {call.Member!.Name}: {finding.Message}");
        apiChecks = (analysis, lines);
        return lines;
    }

    /** A typed node's parameter summary for the canvas. */
    private IReadOnlyList<string>? ApiRows(MissionScriptNode node)
    {
        if (node.ApiCall is { } call && scriptGraph?.Api is { } analysis)
        {
            var typed = MissionApiNode.Build(analysis, call);
            var rows = typed.Parameters.Where(value => value.Present).Select(value => value.Label + " = " + Clip(ParameterText(value))).ToList();
            if (typed.Computed) rows.Add("arguments from Lua");
            if (typed.Findings.Count > 0) rows.Insert(0, typed.Findings.Count == 1 ? "1 problem" : typed.Findings.Count + " problems");
            return rows;
        }
        if (node.ApiEvents is [var single]) return EventFields(single).Select(field => field.Name).ToArray();
        if (StartEntry(node) is not null) return apiSchema!.Program.StartParameters;
        return null;
    }

    /** The program entry of an on_start or on_load callback node; null for any other node. */
    private MissionApiValue? StartEntry(MissionScriptNode node) => node.Kind == MissionScriptNodeKind.Callback
        ? apiSchema?.Program.Entries.FirstOrDefault(entry => entry.Kind == "function" && entry.Name == node.Title) : null;

    private static string Clip(string text)
    {
        text = text.Replace("\r", "", StringComparison.Ordinal).Replace('\n', ' ');
        return text.Length > 34 ? text[..34] + "..." : text;
    }

    private static string ParameterText(MissionApiParameter parameter) => parameter switch
    {
        { Reference: { } reference } => reference.Name,
        { Choice: { } choice } => choice,
        { Widget: MissionApiWidget.List or MissionApiWidget.Table, Literal: true } => parameter.Children.Count(child => child.Present) + " entries",
        _ => parameter.Text
    };

    /** The event's own fields first; the fields every event has come last. */
    private static IEnumerable<MissionApiMember> EventFields(MissionApiEvent value) =>
        value.Fields.Where(field => field.Group != "common").Concat(value.Fields.Where(field => field.Group == "common"));

    /** A start callback's note, and the fields of each event a callback serves. */
    private void DrawApiEvent(MissionScriptNode node)
    {
        if (StartEntry(node) is { } entry)
        {
            ImGui.SeparatorText("Start callback");
            if (entry.Note is { } note) ImGui.TextWrapped(note);
        }
        foreach (var value in node.ApiEvents)
        {
            ImGui.SeparatorText(value.Name + " event");
            Tip("Fields of the event parameter");
            if (value.Note is { } note) ImGui.TextWrapped(note);
            foreach (var field in EventFields(value))
            {
                string type = field.Callable ? "method" : field.Type is { } kind ? TypeText(kind) + (kind.Optional ? ", may be nil" : "") : "";
                ImGui.BulletText(field.Name + "  " + type);
                if (ImGui.IsItemHovered() && (field.Note ?? field.Type?.Note) is { } hint) ImGui.SetTooltip(hint);
            }
        }
    }

    /** Every typed call of the node in source order; the statement's own call is open and the others fold. */
    private void DrawApiCalls(MissionScriptNode node, bool editable)
    {
        if (scriptGraph?.Api is not { } analysis || node.ApiCalls.Count == 0) return;
        if (!ReferenceEquals(apiNodes.Analysis, analysis) || apiNodes.Key != node.Key)
            apiNodes = (analysis, node.Key, node.ApiCalls.Select(call => MissionApiNode.Build(analysis, call)).ToArray());
        for (int ordinal = 0; ordinal < node.ApiCalls.Count; ordinal++)
        {
            var call = node.ApiCalls[ordinal];
            var typed = apiNodes.Nodes[ordinal];
            bool main = ReferenceEquals(call, node.ApiCall);
            ImGui.PushID("api" + ordinal);
            if (main) ImGui.SeparatorText("Call: " + typed.Title);
            bool open = main || ImGui.CollapsingHeader("Call: " + typed.Title + "##api-" + ordinal);
            Tip(CallSummary(typed));
            if (open) DrawApiCall(node.Key, ordinal, typed, editable);
            ImGui.PopID();
        }
    }

    /** What the call is and what it runs on, with its slot type gate. */
    private static string CallSummary(MissionApiNode typed) => typed.Member.Kind + " on " + typed.Receiver.Describe()
        + (typed.Member.Gate is { } gate ? $", slot type {gate.SlotType}{(gate.Exact ? " exact" : "")}" : "");

    /** One typed call: its notes and findings, then its parameters. */
    private void DrawApiCall(string key, int ordinal, MissionApiNode typed, bool editable)
    {
        var member = typed.Member;
        if (member.Note is { } note) ImGui.TextWrapped(note);
        if (member.Effect is { } effect) ImGui.TextDisabled("Reports effect " + effect);
        foreach (var finding in typed.Findings)
            ImGui.TextColored(ProblemColor, (finding.Path.Length > 0 ? finding.Path + ": " : "") + finding.Message);
        if (typed.Computed) { ImGui.TextWrapped("Arguments are built in Lua. Edit them in Advanced Lua."); return; }
        if (member.Arguments.Count == 0) ImGui.TextDisabled("No arguments");
        ImGui.BeginDisabled(!editable);
        foreach (var parameter in typed.Parameters) DrawParameter(key, ordinal, typed, parameter);
        ImGui.EndDisabled();
    }

    /** One parameter: absent with an Add button, in Lua mode, or with its typed widget. */
    private void DrawParameter(string key, int ordinal, MissionApiNode typed, MissionApiParameter parameter)
    {
        var spec = parameter.Spec;
        string id = ApiKey(key, ordinal, parameter.Path);
        ImGui.PushID(parameter.Path);
        try
        {
            string label = parameter.Item ? parameter.Path[parameter.Path.LastIndexOf('[')..] : parameter.Label + (spec.Optional ? "" : " *");
            if (!parameter.Present)
            {
                ImGui.TextDisabled((spec.Default ?? spec.DefaultText) is { } initial ? label + " = " + initial : label);
                Hint(spec);
                ImGui.SameLine();
                if (ImGuiIcons.Button("add", StudioIcon.Plus, "Add " + label, size: ImGui.GetTextLineHeight()))
                    AddApiArgument(key, ordinal, parameter.Path);
                return;
            }
            bool lua = !parameter.Literal || parameter.Widget == MissionApiWidget.Lua || apiLua.Contains(id);
            ImGui.TextUnformatted(label);
            Hint(spec);
            if (parameter.Literal && parameter.Widget != MissionApiWidget.Lua)
            {
                ImGui.SameLine();
                // The button stays lit while the parameter is in Lua mode.
                if (lua) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.Header]);
                bool toggle = UiAuto.SmallButton("Lua");
                if (lua) ImGui.PopStyleColor();
                Tip("Edit as Lua");
                if (toggle) SetApiLuaMode(key, ordinal, parameter.Path, !lua);
            }
            if (spec.Optional || parameter.Item)
            {
                ImGui.SameLine();
                if (ImGuiIcons.Button("remove", StudioIcon.Remove, "Remove " + label, size: ImGui.GetTextLineHeight()))
                { RemoveApiArgument(key, ordinal, parameter.Path); return; }
            }
            if (lua)
            {
                if (LuaEditor(id, parameter.Text, key, "api:" + ordinal + ":" + parameter.Path, preview: false) is { } draft)
                    ApiEdit(key, ordinal, node => MissionApiWriter.SetValue(node, parameter.Path, draft), id);
                return;
            }
            DrawWidget(key, ordinal, typed, parameter, id);
        }
        finally { ImGui.PopID(); }
    }

    /** The typed widget of a present parameter; tables, lists and atoms draw their children below it. */
    private void DrawWidget(string key, int ordinal, MissionApiNode typed, MissionApiParameter parameter, string id)
    {
        var spec = parameter.Spec;
        switch (parameter.Widget)
        {
            case MissionApiWidget.Boolean:
            {
                bool value = parameter.Text == "true";
                if (UiAuto.Checkbox("##value", ref value)) ApplyApiValue(key, ordinal, parameter.Path, value ? "true" : "false");
                break;
            }
            case MissionApiWidget.Integer or MissionApiWidget.Number:
            {
                if (spec.Minimum is not null || spec.Maximum is not null)
                    ImGui.TextDisabled("Range " + Bound(spec.Minimum) + " to " + Bound(spec.Maximum));
                if (TextField(id, parameter.Text, out string text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                {
                    number = Math.Clamp(number, spec.Minimum ?? double.MinValue, spec.Maximum ?? double.MaxValue);
                    if (spec.Kind == "integer") number = Math.Round(number);
                    ApplyApiValue(key, ordinal, parameter.Path, MissionApiSchema.NumberText(number));
                }
                break;
            }
            case MissionApiWidget.Text:
            {
                string current = (parameter.Value as LuaLiteral)?.StringValue ?? "";
                if (TextField(id, current, out string text)) ApplyApiValue(key, ordinal, parameter.Path, LuaScript.Quote(text));
                break;
            }
            case MissionApiWidget.Scalar:
                if (TextField(id, parameter.Text, out string scalar)) ApplyApiValue(key, ordinal, parameter.Path, scalar);
                break;
            case MissionApiWidget.Choice:
            {
                ImGui.SetNextItemWidth(-1);
                if (!UiAuto.BeginCombo("##choice", parameter.Choice ?? "Choose")) break;
                foreach (string entry in parameter.Entries)
                    if (UiAuto.Selectable(entry, entry == parameter.Choice) && entry != parameter.Choice) PickApiChoice(key, ordinal, parameter.Path, entry);
                UiAuto.EndCombo();
                break;
            }
            case MissionApiWidget.Reference or MissionApiWidget.SlotHandle:
                if (parameter.Choices.Count == 0) { ImGui.TextDisabled("The installed SDK lists no entry here."); break; }
                if (Pick("", parameter.Reference, parameter.Choices, out var chosen) && chosen is not null && chosen != parameter.Reference)
                    PickApiReference(key, ordinal, parameter.Path, chosen);
                break;
            case MissionApiWidget.Atom:
            {
                ImGui.SetNextItemWidth(-1);
                if (UiAuto.BeginCombo("##kind", parameter.Choice ?? "Choose kind"))
                {
                    foreach (string kind in parameter.Entries)
                        if (UiAuto.Selectable(kind, kind == parameter.Choice) && kind != parameter.Choice)
                            ApiEdit(key, ordinal, node => MissionApiWriter.SetAtomKind(node, parameter.Path, kind));
                    UiAuto.EndCombo();
                }
                Children(parameter);
                break;
            }
            case MissionApiWidget.List or MissionApiWidget.Table:
            {
                Children(parameter);
                if (spec.Element is not null)
                {
                    int count = parameter.Children.Count(child => child.Item);
                    if (spec.MinCount is not null || spec.MaxCount is not null)
                        ImGui.TextDisabled($"{count} entries, {spec.MinCount ?? 0} to {(spec.MaxCount is { } most ? most.ToString(CultureInfo.InvariantCulture) : "any")}");
                    ImGui.BeginDisabled(count >= spec.MaxCount);
                    if (ImGuiIcons.Button("add-entry", StudioIcon.Plus, "Add entry", size: ImGui.GetTextLineHeight()))
                        AddApiItem(key, ordinal, parameter.Path);
                    ImGui.EndDisabled();
                }
                break;
            }
        }

        void Children(MissionApiParameter owner)
        {
            ImGui.Indent(12);
            foreach (var child in owner.Children) DrawParameter(key, ordinal, typed, child);
            ImGui.Unindent(12);
        }
    }

    /** A one-line field that applies on Enter or when it loses focus. */
    private bool TextField(string id, string current, out string text)
    {
        if (!apiInput.TryGetValue(id + "\n" + current, out text!)) text = current;
        ImGui.SetNextItemWidth(-1);
        bool enter = UiAuto.InputText("##field", ref text, 4096, ImGuiInputTextFlags.EnterReturnsTrue);
        bool left = ImGui.IsItemDeactivatedAfterEdit();
        if (text == current) apiInput.Remove(id + "\n" + current); else apiInput[id + "\n" + current] = text;
        if (!(enter || left) || text == current) return false;
        apiInput.Remove(id + "\n" + current);
        return true;
    }

    private static string Bound(double? value) => value is { } bound ? MissionApiSchema.NumberText(bound) : "any";

    private static string TypeText(MissionApiValue value) => value.Ref is { } target ? value.Kind + " " + target : value.Kind;

    private static void Hint(MissionApiValue spec)
    {
        if (!ImGui.IsItemHovered()) return;
        string text = TypeText(spec) + (spec.SlotGate is { } gate ? $", slot type {gate.SlotType}" : "")
            + (spec.Scope is { } scope ? ", scope " + scope : "") + (spec.Note is { } note ? "\n" + note : "");
        ImGui.SetTooltip(text);
    }

    private bool ApplyApiValue(string key, int ordinal, string path, string expression) =>
        ApiEdit(key, ordinal, node => MissionApiWriter.SetValue(node, path, expression));

    private bool PickApiReference(string key, int ordinal, string path, MissionSdkReference reference) =>
        ApiEdit(key, ordinal, node => node.Find(path) is { Widget: MissionApiWidget.SlotHandle } parameter
            ? MissionApiWriter.SetValue(node, path, MissionApiWriter.HandleText(node, parameter, reference))
            : MissionApiWriter.SetValue(node, path, MissionApiWriter.ReferenceText(node, reference)));

    private bool PickApiChoice(string key, int ordinal, string path, string entry) =>
        ApiEdit(key, ordinal, node => MissionApiWriter.SetValue(node, path, MissionApiWriter.ChoiceText(node, node.Find(path)!, entry)));

    private bool AddApiArgument(string key, int ordinal, string path) => ApiEdit(key, ordinal, node => MissionApiWriter.Add(node, path));
    private bool RemoveApiArgument(string key, int ordinal, string path) => ApiEdit(key, ordinal, node => MissionApiWriter.Remove(node, path));
    private bool AddApiItem(string key, int ordinal, string path) => ApiEdit(key, ordinal, node => MissionApiWriter.AddItem(node, path));

    /** Lua mode is a view of the same span; switching never changes the source. */
    private void SetApiLuaMode(string key, int ordinal, string path, bool lua)
    {
        string id = ApiKey(key, ordinal, path);
        if (lua) apiLua.Add(id); else apiLua.Remove(id);
    }

    /** Edits one typed call of a node in a fresh parse, so spans always come from the document being changed. */
    private bool ApiEdit(string key, int ordinal, Func<MissionApiNode, string> edit, string? draft = null)
    {
        MissionGraphDocument Apply(MissionGraphDocument document)
        {
            var view = MissionScriptGraph.Parse(document.Source, document.Groups, apiContext);
            var node = view.FindNode(key) ?? throw new InvalidOperationException("The node changed; select it again.");
            var call = node.ApiCalls.ElementAtOrDefault(ordinal) ?? throw new InvalidOperationException("The call changed; select it again.");
            return MissionGraphDocument.Parse(edit(MissionApiNode.Build(view.Api!, call)));
        }
        return draft is null ? Change(Apply) : ChangeLua(Apply, draft);
    }

    /** Callbacks from the schema; one the script already returns is shown but not offered again. */
    private void DrawCallbackPalette()
    {
        if (apiSchema is null || scriptGraph is null) return;
        ImGui.SeparatorText("Callbacks");
        ImGui.SetNextItemWidth(-1); UiAuto.InputTextWithHint("##callback-filter", "Find callback", ref apiFilter, 128);
        foreach (string name in apiSchema.CallbackNames().Where(value => apiFilter.Length == 0 || value.Contains(apiFilter, StringComparison.OrdinalIgnoreCase)))
        {
            bool present = scriptGraph.Nodes.Any(node => node.Key == MissionScriptGraph.Root + "/field/" + name);
            ImGui.BeginDisabled(present);
            if (UiAuto.Selectable(name + "##callback")) AddApiCallback(name);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(apiSchema.EventForCallback(name)?.Note ?? apiSchema.Program.Entries.FirstOrDefault(entry => entry.Name == name)?.Note ?? name);
        }
    }

    private bool AddApiCallback(string name) => apiSchema is not null && Change(document =>
        MissionGraphDocument.Parse(MissionScriptGraph.Parse(document.Source, document.Groups).AddField(MissionApiWriter.CallbackText(apiSchema, name))));

    /** Every member a receiver in this scope reaches, written with valid placeholders for its required arguments. */
    private void DrawMemberPalette(string scope)
    {
        if (scriptGraph?.Api is not { } analysis) return;
        var names = scriptGraph.ScopeNames(scope);
        ImGui.SeparatorText("Sunrise API");
        ImGui.SetNextItemWidth(-1); UiAuto.InputTextWithHint("##member-filter", "Find call", ref apiFilter, 128);
        foreach (var (member, receiver) in PaletteMembers(analysis, names))
        {
            string label = receiver + member.Separator + member.Name;
            if (apiFilter.Length > 0 && !label.Contains(apiFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (UiAuto.Selectable(label + "##" + member.Receiver + "." + member.Name + "." + member.Gate?.SlotType)) AddApiMember(scope, member);
            if (ImGui.IsItemHovered() && member.Note is { } note) ImGui.SetTooltip(note);
        }
    }

    private static IEnumerable<(MissionApiMember Member, string Receiver)> PaletteMembers(MissionApiAnalysis analysis,
        IReadOnlyDictionary<string, MissionApiType> names)
    {
        foreach (var member in analysis.Api.Schema.Members.Where(value => value.Callable))
            if (MissionApiWriter.ReceiverText(analysis, member, names) is { } receiver) yield return (member, receiver.Text);
        foreach (var (name, type) in names.Where(pair => pair.Value.Receiver == MissionApiType.EventReceiver))
            foreach (var member in type.Events.SelectMany(value => value.Fields).Where(value => value.Callable).DistinctBy(value => value.Name))
                yield return (member, name);
    }

    private bool AddApiMember(string scope, MissionApiMember member) => Change(document =>
    {
        var view = MissionScriptGraph.Parse(document.Source, document.Groups, apiContext);
        var analysis = view.Api ?? throw new InvalidOperationException("Typed nodes need the generated schema.");
        var names = view.ScopeNames(scope);
        string code;
        if (names.FirstOrDefault(pair => pair.Value.Events.Any(value => value.Callback == member.Receiver)).Key is { } eventName)
            code = MissionApiWriter.CallText(analysis, member, eventName, names);
        else
        {
            var receiver = MissionApiWriter.ReceiverText(analysis, member, names)
                ?? throw new InvalidOperationException($"No {member.Receiver} is in scope here.");
            code = MissionApiWriter.CallText(analysis, member, receiver.Text, names, receiver.Type);
        }
        return MissionGraphDocument.Parse(view.AddToScope(scope, code));
    });
}
