using ImGuiNET;
using SunriseStudio.Formats;

namespace SunriseStudio.Editor.Creation;

/** Display names resolve through both perk tables before a behavior program opens. */
public sealed class PerkBehaviorPickerWorkflow : CreationWorkflow
{
    private readonly StudioWorkspace workspace;
    private readonly CreationField perk, details;
    private uint? selected;
    private long loadedOverlay = -1;
    private SandboxPerkSource? source;
    private PerkBehaviorGraph? graph;
    private string problem = "";

    public PerkBehaviorPickerWorkflow(StudioWorkspace workspace)
    {
        this.workspace = workspace;
        perk = new("Perk", CreationFieldKind.Tag, true, "Installed or project sandbox perk")
        {
            Search = Search,
            Hint = "Changes every item with this perk. " + StudioUi.MayNotWork
        };
        details = new("Behavior", CreationFieldKind.Rows, false, "Native behavior availability")
        { DrawRows = DrawDetails };
    }

    public override string Title => "Edit perk behavior";
    public override string CreateLabel => "Open node editor";
    public override IReadOnlyList<CreationField> Fields => [perk, details];
    protected override bool RequiresProject => false;

    /** Display-only perks stay selectable so the picker can explain their missing program. */
    public override void Update(StudioWorkspace workspace)
    {
        if (!InvestmentReady(workspace)) return;
        if (perk.Tag == selected && workspace.Catalog!.OverlayRevision == loadedOverlay) return;
        selected = perk.Tag; loadedOverlay = workspace.Catalog!.OverlayRevision;
        source = null; graph = null; problem = "";
        if (selected is not { } index) return;
        try { source = InvestmentSandboxPerkAuthoring.Read(workspace.Catalog, checked((int)index)); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException
            or KeyNotFoundException or OverflowException)
        { problem = error.Message; return; }
        graph = PerkBehaviorWorkflow.OpenProgram(source.Gameplay, source.GameplayTag, out problem);
    }

    protected override void Reload() => loadedOverlay = -1;

    protected override IEnumerable<string> Validate(StudioWorkspace workspace)
    {
        if (workspace.Investment is null) yield return "Reading perk names.";
        else if (problem.Length != 0) yield return problem;
        else if (source is null || graph is null) yield return "Choose a perk with a supported native program.";
    }

    public override CreationFollowUp? Create(StudioWorkspace workspace)
    {
        var problems = Problems(workspace);
        if (problems.Count != 0) throw new InvalidDataException(string.Join("\n", problems));
        return new(Tag: source!.GameplayTag, Page: "sunrise.perk-behavior", Status: "Perk behavior opened");
    }

    private IEnumerable<CreationChoice> Search(string text) => workspace.Investment?.PerkDefinitions
        .Where(value => value.Name.Length != 0 && (text.Length == 0
            || value.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || value.Description.Contains(text, StringComparison.OrdinalIgnoreCase)))
        .Select(value => new CreationChoice((uint)value.Index, value.Name, value.Description)) ?? [];

    private void DrawDetails()
    {
        if (problem.Length != 0) { ImGui.TextDisabled("No supported program"); StudioUi.Tip(problem); return; }
        if (graph is null) { ImGui.TextDisabled("Choose a perk."); return; }
        ImGui.TextUnformatted($"{graph.Activation.Count} / {graph.Effects.Count} / {graph.Deactivation.Count} nodes");
        StudioUi.Tip("Activation / effect / deactivation");
        if (graph.Diagnostics.Count != 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(graph.Diagnostics.Count == 1 ? "1 note" : $"{graph.Diagnostics.Count} notes");
            StudioUi.Tip(string.Join("\n", graph.Diagnostics));
        }
        if (workspace.Project is null) ImGui.TextDisabled("Open a project to save edits.");
    }
}
