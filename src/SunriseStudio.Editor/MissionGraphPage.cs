using System.Numerics;
using ImGuiNET;
using SunriseStudio.Missions;
using SunriseStudio.Projects;
using SunriseStudio.Viewport;
using static SunriseStudio.Editor.StudioUi;

namespace SunriseStudio.Editor;

/** Installed mission scripts save in place and belong to no project; a project's scripts are its mod's scripts. */
public sealed partial class MissionGraphPage
{
    private enum ScriptHome { Installed, Project }
    private MissionScriptCatalog? catalog;
    private MissionScriptEntry? script;
    private ScriptHome home;
    // False for a new script that has not been written anywhere yet.
    private bool stored;
    private MissionGraphDocument? graph;
    private MissionScriptGraph? scriptGraph;
    private string scriptError = "";
    private IReadOnlyList<MissionSdkReference> references = [];
    // Scene checks; unlike the document's own checks, they need the SDK.
    private IReadOnlyList<MissionGraphDiagnostic> sceneChecks = [];
    private readonly NodeCanvas canvas = new();
    private readonly Stack<string> undo = [], redo = [];
    private StudioProject? project;
    private StudioWorkspace? workspaceOwner;
    private string selected = "", groupKey = MissionScriptGraph.Root, advancedField = "", advancedValue = "", referenceFilter = "";
    // The Lua tab: two-way sync with the nodes (shared with the node graph model); a draft that does not parse stays.
    private readonly Solforge.Graphs.GraphSourceSync source = new();
    private static double Now => Environment.TickCount64 / 1000.0;
    private string newId = "step", status = "";
    // True when status holds an error.
    private bool statusFailed;
    // The installation the catalog was last built for, and why that build failed.
    private string? catalogTried, catalogError;
    // The open Mod scripts list; read once each time it opens.
    private IReadOnlyList<MissionScriptEntry> projectScripts = [];
    private Action? pendingSwitch;
    private bool showSwitch;
    private bool dirty;

    public MissionGraphPage() { canvas.CodeRenderer = DrawCanvasLua; canvas.ShowEdgeLabels = true; }

    /** Folder for node layouts; they are kept per script file and never written into a script or project. */
    public string? LayoutDirectory { get; set; }
    public ImFontPtr? CodeFont { get => canvas.CodeFont; set => canvas.CodeFont = value; }
    public bool IsDirty { get => dirty || HasLuaDrafts || HasSourceDraft; private set => dirty = value; }
    /** Installed scripts are always editable; a project's scripts need that project open and idle. */
    public bool CanEdit => graph is not null && (home == ScriptHome.Installed || project is not null && workspaceOwner?.CanEdit == true);
    public bool CanSave => CanEdit;
    public bool CanUndo => CanEdit && !HasLuaDrafts && (undo.Count > 0 || HasSourceDraft);
    public bool CanRedo => CanEdit && !HasLuaDrafts && redo.Count > 0;
    public MissionGraphDocument? Document => graph;
    /** The file this script saves to; null until a new script is first saved. */
    public string? ScriptPath => stored ? script?.FullPath : null;
    /** The open script's file, also before a new script is first saved; null with no script open (the unified Graph panel). */
    public string? OpenScriptPath => graph is null ? null : script?.FullPath;
    public bool IsInstalledScript => home == ScriptHome.Installed;
    /** The Checks tab's document, scene and template findings plus a Lua draft that did not apply (for the AI). */
    public IReadOnlyList<string> CheckLines => graph is null ? []
        : [.. graph.Diagnostics.Concat(sceneChecks).Select(diagnostic => diagnostic.Message),
            .. (source.Error.Length > 0 ? new[] { "Lua tab not applied: " + source.Error } : [])];
    // The Lua tab holds text that is not applied yet.
    private bool HasSourceDraft => graph is not null && source.Draft != graph.Source;

    /** Discard drops only the unsaved editor document; no file changes. */
    public void DiscardDraft()
    {
        legacyAssetId = null;
        graph = null; script = null; scriptGraph = null; stored = false; source.Reset("");
        undo.Clear(); redo.Clear(); selected = "";
        ClearLuaDrafts();
        pendingSwitch = null; showSwitch = false; IsDirty = false;
    }

    /** Saves the open script into the workspace's project. */
    public void SaveCurrent(StudioWorkspace workspace)
    {
        workspaceOwner = workspace;
        if (graph is null) return;
        Try(() => { ApplyDraft(); Save(); });
    }

    private void ApplyDraft()
    {
        if (!HasSourceDraft) return;
        RequireNoLuaDrafts();
        if (!source.TryApply(text =>
            {
                var next = MissionGraphDocument.Parse(text);
                undo.Push(graph!.Source); redo.Clear(); graph = next; IsDirty = true; Refresh();
            }, Now, force: true))
            throw new InvalidDataException("The Lua tab does not parse, so it was not applied (see Checks): " + source.Error);
    }

    /** Opens an installed script, or a script in the open project's scripts folder. */
    public void OpenScript(string path, StudioWorkspace workspace)
    {
        if (IsDirty) RequestSwitch(Open);
        else Open();

        void Open()
        {
            string game = workspace.Catalog?.GameRoot ?? throw new InvalidOperationException("Open an installation before a mission script.");
            var nextCatalog = string.Equals(catalog?.GameRoot, game, StringComparison.OrdinalIgnoreCase) ? catalog! : new(game);
            var previousCatalog = catalog; var previousProject = project; var previousWorkspace = workspaceOwner;
            try
            {
                catalog = nextCatalog; workspaceOwner = workspace; project = workspace.Project;
                OpenScript(path, game);
            }
            catch { catalog = previousCatalog; project = previousProject; workspaceOwner = previousWorkspace; throw; }
        }
    }

    /** Opens a script after the page has adopted its project context. */
    public void OpenScript(string path, string gameRoot)
    {
        catalog ??= new(gameRoot);
        string full = Path.GetFullPath(path);
        var installed = catalog.Scripts.FirstOrDefault(value => Path.GetFullPath(value.FullPath).Equals(full, StringComparison.OrdinalIgnoreCase));
        if (installed is not null) { Adopt(installed, ScriptHome.Installed, true); return; }
        var owned = project is null ? null : MissionProject.List(project)
            .FirstOrDefault(value => Path.GetFullPath(value.FullPath).Equals(full, StringComparison.OrdinalIgnoreCase));
        if (owned is null) throw new InvalidDataException("Choose an installed mission script or one in the open project.");
        Adopt(Describe(owned), ScriptHome.Project, true);
    }

    /** Undo keeps the original Lua text and rebuilds only its graph view. */
    public void Undo()
    {
        if (graph is null) return;
        Try(() =>
        {
            RequireNoLuaDrafts();
            ApplyDraft();
            if (!undo.TryPop(out var previous)) return;
            redo.Push(graph.Source); graph = MissionGraphDocument.Parse(previous); Refresh(); IsDirty = true;
        });
    }

    /** Redoes the last undone graph edit. */
    public void Redo()
    {
        if (graph is null) return;
        Try(() =>
        {
            RequireNoLuaDrafts();
            // Applying the Lua tab would clear the redo history, so only Revert keeps it.
            if (HasSourceDraft) throw new InvalidOperationException("Revert the Lua tab before redo.");
            if (!redo.TryPop(out var next)) return;
            undo.Push(graph.Source); graph = MissionGraphDocument.Parse(next); Refresh(); IsDirty = true;
        });
    }

    /** Draws the toolbar and the Nodes, Lua and Checks tabs; a recoverable error becomes the status line. */
    public void Draw(StudioWorkspace workspace)
    {
        workspaceOwner = workspace;
        if (workspace.Catalog is null) { ImGui.TextDisabled("Open a Sunrise installation"); return; }
        string root = workspace.Catalog.GameRoot;
        try
        {
            if (pendingSwitch is not null && !IsDirty) FinishSwitch();
            // A failed build is not retried until the installation changes or Reload is pressed.
            if (pendingSwitch is null && catalog?.GameRoot != root && catalogTried != root)
                RequestSwitch(() => { LoadCatalog(root); DiscardDraft(); });
            else if (pendingSwitch is null && !ReferenceEquals(project, workspace.Project))
            {
                // Closing or switching the project only touches an open project script.
                if (graph is not null && home == ScriptHome.Project) RequestSwitch(() => { DiscardDraft(); project = workspace.Project; });
                else project = workspace.Project;
            }
            DrawSwitch();
            DrawToolbar(workspace);
            if (catalog is null && catalogError is not null) { ImGui.TextDisabled("Scripts did not load: " + catalogError); return; }
            if (graph is null) { ImGui.TextDisabled("Choose a mission script"); return; }
            if (status.Length > 0)
            {
                if (statusFailed) Problem(status);
                else ImGui.TextDisabled(status);
            }
            var calls = ApiChecks();
            string sdk = SdkProblem;
            int problems = graph.Diagnostics.Count + sceneChecks.Count + calls.Count
                + (scriptError.Length > 0 ? 1 : 0) + (sdk.Length > 0 ? 1 : 0) + (source.Error.Length > 0 ? 1 : 0);
            // Unified Graph: the same Nodes / source / Checks views as the node graph model (GraphViews).
            GraphViews.Tabs("mission-views", () => DrawNodes(CanEdit), "Lua", () => DrawSource(CanEdit), problems,
                () => DrawChecks(calls, sdk, problems));
        }
        catch (Exception error) when (Recoverable(error)) { Fail(error.Message); workspace.Error = error.Message; }
    }

    /** Every problem, one per line; without a schema it also says API calls were not checked. */
    private void DrawChecks(IReadOnlyList<string> calls, string sdk, int problems)
    {
        DrawHost();
        if (problems == 0)
        {
            ImGui.TextDisabled(apiSchema is null ? "No problems found; API calls not checked" : "No problems found");
            ApiStatusTip();
            return;
        }
        if (sdk.Length > 0) Problem(sdk);
        if (source.Error.Length > 0) Problem("Lua tab not applied (the nodes keep the last good script): " + source.Error);
        if (scriptError.Length != 0) Problem("Script view: " + scriptError);
        foreach (var diagnostic in graph!.Diagnostics.Concat(sceneChecks)) Problem(diagnostic.Message);
        foreach (string check in calls) Problem(check);
        if (apiSchema is null) { ImGui.TextDisabled("API calls not checked"); ApiStatusTip(); }
    }

    /** One wrapped line in the problem color. */
    private static void Problem(string text) => GraphViews.Problem(text, ProblemColor);

    private void Fail(string text) { status = text; statusFailed = true; }

    private void Note(string text) { status = text; statusFailed = false; }

    private static bool Recoverable(Exception error) => error is IOException or InvalidDataException or InvalidOperationException
        or NotSupportedException or ArgumentException or UnauthorizedAccessException;

    /** Reads one project script only when it is picked, not each time the list opens. */
    private MissionScriptEntry Describe(MissionScriptEntry entry) =>
        catalog?.Describe(entry.Name, entry.RelativePath, entry.FullPath) ?? entry;

    /** Builds the catalog for one installation; a failure leaves no catalog and keeps the reason. */
    private void LoadCatalog(string root)
    {
        catalogTried = root; catalog = null; catalogError = null;
        try { catalog = new(root); }
        catch (Exception error) when (Recoverable(error)) { catalogError = error.Message; }
    }

    /** Picks up scripts and SDK files the game or a git pull wrote since the page opened. */
    private void Reload(string root)
    {
        LoadCatalog(root);
        if (graph is null || script is null) return;
        if (catalog is null || !stored) { DiscardDraft(); return; }
        Adopt(script, home, true);
    }

    /** Undo and Redo live in the Edit menu and its shortcuts, so the toolbar has no buttons for them. */
    private void DrawToolbar(StudioWorkspace workspace)
    {
        string root = workspace.Catalog!.GameRoot;
        float combo = Math.Clamp(ImGui.GetContentRegionAvail().X * .3f, 100, 220);
        ImGui.BeginDisabled(catalog is null);
        if (ImGuiIcons.Button("mission-new", StudioIcon.Plus, "New script")) RequestSwitch(() => showNewScript = true);
        ImGui.EndDisabled();
        Flow(combo);
        DrawNewScript(workspace);
        ImGui.BeginDisabled(catalog is null);
        ImGui.SetNextItemWidth(combo);
        if (UiAuto.BeginCombo("##mission-script", home == ScriptHome.Installed && script is not null ? script.Name : "Installed scripts"))
        {
            if (catalog is { Scripts.Count: 0 })
                ImGui.TextDisabled(Directory.Exists(catalog.ScriptsRoot) ? "No installed scripts" : "No scripts folder in this installation");
            foreach (var entry in catalog?.Scripts ?? [])
                if (UiAuto.Selectable(entry.Name, home == ScriptHome.Installed && entry.FullPath == script?.FullPath))
                    RequestSwitch(() => Adopt(entry, ScriptHome.Installed, true));
            UiAuto.EndCombo();
        }
        ImGui.EndDisabled();
        Tip(catalog is null ? "Scripts did not load" : ScriptTip(ScriptHome.Installed, "Installed scripts"));
        if (ToolbarButton("mission-reload", true, StudioIcon.Loop, "Reload scripts and API schema"))
        {
            catalogTried = null;
            RequestSwitch(() => Reload(root));
        }
        Flow(combo);
        ImGui.BeginDisabled(project is null);
        ImGui.SetNextItemWidth(combo);
        if (UiAuto.BeginCombo("##project-missions", home == ScriptHome.Project && script is not null ? script.Name : "Mod scripts"))
        {
            if (ImGui.IsWindowAppearing()) { projectScripts = []; Try(() => projectScripts = MissionProject.List(project!)); }
            if (projectScripts.Count == 0) ImGui.TextDisabled("This project has no mod scripts");
            foreach (var entry in projectScripts)
                if (UiAuto.Selectable(entry.Name + "##" + entry.RelativePath, home == ScriptHome.Project && entry.FullPath == script?.FullPath))
                    RequestSwitch(() => Adopt(Describe(entry), ScriptHome.Project, true));
            DrawLegacyScripts(workspace);
            UiAuto.EndCombo();
        }
        ImGui.EndDisabled();
        Tip(project is null ? "Open a project to edit mod scripts" : ScriptTip(ScriptHome.Project, "This project's mod scripts"));
        // An unsaved script shows as Save* in the accent color.
        if (IsDirty) ImGui.PushStyleColor(ImGuiCol.Text, StudioTheme.Accent);
        bool save = ToolbarButton(IsDirty ? "Save*###mission-save" : "Save###mission-save", CanSave);
        if (IsDirty) { ImGui.PopStyleColor(); Tip("Unsaved changes"); }
        if (save) SaveCurrent(workspace);
        if (ToolbarButton("mission-export", graph is not null, StudioIcon.Export, "Export Lua for review")) ExportLua();
        if (graph is not null && !CanEdit)
        {
            Flow(ImGui.CalcTextSize("Read only").X);
            ImGui.TextDisabled("Read only");
            Tip("Open its project to edit");
        }
        if (graph is not null && apiSchema is null) WarningMark("No API schema. " + apiStatus);
        if (pendingSwitch is not null && ToolbarButton("mission-switch", true, StudioIcon.Forward, "Finish switching scripts"))
            showSwitch = true;
    }

    /** The open script's place and path when it belongs to this list; otherwise the list's own tooltip. */
    private string ScriptTip(ScriptHome where, string list)
    {
        if (graph is null || script is null || home != where) return list;
        string place = !stored ? "Not saved yet" : where == ScriptHome.Installed ? "Installed; saves in place" : "Mod script in " + project?.Name;
        return place + "\n" + script.FullPath;
    }

    private void ExportLua()
    {
        using var dialog = new FolderBrowserDialog { Description = "Export mission script for review", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        Try(() =>
        {
            RequireNoLuaDrafts();
            ApplyDraft();
            string game = catalog?.GameRoot ?? throw new InvalidOperationException("Installed scripts did not load.");
            string path = MissionProject.Export(script!.RelativePath, graph!.Source, dialog.SelectedPath, game);
            Note("Exported " + Path.GetFileName(path));
        });
    }

    /** Stays on the toolbar row while the next item fits; otherwise starts a new row. */
    private static void Flow(float width)
    {
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X < width) ImGui.NewLine();
    }

    /** A toolbar button that wraps instead of clipping; with an icon, label is only its id. */
    private static bool ToolbarButton(string label, bool enabled, StudioIcon? icon = null, string tooltip = "")
    {
        Flow(icon is null ? ImGui.CalcTextSize(label, true).X + ImGui.GetStyle().FramePadding.X * 2 : ImGui.GetFrameHeight());
        if (icon is { } image) return IconButton(label, image, tooltip, enabled);
        ImGui.BeginDisabled(!enabled);
        bool hit = UiAuto.Button(label);
        ImGui.EndDisabled();
        return hit;
    }

    /** A toolbar warning icon that only shows its tooltip. */
    private static void WarningMark(string tooltip)
    {
        float size = ImGui.GetFrameHeight();
        Flow(size);
        var at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new(size));
        ImGuiIcons.Draw(ImGui.GetWindowDrawList(), StudioIcon.Warning, at + new Vector2(size * .22f), size * .56f, ImGui.GetColorU32(ProblemColor));
        Tip(tooltip);
    }

    private void Adopt(MissionScriptEntry entry, ScriptHome where, bool onDisk)
    {
        var document = MissionGraphDocument.Parse(onDisk ? MissionScriptCatalog.ReadSource(entry.FullPath) : "");
        Adopt(entry, where, onDisk, document);
    }

    /** Makes entry the open script and resets every per-script view state. */
    private void Adopt(MissionScriptEntry entry, ScriptHome where, bool onDisk, MissionGraphDocument document)
    {
        RequireNoLuaDrafts();
        var installed = catalog ?? throw new InvalidOperationException("Installed scripts did not load.");
        var (schema, schemaStatus) = installed.ApiState;
        // The tables the API schema names, such as mission.TaskGroup, feed the typed pickers.
        var entryReferences = installed.ReadReferences(entry, schema?.MissionTables);
        script = entry; home = where; stored = onDisk; graph = document; references = entryReferences;
        legacyAssetId = null;
        selected = ""; groupKey = MissionScriptGraph.Root; undo.Clear(); redo.Clear();
        IsDirty = false; Note(""); ClearLuaDrafts();
        typedNode = typedLeg = actionNode = ""; newCue = newFilter = null; actionDraft = null;
        AdoptApi(schema, schemaStatus);
        canvas.Reset();
        if (onDisk && Layouts is { } layouts) foreach (var (key, point) in layouts.Load(entry.FullPath)) canvas.Positions[key] = point;
        Refresh();
    }

    private MissionLayoutStore? Layouts => LayoutDirectory is null ? null : new(LayoutDirectory);

    private void SaveLayout()
    {
        if (ScriptPath is not { } path || Layouts is not { } layouts) return;
        Try(() => layouts.Save(path, canvas.Positions));
    }

    /** Rebuilds every view of the current document: references, typed API, script graph and scene checks. */
    private void Refresh()
    {
        source.Reset(graph!.Source);
        if (catalog is not null && script is not null)
        {
            var current = catalog.DescribeSource(script.Name, script.RelativePath, graph) with { FullPath = script.FullPath };
            if (current.SdkModule != script.SdkModule)
            { script = current; references = catalog.ReadReferences(current, apiSchema?.MissionTables); }
        }
        visual = new(graph, references);
        RefreshApi();
        canvasCache = default;
        try { scriptGraph = MissionScriptGraph.Parse(graph.Source, graph.Groups, apiContext); scriptError = ""; }
        catch (InvalidDataException error) { scriptGraph = null; scriptError = error.Message; }
        // With typed API checks on, they already check scene sends.
        try { sceneChecks = visual.SceneDiagnostics(sends: scriptGraph?.Api is null); }
        catch (InvalidDataException error) { sceneChecks = [new("Scene checks: " + error.Message)]; }
        // Unified Graph: placeholders a template left, and Slot/Squad/Directive/states names this mission's SDK (the map) lacks.
        var names = MissionTemplates.Check(graph.Source, references);
        if (names.Count > 0) sceneChecks = [.. sceneChecks, .. names.Select(finding => new MissionGraphDiagnostic(finding.Message))];
        if (!GroupExists(groupKey)) groupKey = MissionScriptGraph.Root;
    }

    private bool Change(Func<MissionGraphDocument, MissionGraphDocument> operation) => ChangeCore(operation, null);

    /** Applies one visual-authoring edit to the current document. */
    private bool Author(Func<MissionVisualAuthoring, MissionGraphDocument> edit) =>
        Change(document => edit(new(document, references)));

    private bool ChangeLua(Func<MissionGraphDocument, MissionGraphDocument> operation, params string[] applied) =>
        ChangeCore(operation, applied);

    /** Applies one edit as one undo step; applied names the Lua drafts the edit consumes. */
    private bool ChangeCore(Func<MissionGraphDocument, MissionGraphDocument> operation, IReadOnlyCollection<string>? applied)
    {
        return Try(() =>
        {
            RequireLuaChange(applied);
            ApplyDraft();
            var next = operation(graph!);
            if (next.Source != graph!.Source)
            { undo.Push(graph.Source); redo.Clear(); graph = next; IsDirty = true; Note(""); Refresh(); }
            foreach (string id in applied ?? []) luaDrafts.Remove(id);
        });
    }

    private bool Try(Action action)
    {
        try { action(); return true; }
        catch (Exception error) when (Recoverable(error)) { Fail(error.Message); return false; }
    }

    /**
     * Re-reads the script lists and API schema, as the toolbar's Reload does (the AI calls it after a path setting changes).
     * @return false when an unsaved draft is open; the page then asks the user before reloading.
     */
    public bool ReloadScripts(string root)
    {
        catalogTried = null;
        RequestSwitch(() => Reload(root));
        return !IsDirty;
    }

    private void RequestSwitch(Action action)
    {
        if (!IsDirty) { Try(action); return; }
        pendingSwitch = action; showSwitch = true;
    }

    private void FinishSwitch()
    {
        var action = pendingSwitch; pendingSwitch = null; showSwitch = false;
        if (action is not null) Try(action);
    }

    /** A canceled switch keeps the draft until it is resolved. */
    private void DrawSwitch()
    {
        if (showSwitch) { ImGui.OpenPopup("Unsaved mission script"); showSwitch = false; }
        if (!ImGui.BeginPopupModal("Unsaved mission script", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextUnformatted(script?.Name ?? "Mission script");
        if (HasLuaDrafts) ImGui.TextWrapped("Apply or revert Lua edits to save. Discard drops them.");
        ImGui.BeginDisabled(!CanSave);
        if (UiAuto.Button("Save and continue") && Try(() => { ApplyDraft(); Save(); }))
        { FinishSwitch(); ImGui.CloseCurrentPopup(); }
        ImGui.EndDisabled(); ImGui.SameLine();
        if (UiAuto.Button("Discard"))
        { ClearLuaDrafts(); IsDirty = false; source.Reset(graph?.Source ?? ""); FinishSwitch(); ImGui.CloseCurrentPopup(); }
        ImGui.SameLine(); if (UiAuto.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    /** Installed scripts write the installed file; mod scripts write the project's scripts folder. */
    private void Save()
    {
        if (graph is null || script is null) return;
        if (!CanSave) throw new InvalidOperationException("Open this script's project to save it.");
        RequireNoLuaDrafts();
        string target;
        if (home == ScriptHome.Installed)
        {
            var installed = catalog ?? throw new InvalidOperationException("Installed scripts did not load.");
            if (!stored && File.Exists(installed.InstalledPath(script.RelativePath)))
                throw new InvalidOperationException("An installed script for this activity exists. Open it instead.");
            target = installed.SaveInstalled(script.RelativePath, graph.Source);
            if (!stored) installed.AddInstalled(script with { FullPath = target });
        }
        else
        {
            if (legacyAssetId is { } legacy)
                target = MissionProject.MigrateLegacy(project!, legacy, graph.Source);
            else if (!stored && File.Exists(MissionProject.PathFor(project!, script.RelativePath)))
                throw new InvalidOperationException("This project already has a script for this activity. Open it instead.");
            else target = MissionProject.Save(project!, script.RelativePath, graph.Source);
            workspaceOwner?.MissionSourceSaved(project!);
        }
        legacyAssetId = null;
        script = script with { FullPath = target };
        stored = true;
        SaveLayout();
        IsDirty = false;
        Note("Saved " + Path.GetFileName(target));
    }

    private void DrawSource(bool editable)
    {
        ImGui.BeginDisabled(!editable || !HasSourceDraft);
        if (UiAuto.SmallButton("Apply")) ApplySource(force: true);
        ImGui.SameLine();
        if (UiAuto.SmallButton("Revert")) source.Reset(graph!.Source);
        ImGui.EndDisabled();
        ImGui.SameLine();
        bool live = source.Live;
        if (UiAuto.Checkbox("Live##lua-live", ref live)) source.Live = live;
        Tip("Apply Lua edits to the nodes after a short pause. Lua that does not parse is kept here and listed in Checks.");
        if (source.Error.Length > 0) { ImGui.SameLine(); ImGui.TextColored(ProblemColor, "Not applied: see Checks"); }
        if (editable) ImGui.SameLine();
        // Room to paste the largest installed script over the draft.
        string text = source.Draft;
        LuaText("source", ref text, new(-1, -1), editable, headroom: 512 * 1024);
        if (!editable) return;
        source.Edit(text, Now);
        if (source.Due(Now)) ApplySource(force: false);
    }

    /** Lua tab to nodes: one undo step; bad Lua leaves the graph and the draft as they are and reports in Checks. */
    private void ApplySource(bool force)
    {
        if (graph is null || HasLuaDrafts) return;
        source.TryApply(text =>
        {
            var next = MissionGraphDocument.Parse(text);
            if (next.Source == graph!.Source) return;
            undo.Push(graph.Source); redo.Clear(); graph = next; IsDirty = true; Note(""); Refresh();
        }, Now, force);
    }

    private static string Friendly(string text)
    {
        string value = text.Replace('_', ' ').Replace('.', ' ');
        return value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
    }
}
