using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor.Creation;

/** A blank graph or a copy compiles into a new project entry; the source program is never written. */
public sealed class PerkBehaviorWorkflow : CreationWorkflow
{
    public const string Unsupported = "Uses operations that cannot be compiled yet.";
    private const string NotUsed = "No item uses it yet.";
    private const string SelfNamed = "Names itself in a branch that cannot be moved.";
    /** Stands in for the new entry's tag; Create always replaces it. */
    private const uint PendingTag = 0xFFFFFFFF;
    private readonly CreationField start, name, behavior, package, advanced;
    /** Tag of the program the editor holds; null for a blank graph. */
    private uint? loaded;
    private string sourceProblem = "";
    /** Set by Retry; the source is read again even though it did not change. */
    private bool reload;
    /** The name Studio last filled in; a name equal to it is replaced on a source change. */
    private string autoName = "";
    public PerkBehaviorEditor Editor { get; } = new();

    /** @param copy Starts on Pick existing instead of Blank graph. */
    public PerkBehaviorWorkflow(StudioWorkspace workspace, bool copy = false)
    {
        start = new("Start from", CreationFieldKind.Tag, true, "Perk program the new entry copies; read only")
        {
            Rows = () => workspace.CategoryRowsOf(StudioCategories.PerksName),
            Part = new("Blank graph", "", [])
        };
        start.MakeNew = !copy;
        name = new("Name", CreationFieldKind.Text, true, "Name of the authored perk behavior");
        behavior = new("Behavior", CreationFieldKind.Rows, false, "Native conditions and effects")
        { DrawRows = () => DrawEditor(workspace) };
        package = PackageField(workspace);
        package.Visible = false;
        advanced = new("Advanced", CreationFieldKind.Rows, false, "Package allocation")
        { DrawRows = DrawAdvanced };
        Editor.Load(Blank());
    }

    public override string Title => start.MakeNew ? "New perk behavior" : "Clone perk behavior";
    public override string CreateLabel => "Compile and create";
    public override IReadOnlyList<CreationField> Fields => [start, name, behavior, advanced, package];
    public override CreationField? CloneSource => start;
    public override IReadOnlyList<string> Limits =>
    [
        NotUsed,
        "Only three node kinds work in game.",
        "Unsupported installed operations stay read-only."
    ];

    /** A new source replaces the draft; going back to Pick existing with no source keeps it. */
    public override void Update(StudioWorkspace workspace)
    {
        uint? wanted = start.MakeNew ? null : start.Tag;
        if (wanted == loaded && !reload || !start.MakeNew && wanted is null || workspace.Catalog is not { } catalog) return;
        loaded = wanted; sourceProblem = ""; reload = false;
        if (wanted is not { } tag)
        {
            Editor.Load(Blank());
            if (name.Text == autoName) name.Text = autoName = "";
            return;
        }
        PerkBehaviorGraph? graph = null;
        if (catalog.TryGetEntry(tag, out var entry) && entry.Reference == InvestmentSandboxPerkAuthoring.GameplayClass)
        {
            try { graph = OpenProgram(catalog.Read(tag), tag, out sourceProblem); }
            catch (Exception error) when (error is IOException or InvalidDataException or KeyNotFoundException
                or UnauthorizedAccessException)
            { sourceProblem = Unreadable(error); }
        }
        if (graph is null && sourceProblem.Length == 0) sourceProblem = "Choose an installed or project perk program.";
        Editor.Load(graph ?? Blank());
        if (graph is not null) CheckSelfNamed();
        if (name.Text.Trim().Length == 0 || name.Text == autoName)
            name.Text = autoName = graph is null ? "" : start.TagLabel + " copy";
    }

    /** A blank graph is never reread; that would drop the user's draft. */
    protected override void Reload() => reload = loaded is not null;

    /** A blank draft names itself with PendingTag, so a new owner damage multiplier validates. */
    private static PerkBehaviorGraph Blank()
    {
        var graph = NativePerkBehavior.New();
        graph.ProgramTag = PendingTag;
        return graph;
    }

    /** Flags a copy that names itself where Compile cannot move it; other errors show at Create. */
    private void CheckSelfNamed()
    {
        try { Compile(PendingTag); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException
            or KeyNotFoundException or OverflowException or ArgumentException)
        { if (error.Message == SelfNamed) sourceProblem = SelfNamed; }
    }

    /**
     * The supported-program check every perk workflow shares.
     * @return The graph, named by its own tag; null with problem set when Studio cannot compile it.
     */
    public static PerkBehaviorGraph? OpenProgram(ReadOnlyMemory<byte> bytes, uint tag, out string problem)
    {
        problem = "";
        try
        {
            var graph = NativePerkBehavior.Decompile(bytes);
            graph.ProgramTag = tag;
            return graph;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException
            or KeyNotFoundException or OverflowException or ArgumentException)
        { problem = Unsupported; return null; }
    }

    protected override IEnumerable<string> Validate(StudioWorkspace workspace)
    {
        if (!start.MakeNew && sourceProblem.Length != 0) yield return sourceProblem;
        if (PackageProblem(workspace, package) is { } problem) yield return problem;
        if (name.Text.Trim().Length > 200) yield return "Name is longer than 200 characters.";
        foreach (string diagnostic in Editor.Problems) yield return diagnostic;
    }

    /** Compilation finishes before the importer stages a new native entry. */
    public override CreationFollowUp? Create(StudioWorkspace workspace)
    {
        var problems = Problems(workspace);
        if (problems.Count != 0) throw new InvalidDataException(string.Join("\n", problems));
        ushort packageId = PackageId(package);
        int index = workspace.NextImportEntry(packageId);
        byte[] bytes = Compile(TagOf(packageId, index));
        var addition = new ProjectAssetAddition(FileName(name.Text.Trim()) + ".perk", bytes,
            new(packageId, InvestmentSandboxPerkAuthoring.GameplayClass, 8U << 9, name.Text.Trim(), index));
        workspace.ImportAsset((_, _) => new([addition]));
        return new(Page: "sunrise.perk-behavior", Status: "Perk behavior created", Background: true);
    }

    private byte[] Compile(uint tag) => CompileCopy(Editor, tag, loaded);

    /**
     * Self references must name the new entry; one left on the source tag is refused.
     * @param tag Tag of the new entry.
     * @param source Tag of the copied program; null for a blank graph.
     * @return The compiled bytes; the editor's graph keeps its own tag.
     */
    public static byte[] CompileCopy(PerkBehaviorEditor editor, uint tag, uint? source)
    {
        var graph = editor.Document;
        uint own = graph.ProgramTag;
        graph.ProgramTag = tag;
        try
        {
            byte[] bytes = editor.Compile();
            if (source is { } old && bytes.AsSpan().IndexOf(BitConverter.GetBytes(old)) >= 0)
                throw new InvalidDataException(SelfNamed);
            return bytes;
        }
        finally { graph.ProgramTag = own; }
    }

    private void DrawEditor(StudioWorkspace workspace)
    {
        ImGui.BeginChild("new-perk-graph", new(0, 460 * CreationWidgets.Scale), ImGuiChildFlags.Borders);
        Editor.Draw("new-perk", workspace.CanEdit);
        ImGui.EndChild();
    }

    private void DrawAdvanced()
    {
        if (!ImGui.CollapsingHeader("Package settings")) return;
        string value = package.Text;
        ImGui.SetNextItemWidth(150 * CreationWidgets.Scale);
        if (UiAuto.InputText("Package ID", ref value, 4, ImGuiInputTextFlags.CharsHexadecimal)) package.Text = value;
        if (package.Hint.Length != 0) StudioUi.Help(package.Hint);
    }
}
