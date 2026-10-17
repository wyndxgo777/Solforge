using System.Globalization;
using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** The three phases of a perk's behaviour graph. */
public enum PerkBehaviorPhase { Activation, Effects, Deactivation }
/** Load state of the perk census used to name perks. */
public enum PerkCensusState { Loading, Ready, Failed }

/** Phase links show native array ownership. Only decoded node inputs can change. */
public sealed class PerkBehaviorEditor
{
    private readonly NodeCanvas canvas = new() { ShowEdgeLabels = true, MinimumFitZoom = .95f };
    private readonly Stack<string> undo = [], redo = [];
    private readonly Dictionary<string, string> propertyDrafts = [];
    private PerkBehaviorGraph graph = NativePerkBehavior.New();
    private string selected = "", primitiveSearch = "", pickerSearch = "", status = "", saved = "";
    private PerkBehaviorPhase phase;
    // Picker combos list at most this many matches; the search narrows the rest.
    private const int PickerRows = 300;

    public PerkBehaviorEditor() => saved = Snapshot();

    /** Node kinds the installed perks use; the census fills it once per installation. */
    public static IReadOnlyList<PerkInstalledKind> InstalledKinds
    {
        get => installedKinds;
        set
        {
            installedKinds = value;
            // The compiler reads the label table to turn label filters into masks.
            if (value is PerkCensus { LabelTable: { } labels }) PerkLabelTable.Installed = labels;
        }
    }
    private static IReadOnlyList<PerkInstalledKind> installedKinds = [];

    public static PerkCensusState CensusState { get; private set; }
    private static string censusError = "";
    private static Task<Action>? censusWork;
    private static CancellationTokenSource? censusCancel;

    /** Starts the census of a newly opened install and cancels the previous one. UI thread only. */
    public static void StartCensus(PackageCatalog catalog)
    {
        InstalledKinds = [];
        PerkLabelTable.Installed = null;
        CensusState = PerkCensusState.Loading;
        censusCancel?.Cancel();
        censusCancel = new();
        var token = censusCancel.Token;
        censusWork = Task.Run<Action>(() =>
        {
            var census = PerkBehaviorCensus.Read(catalog, token);
            return () => InstalledKinds = census;
        });
    }

    /** Adopts a finished census between frames. UI thread only. */
    public static void PollCensus()
    {
        if (censusWork is not { IsCompleted: true } done) return;
        censusWork = null;
        try { done.GetAwaiter().GetResult()(); CensusState = PerkCensusState.Ready; }
        catch (Exception error) { censusError = error.Message; CensusState = PerkCensusState.Failed; }
    }

    private static readonly string[] LabelArrayNames = ["Any of", "All of", "None of", "Not all of"];
    /** Installed picker values; empty until the census finishes. */
    private static PerkCensus? Census => InstalledKinds as PerkCensus;
    public PerkBehaviorGraph Document => graph;
    public bool IsDirty => Snapshot() != saved;
    public bool CanUndo => undo.Count != 0;
    public bool CanRedo => redo.Count != 0;

    /** Validation reads the installed tables too, so their references key the cache. */
    public IReadOnlyList<string> Problems
    {
        get
        {
            PollCensus();
            var key = (revision, graph.ProgramTag, (object)InstalledKinds, (object?)PerkLabelTable.Installed);
            if (problems is not { } cached || cached.Key != key)
                problems = cached = (key, NativePerkBehavior.Validate(graph, source));
            return cached.Value;
        }
    }

    // Bumped by every graph change; the snapshot, problems and canvas caches key on it.
    private int revision;
    private PerkBehaviorGraph? source;
    private (int Revision, uint Tag, string Json)? snapshot;
    private ((int, uint, object, object?) Key, IReadOnlyList<string> Value)? problems;
    private (int Revision, IReadOnlyList<CanvasNode> Nodes, IReadOnlyList<CanvasEdge> Edges)? canvasModel;

    /** Graph snapshots stay in memory; compiled package changes use project deltas. */
    public void Load(PerkBehaviorGraph document)
    {
        graph = Copy(document); revision++; saved = Snapshot();
        // Undo and redo keep the same source bytes, so one decompile serves every check.
        source = graph.OriginalBytes.Length == 0 ? null : NativePerkBehavior.Decompile(graph.OriginalBytes);
        undo.Clear(); redo.Clear(); propertyDrafts.Clear(); canvas.Reset(); selected = status = "";
    }

    /** Marks the current graph as the saved baseline. */
    public void MarkSaved() => saved = Snapshot();
    /** Encodes the graph back to native perk behaviour bytes. */
    public byte[] Compile() => NativePerkBehavior.Compile(graph);

    /** A phase admits only its native condition or effect node kind. */
    public string AddNode(PerkBehaviorPhase target, string primitive)
    {
        var spec = NativePerkBehavior.PrimitiveOf(primitive)
            ?? throw new InvalidDataException("The native operation is not available.");
        if (!spec.CanCreate || spec.Kind != Kind(target))
            throw new InvalidDataException("This operation cannot be created in the selected phase.");
        var node = NativePerkBehavior.CreateNode(primitive);
        Change(() => Nodes(target).Add(node));
        selected = node.Id; phase = target;
        return node.Id;
    }

    /** Only new nodes take children; installed rows, groups and lists keep their native layout. */
    public string AddChild(string parentId, string primitive)
    {
        var (_, parent, _) = Find(parentId);
        if (!parent.Editable || NativePerkBehavior.ChildPrimitives(parent).Count == 0)
            throw new InvalidDataException("Only a new counter, group list or condition list takes children.");
        var child = NativePerkBehavior.CreateChild(parent, primitive);
        Change(() => parent.Children.Add(child));
        selected = child.Id;
        return child.Id;
    }

    /** Adds a label or label group to one of a filter's four arrays; unknown hashes are refused. */
    public void AddLabel(string nodeId, string filterKey, int array, uint hash)
    {
        var filter = EditableFilter(nodeId, filterKey, array);
        if (PerkLabelTable.Installed is not { } table || !table.Contains(hash))
            throw new InvalidDataException($"0x{hash:X8} is not in the installed label table.");
        var list = filter.Arrays()[array];
        if (list.Contains(hash)) throw new InvalidDataException("The label is already in this list.");
        Change(() => list.Add(hash));
    }

    /** Removes one label from a node's filter array. */
    public void RemoveLabel(string nodeId, string filterKey, int array, int index)
    {
        var list = EditableFilter(nodeId, filterKey, array).Arrays()[array];
        if (index < 0 || index >= list.Count) throw new InvalidDataException("Choose a label in the list.");
        Change(() => list.RemoveAt(index));
    }

    private PerkLabelFilter EditableFilter(string nodeId, string filterKey, int array)
    {
        var (_, node, _) = Find(nodeId);
        var filter = node.LabelFilters.FirstOrDefault(row => row.Key == filterKey)
            ?? throw new InvalidDataException("The node has no such label filter.");
        if (array is < 0 or > 3) throw new InvalidDataException("A label filter has four lists.");
        if (!node.Editable || !filter.Editable) throw new InvalidDataException("This label filter is read-only.");
        return filter;
    }

    /** Removes a node and detaches it from its parent list. */
    public void RemoveNode(string id)
    {
        var (list, node, parent) = Find(id);
        RequireStructural(node, parent);
        Change(() => list.Remove(node));
        if (selected == id) selected = "";
    }

    /** Native array order is explicit; dragging a box changes only its layout. */
    public void MoveNode(string id, int direction)
    {
        var (list, node, parent) = Find(id);
        RequireStructural(node, parent);
        int from = list.IndexOf(node), to = from + Math.Sign(direction);
        if (to < 0 || to >= list.Count || to == from) return;
        Change(() => { list.RemoveAt(from); list.Insert(to, node); });
    }

    /** Numeric fields retain the native range and integer width declared by the compiler. */
    public void SetProperty(string id, string key, double value)
    {
        var (_, node, _) = Find(id);
        var property = node.Properties.FirstOrDefault(row => row.Key == key)
            ?? throw new InvalidDataException("The node has no such parameter.");
        if (!node.Editable || !property.Editable)
            throw new InvalidDataException("This native parameter is read-only.");
        if (!double.IsFinite(value) || value < property.Minimum || value > property.Maximum
            || property.Kind != PerkBehaviorValueKind.Float && Math.Truncate(value) != value
            || property.Kind == PerkBehaviorValueKind.Boolean && value is not (0 or 1))
            throw new InvalidDataException($"{property.Label} must be between {property.Minimum:G} and {property.Maximum:G}.");
        if (Picker(property) is { Count: > 0 } choices && choices.All(choice => choice.Value != value))
            throw new InvalidDataException($"Choose {property.Label} from the installed values.");
        if (property.Picker.Length == 0 && property.Choices.Count != 0 && property.Choices.All(choice => choice.Value != value))
            throw new InvalidDataException($"Choose {property.Label} from its list.");
        if (property.Value != value) Change(() => property.Value = value);
    }

    /** Installed values plus the property's own choices, or null when it takes any value in range. */
    private static IReadOnlyList<PerkBehaviorChoice>? Picker(PerkBehaviorProperty property)
    {
        if (property.Picker.Length == 0) return null;
        var installed = Census?.Picker(property.Picker) ?? [];
        return installed.Count == 0 ? [] : [.. property.Choices, .. installed];
    }

    /** Installed rows and lists are fixed, and an inner condition belongs to its state test. */
    private static void RequireStructural(PerkBehaviorNode node, PerkBehaviorNode? parent)
    {
        if (!node.Editable) throw new InvalidDataException("This native node is read-only.");
        if (parent is { OriginalOffset: >= 0 }) throw new InvalidDataException("Installed rows keep their native layout.");
        if (parent is not null && NativePerkBehavior.ChildPrimitives(parent).Count == 0)
            throw new InvalidDataException("The inner condition belongs to its state test.");
    }

    /** Restores the previous graph snapshot. */
    public void Undo()
    {
        if (!undo.TryPop(out string? previous)) return;
        redo.Push(Snapshot()); Restore(previous);
    }

    /** Reapplies the last undone snapshot. */
    public void Redo()
    {
        if (!redo.TryPop(out string? next)) return;
        undo.Push(Snapshot()); Restore(next);
    }

    /** The same graph editor serves a standalone package record and an exotic weapon draft. */
    public void Draw(string id, bool editable)
    {
        PollCensus();
        ImGui.PushID(id);
        ImGui.BeginDisabled(!editable);
        ImGui.SetNextItemWidth(160);
        if (UiAuto.BeginCombo("##phase", PhaseName(phase)))
        {
            foreach (var value in Enum.GetValues<PerkBehaviorPhase>())
                if (UiAuto.Selectable(PhaseName(value), value == phase)) phase = value;
            UiAuto.EndCombo();
        }
        ImGui.SameLine();
        if (StudioUi.IconButton("add-node", StudioIcon.Plus, "Add node")) ImGui.OpenPopup("Add native operation");
        DrawAddNode();
        ImGui.SameLine(); if (StudioUi.IconButton("undo", StudioIcon.Undo, "Undo", CanUndo)) Undo();
        ImGui.SameLine(); if (StudioUi.IconButton("redo", StudioIcon.Redo, "Redo", CanRedo)) Redo();
        ImGui.EndDisabled();
        var (nodes, edges) = CanvasModel();
        ImGui.SameLine(); if (StudioUi.IconButton("frame", StudioIcon.Frame, "Frame all")) canvas.Frame(id);
        ImGui.SameLine(); if (StudioUi.IconButton("arrange", StudioIcon.Tree, "Arrange")) canvas.Arrange(id, nodes, edges);
        DrawCensusState();
        StudioUi.Help("Links show each phase's nodes. Up and Down change their order.");
        if (status.Length != 0) ImGui.TextWrapped(status);
        var size = ImGui.GetContentRegionAvail();
        float inspectorWidth = Math.Clamp(size.X * .34f, 250, 390);
        ImGui.BeginChild("perk-canvas", new(Math.Max(100, size.X - inspectorWidth - 8), size.Y),
            ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        canvas.Draw(id, nodes, edges, editable, ref selected, new()
        {
            Remove = editable ? key => Try(() => RemoveNode(key)) : null
        });
        ImGui.EndChild(); ImGui.SameLine();
        ImGui.BeginChild("perk-inspector", new(0, size.Y), ImGuiChildFlags.Borders);
        DrawInspector(editable);
        ImGui.EndChild();
        ImGui.PopID();
    }

    /** One status glyph for the installed perk census: loading, ready or failed. */
    private static void DrawCensusState()
    {
        var (icon, tooltip) = CensusState switch
        {
            PerkCensusState.Loading => (StudioIcon.Reset, "Reading installed perks. Pickers and labels fill in when done."),
            PerkCensusState.Failed => (StudioIcon.Warning, "Could not read installed perks: " + censusError),
            _ => (StudioIcon.Check, "Installed perk values are ready.")
        };
        ImGui.SameLine();
        float size = ImGui.GetFrameHeight();
        var at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new(size));
        uint color = ImGui.GetColorU32(CensusState == PerkCensusState.Failed ? ImGuiCol.Text : ImGuiCol.TextDisabled);
        ImGuiIcons.Draw(ImGui.GetWindowDrawList(), icon, at + new Vector2(size * .22f), size * .56f, color);
        StudioUi.Tip(tooltip);
    }

    /** A node kind row; the tooltip of a kind no in-game perk has used says it is not tested. */
    private static bool KindRow(string key, string name, string description)
    {
        bool clicked = UiAuto.Selectable(name + "##" + key);
        StudioUi.Tip(NativePerkBehavior.IsTested(key) ? description : description + " May not work in game.");
        return clicked;
    }

    private static bool KindRow(PerkBehaviorPrimitive primitive) =>
        KindRow(primitive.Key, primitive.Name, primitive.Description);

    /** Only constructible primitives of the selected phase are offered. */
    private void DrawAddNode()
    {
        if (!ImGui.BeginPopup("Add native operation")) return;
        ImGui.SetNextItemWidth(300);
        UiAuto.InputTextWithHint("##operation-search", "Find a native operation", ref primitiveSearch, 256);
        foreach (var primitive in NativePerkBehavior.Primitives.Where(value => value.Kind == Kind(phase) && value.CanCreate
            && (primitiveSearch.Length == 0 || value.Name.Contains(primitiveSearch, StringComparison.OrdinalIgnoreCase)
                || value.Description.Contains(primitiveSearch, StringComparison.OrdinalIgnoreCase))))
            if (KindRow(primitive)) { Try(() => AddNode(phase, primitive.Key)); ImGui.CloseCurrentPopup(); }
        if (Kind(phase) == PerkBehaviorNodeKind.Condition && UiAuto.TreeNode("While a state holds"))
        {
            foreach (var primitive in NativePerkBehavior.StateGatedPrimitives.Where(value => primitiveSearch.Length == 0
                || value.Name.Contains(primitiveSearch, StringComparison.OrdinalIgnoreCase)))
                if (KindRow(primitive)) { Try(() => AddNode(phase, primitive.Key)); ImGui.CloseCurrentPopup(); }
            ImGui.TreePop();
        }
        // Installed kinds without a writer are listed so nothing is hidden; they cannot be added yet.
        var writable = NativePerkBehavior.Primitives.Where(value => value.CanCreate).Select(value => value.NativeClass).ToHashSet();
        var unwritten = InstalledKinds.Where(kind => kind.Kind == Kind(phase) && !writable.Contains(kind.NativeClass)).ToArray();
        if (unwritten.Length != 0 && primitiveSearch.Length == 0)
        {
            ImGui.SeparatorText($"Read only ({unwritten.Length})");
            ImGui.BeginDisabled();
            foreach (var kind in unwritten)
            {
                UiAuto.Selectable($"{kind.Kind} opcode {kind.Opcode}  ({kind.NativeClass:X8})##{kind.NativeClass}");
                StudioUi.Tip($"Used by {kind.Programs} perks. Kept, not writable.");
            }
            ImGui.EndDisabled();
        }
        ImGui.EndPopup();
    }

    /** Only decoded node inputs can change. */
    private void DrawInspector(bool editable)
    {
        var node = AllNodes().FirstOrDefault(value => value.Id == selected);
        if (node is null)
        {
            ImGui.TextDisabled("Select a node to edit it.");
            ImGui.BeginDisabled(!editable);
            DrawHud();
            ImGui.EndDisabled();
            DrawProblems();
            return;
        }
        ImGui.TextUnformatted(node.Title);
        StudioUi.Help(node.Editable && !NativePerkBehavior.IsTested(node.Primitive)
            ? node.Description + " May not work in game." : node.Description);
        if (!node.Editable)
        {
            ImGui.TextDisabled("Read only");
            StudioUi.Tip("Kept as installed.");
        }
        var (_, _, parent) = Find(node.Id);
        bool structural = editable && node.Editable && (parent is null
            || parent.OriginalOffset < 0 && NativePerkBehavior.ChildPrimitives(parent).Count != 0);
        if (StudioUi.IconButton("up", StudioIcon.Up, "Earlier", structural)) Try(() => MoveNode(node.Id, -1));
        ImGui.SameLine(); if (StudioUi.IconButton("down", StudioIcon.Down, "Later", structural)) Try(() => MoveNode(node.Id, 1));
        ImGui.SameLine();
        if (StudioUi.IconButton("remove", StudioIcon.Remove, "Remove", structural)) { Try(() => RemoveNode(node.Id)); return; }
        if (node.Primitive is "counter" or "all-groups" or "group" or "extend-timers") DrawChildren(node, editable);
        ImGui.Separator();
        foreach (var property in node.Properties)
        {
            string key = node.Id + ":" + property.Key;
            ImGui.PushID(key);
            ImGui.TextUnformatted(property.Label);
            StudioUi.Tip(property.Description);
            ImGui.BeginDisabled(!editable || !node.Editable || !property.Editable);
            if (Picker(property) is { } picker) DrawPicker(node, property, picker);
            else if (property.Choices.Count != 0)
            {
                string label = property.Choices.FirstOrDefault(choice => choice.Value == property.Value)?.Label ?? Format(property);
                ImGui.SetNextItemWidth(-1);
                if (UiAuto.BeginCombo("##choice", label))
                {
                    foreach (var choice in property.Choices)
                        if (UiAuto.Selectable(choice.Label, choice.Value == property.Value))
                            Try(() => SetProperty(node.Id, property.Key, choice.Value));
                    UiAuto.EndCombo();
                }
            }
            else if (property.Kind == PerkBehaviorValueKind.Boolean)
            {
                bool value = property.Value != 0;
                if (UiAuto.Checkbox("Enabled", ref value)) Try(() => SetProperty(node.Id, property.Key, value ? 1 : 0));
            }
            else
            {
                string value = propertyDrafts.GetValueOrDefault(key, Format(property));
                ImGui.SetNextItemWidth(-1);
                bool enter = UiAuto.InputText("##value", ref value, 96, ImGuiInputTextFlags.EnterReturnsTrue);
                propertyDrafts[key] = value;
                if (enter || ImGui.IsItemDeactivatedAfterEdit()) Try(() =>
                {
                    SetProperty(node.Id, property.Key, Parse(property, value));
                    propertyDrafts.Remove(key);
                });
            }
            ImGui.EndDisabled(); ImGui.PopID();
        }
        foreach (var filter in node.LabelFilters) DrawLabelFilter(node, filter, editable);
        if (UiAuto.CollapsingHeader("Native details"))
        {
            ImGui.TextUnformatted($"Class: 0x{node.NativeClass:X8}");
            ImGui.TextUnformatted("Operation: " + node.Primitive);
        }
        DrawProblems();
    }

    /** Four label lists; an empty filter passes everything. */
    private void DrawLabelFilter(PerkBehaviorNode node, PerkLabelFilter filter, bool editable)
    {
        var table = PerkLabelTable.Installed;
        ImGui.PushID(node.Id + ":labels:" + filter.Key);
        ImGui.SeparatorText(filter.Label);
        if (!filter.Editable)
        {
            ImGui.TextDisabled("Read only");
            StudioUi.Tip("Kept as installed.");
        }
        var arrays = filter.Arrays();
        for (int a = 0; a < 4; a++)
        {
            ImGui.PushID(a);
            ImGui.TextUnformatted(LabelArrayNames[a]);
            ImGui.BeginDisabled(!editable || !node.Editable || !filter.Editable || table is null);
            for (int i = 0; i < arrays[a].Count; i++)
            {
                ImGui.PushID(i);
                int index = i, array = a;
                if (StudioUi.IconButton("remove-label", StudioIcon.Remove, "Remove label"))
                    Try(() => RemoveLabel(node.Id, filter.Key, array, index));
                ImGui.SameLine();
                ImGui.TextUnformatted(table?.NameOf(arrays[a][i]) ?? $"0x{arrays[a][i]:X8}");
                ImGui.PopID();
            }
            ImGui.SetNextItemWidth(-1);
            if (table is not null && UiAuto.BeginCombo("##add-label", "Add label", ImGuiComboFlags.HeightLarge))
            {
                ImGui.SetNextItemWidth(-1);
                UiAuto.InputTextWithHint("##label-search", "Search", ref pickerSearch, 128);
                int array = a;
                foreach (var entry in table.Entries.Where(entry => pickerSearch.Length == 0
                    || entry.Name.Contains(pickerSearch, StringComparison.OrdinalIgnoreCase)).Take(PickerRows))
                    if (UiAuto.Selectable(entry.Group ? entry.Name : $"{entry.Name} (bit {entry.Bit})"))
                        Try(() => AddLabel(node.Id, filter.Key, array, entry.Hash));
                UiAuto.EndCombo();
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        ImGui.PopID();
    }

    /** A searchable list of installed values; an empty census leaves the value unchanged. */
    private void DrawPicker(PerkBehaviorNode node, PerkBehaviorProperty property, IReadOnlyList<PerkBehaviorChoice> picker)
    {
        string label = picker.FirstOrDefault(choice => choice.Value == property.Value)?.Label ?? Format(property);
        // An empty picker waits for the census; the toolbar shows its state.
        if (picker.Count == 0) { ImGui.TextDisabled(label); return; }
        ImGui.SetNextItemWidth(-1);
        if (!UiAuto.BeginCombo("##picker", label, ImGuiComboFlags.HeightLarge)) return;
        ImGui.SetNextItemWidth(-1);
        UiAuto.InputTextWithHint("##picker-search", "Search", ref pickerSearch, 128);
        foreach (var choice in picker.Where(choice => pickerSearch.Length == 0
            || choice.Label.Contains(pickerSearch, StringComparison.OrdinalIgnoreCase)).Take(PickerRows))
            if (UiAuto.Selectable(choice.Label, choice.Value == property.Value))
                Try(() => SetProperty(node.Id, property.Key, choice.Value));
        UiAuto.EndCombo();
    }

    /** New counters, group lists and condition lists take children; installed ones stay as they are. */
    private void DrawChildren(PerkBehaviorNode parent, bool editable)
    {
        ImGui.SeparatorText($"{ChildName(parent)}s ({parent.Children.Count})");
        if (parent.OriginalOffset >= 0)
        {
            ImGui.TextDisabled("Fixed layout");
            StudioUi.Tip("Installed lists keep their layout. Select an entry to edit its values.");
            return;
        }
        ImGui.BeginDisabled(!editable || !parent.Editable);
        ImGui.SetNextItemWidth(-1);
        if (UiAuto.BeginCombo("##add-child", "Add " + ChildName(parent).ToLowerInvariant(), ImGuiComboFlags.HeightLarge))
        {
            foreach (string key in NativePerkBehavior.ChildPrimitives(parent))
            {
                var spec = NativePerkBehavior.PrimitiveOf(key);
                if (KindRow(key, spec?.Name ?? "Group", spec?.Description ?? "")) Try(() => AddChild(parent.Id, key));
            }
            UiAuto.EndCombo();
        }
        ImGui.EndDisabled();
    }

    private static string ChildName(PerkBehaviorNode parent) => parent.Primitive switch
    {
        "counter" => "Row", "all-groups" => "Group", _ => "Condition"
    };

    /** The HUD entry belongs to the whole perk, not to one phase node. */
    public void SetHud(PerkHudStatus? hud) => Change(() => graph.Hud = hud);

    /** The HUD buff toggle, its label and the deactivation timer option. */
    private void DrawHud()
    {
        StudioUi.Heading("HUD buff", StudioUi.MayNotWork);
        bool shown = graph.Hud is not null;
        if (UiAuto.Checkbox("Show while active", ref shown))
            Try(() => SetHud(shown ? new() { Label = (uint)NativePerkBehavior.HudLabels[0].Value } : null));
        StudioUi.Tip("Adds an entry to the HUD buff tray while the perk is active.");
        if (graph.Hud is not { } hud) return;
        var labels = NativePerkBehavior.HudLabels;
        string label = labels.FirstOrDefault(choice => choice.Value == hud.Label)?.Label ?? $"0x{hud.Label:X8}";
        if (labels.Count == 1) ImGui.TextDisabled("Label: " + label);
        else
        {
            ImGui.SetNextItemWidth(-1);
            if (UiAuto.BeginCombo("##hud-label", label))
            {
                foreach (var choice in labels)
                    if (UiAuto.Selectable(choice.Label, choice.Value == hud.Label))
                        Try(() => SetHud(new() { Label = (uint)choice.Value, ShowTimer = hud.ShowTimer }));
                UiAuto.EndCombo();
            }
        }
        bool timer = hud.ShowTimer;
        if (UiAuto.Checkbox("Show deactivation timer", ref timer))
            Try(() => SetHud(new() { Label = hud.Label, ShowTimer = timer }));
    }

    /** Preservation notes remain visible beside errors that prevent compilation. */
    private void DrawProblems()
    {
        ImGui.SeparatorText("Checks");
        var problems = Problems;
        if (problems.Count == 0) ImGui.TextDisabled("Ready to compile");
        ImGui.PushStyleColor(ImGuiCol.Text, StudioTheme.Accent);
        foreach (string problem in problems) ImGui.TextWrapped(problem);
        ImGui.PopStyleColor();
        var notes = graph.Diagnostics.ToArray();
        if (notes.Length > 0 && UiAuto.TreeNode($"Notes ({notes.Length})###perk-notes"))
        {
            StudioUi.Facts("perk-notes", notes);
            ImGui.TreePop();
        }
    }

    private (IReadOnlyList<CanvasNode>, IReadOnlyList<CanvasEdge>) CanvasModel()
    {
        if (canvasModel is not { } cached || cached.Revision != revision)
        {
            var (nodes, edges) = BuildCanvasModel();
            canvasModel = cached = (revision, nodes, edges);
        }
        return (cached.Nodes, cached.Edges);
    }

    /** Edges show phase membership; array order stays in each node's label. */
    private (IReadOnlyList<CanvasNode>, IReadOnlyList<CanvasEdge>) BuildCanvasModel()
    {
        var nodes = new List<CanvasNode>();
        var edges = new List<CanvasEdge>();
        foreach (var value in Enum.GetValues<PerkBehaviorPhase>())
        {
            string root = "phase:" + value;
            var children = Nodes(value);
            nodes.Add(new(root, PhaseName(value), $"{children.Count} " + (children.Count == 1 ? "node" : "nodes"),
                Tint: new Vector4(.2f, .35f, .45f, 1), Width: 170));
            for (int index = 0; index < children.Count; index++)
                Add(children[index], root, $"{PhaseName(value)} / {index + 1}", $"Position {index + 1}");
        }
        return (nodes, edges);

        void Add(PerkBehaviorNode node, string parent, string place, string edge)
        {
            string preview = string.Join('\n', node.Properties.Take(5).Select(row => row.Label + ": " + Format(row)));
            nodes.Add(new(node.Id, node.Title, node.Editable ? place : "Read-only native operation",
                preview.Length == 0 ? null : preview, node.Editable ? new(.32f, .4f, .28f, 1) : new(.35f, .31f, .31f, 1), Width: 270));
            edges.Add(new(parent, node.Id, false, edge));
            string name = node.Primitive switch
            {
                "counter" => "Row", "all-groups" => "Group", "group" or "extend-timers" => "Condition", _ => ""
            };
            for (int index = 0; index < node.Children.Count; index++)
                Add(node.Children[index], node.Id, name.Length == 0 ? "Inner condition" : $"{name} {index + 1}",
                    name.Length == 0 ? "Inner" : $"{name} {index + 1}");
        }
    }

    /** A node identity selects one exact phase list or one parent's rows. */
    private (List<PerkBehaviorNode> List, PerkBehaviorNode Node, PerkBehaviorNode? Parent) Find(string id)
    {
        foreach (var value in Enum.GetValues<PerkBehaviorPhase>())
            if (Search(Nodes(value), null) is { } found) return found;
        throw new InvalidDataException("Choose a behavior node first.");

        (List<PerkBehaviorNode>, PerkBehaviorNode, PerkBehaviorNode?)? Search(List<PerkBehaviorNode> list, PerkBehaviorNode? parent)
        {
            foreach (var node in list)
            {
                if (node.Id == id) return (list, node, parent);
                if (Search(node.Children, node) is { } nested) return nested;
            }
            return null;
        }
    }

    private List<PerkBehaviorNode> Nodes(PerkBehaviorPhase value) => value switch
    {
        PerkBehaviorPhase.Activation => graph.Activation,
        PerkBehaviorPhase.Effects => graph.Effects,
        _ => graph.Deactivation
    };
    private IEnumerable<PerkBehaviorNode> AllNodes() =>
        graph.Activation.Concat(graph.Effects).Concat(graph.Deactivation).SelectMany(Flatten);
    private static IEnumerable<PerkBehaviorNode> Flatten(PerkBehaviorNode node) => node.Children.SelectMany(Flatten).Prepend(node);
    private static PerkBehaviorNodeKind Kind(PerkBehaviorPhase value) => value == PerkBehaviorPhase.Effects
        ? PerkBehaviorNodeKind.Effect : PerkBehaviorNodeKind.Condition;
    private static string PhaseName(PerkBehaviorPhase value) => value switch
    {
        PerkBehaviorPhase.Activation => "Activation", PerkBehaviorPhase.Effects => "Effects", _ => "Deactivation"
    };
    private static string Format(PerkBehaviorProperty property) => property.Kind == PerkBehaviorValueKind.Label
        ? $"0x{(uint)(property.Value % 4294967296.0):X8} (bit {Math.Floor(property.Value / 4294967296.0)})"
        : property.Kind is PerkBehaviorValueKind.Tag or PerkBehaviorValueKind.Hash
        ? $"0x{checked((uint)property.Value):X8}" : property.Kind == PerkBehaviorValueKind.Float
            ? ((float)property.Value).ToString("G", CultureInfo.InvariantCulture)
            : property.Value.ToString("G9", CultureInfo.InvariantCulture);
    private static double Parse(PerkBehaviorProperty property, string value) =>
        property.Kind is PerkBehaviorValueKind.Tag or PerkBehaviorValueKind.Hash
            ? uint.Parse(value.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    /** Serialized once per revision; ProgramTag is keyed too because callers may set it directly. */
    private string Snapshot()
    {
        if (snapshot is not { } cached || cached.Revision != revision || cached.Tag != graph.ProgramTag)
            snapshot = cached = (revision, graph.ProgramTag, JsonSerializer.Serialize(graph));
        return cached.Json;
    }
    private static PerkBehaviorGraph Copy(PerkBehaviorGraph source) =>
        JsonSerializer.Deserialize<PerkBehaviorGraph>(JsonSerializer.Serialize(source))!;
    private void Restore(string source)
    {
        graph = JsonSerializer.Deserialize<PerkBehaviorGraph>(source)!;
        revision++;
        propertyDrafts.Clear(); status = "";
    }
    /** A failed draft edit restores the graph and leaves undo unchanged. */
    private void Change(Action action)
    {
        string before = Snapshot();
        try { action(); }
        catch { Restore(before); throw; }
        revision++;
        if (Snapshot() != before) { undo.Push(before); redo.Clear(); status = ""; }
    }
    private void Try(Action action)
    {
        try { action(); status = ""; }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or OverflowException or FormatException)
        { status = error.Message; }
    }
}
