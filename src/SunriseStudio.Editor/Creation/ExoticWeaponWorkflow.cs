using System.Globalization;
using ImGuiNET;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor.Creation;

/** One draft connects native behavior graphs, item rows and Blender art. */
public sealed class ExoticWeaponWorkflow : CreationWorkflow
{
    /** This installed item supplies the checked weapon authoring profile. */
    private const uint CheckedBaseItem = 0x8132017D;
    /** File stamps are read at most this often, in milliseconds. */
    private const int StampInterval = 500;
    internal const string ExportFilter = "Sunrise weapon (GLB)|*.glb";
    internal const string ExportHint = "Blender: File > Export > Sunrise weapon.";
    private const string Symbol = "Transparent symbol PNG";
    private const string NewGraphOnly = "New graphs only for now.";

    /** Optional fields under a heading field, so the dialog draws them in order. */
    private sealed record Section(string Name, CreationField Header, CreationField[] Members);

    private readonly StudioWorkspace workspace;
    private readonly CreationField baseWeapon, name, description, archetype, firstName, firstDescription,
        secondName, secondDescription, catalystName, catalystDescription, normalPerk, file, collection,
        firstGraph, secondGraph, stats, surfaces, advanced, package,
        bonusName, bonusDescription, bonusGraph, weaponIcon, firstIcon, secondIcon,
        catalystIcon, bonusIcon, hudIcon;
    private readonly CreationField[] required;
    private readonly Section[] sections;
    private readonly HashSet<string> openSections = ["Weapon", "Art and import"];
    private readonly Dictionary<byte, int> catalystStats = new() { [27] = 20, [22] = 15 };
    private InvestmentItem? source;
    private InvestmentItemDetail? detail;
    private IReadOnlyList<InvestmentCollectionNode> collections = [];
    private IReadOnlyList<WeaponArtVariant> variants = [];
    private uint? loadedBase;
    private long loadedOverlay = -1;
    private string sourceError = "", inspectedKey = "", inspectionError = "", stampedPath = "";
    private long nextStamp;
    private bool inspecting;
    private OriginalAssetInspection? inspection;
    private int firstSocket = -1, secondSocket = -1, catalystSocket = -1;
    private ushort firstTemplate, secondTemplate, catalystTemplate;
    private uint newStat = 27;
    private int newStatValue = 10;
    private (int Revision, CreationChoice[] Choices, HashSet<int> Indexes) normalPerks = (-1, [], []);
    private (IReadOnlyList<InvestmentCollectionNode>? Nodes, CreationChoice[] Rows) collectionChoices = (null, Array.Empty<CreationChoice>());
    private (int Revision, CreationChoice[] Rows) statChoices = (-1, Array.Empty<CreationChoice>());
    public PerkBehaviorEditor FirstBehavior { get; } = new();
    public PerkBehaviorEditor SecondBehavior { get; } = new();
    public PerkBehaviorEditor CatalystBehavior { get; } = new();

    public ExoticWeaponWorkflow(StudioWorkspace workspace)
    {
        this.workspace = workspace;
        baseWeapon = new("Base:", CreationFieldKind.Rows, false, "Native weapon and socket layout") { DrawRows = DrawBase };
        name = new("Name", CreationFieldKind.Text, true, "Weapon name");
        description = new("Description", CreationFieldKind.Text, false, "Weapon description") { Lines = 2 };
        archetype = new("Weapon type", CreationFieldKind.Text, true, "Display type line, type identifier, weapon_type trait and new archetype identity");
        firstName = new("Intrinsic name", CreationFieldKind.Text, true, "First new perk name");
        firstDescription = new("Intrinsic description", CreationFieldKind.Text, false, "First perk description") { Lines = 2 };
        secondName = new("Trait name", CreationFieldKind.Text, true, "Second new perk name");
        secondDescription = new("Trait description", CreationFieldKind.Text, false, "Second perk description") { Lines = 2 };
        weaponIcon = IconField("Weapon icon (PNG)", "96 x 96 PNG");
        firstIcon = IconField("Intrinsic icon (PNG)", Symbol);
        secondIcon = IconField("Trait icon (PNG)", Symbol);
        catalystIcon = IconField("Catalyst icon (PNG)", Symbol);
        bonusIcon = IconField("Catalyst perk icon (PNG)", Symbol);
        hudIcon = IconField("HUD silhouette (PNG)", "137 x 76 transparent PNG. " + StudioUi.MayNotWork);
        catalystName = new("Catalyst name", CreationFieldKind.Text, true, "New catalyst name");
        catalystDescription = new("Catalyst description", CreationFieldKind.Text, false, "Catalyst description") { Lines = 2 };
        bonusName = new("Catalyst perk name", CreationFieldKind.Text, true, "New catalyst bonus perk name");
        bonusDescription = new("Catalyst perk description", CreationFieldKind.Text, false, "New catalyst bonus perk description")
        { Lines = 2 };
        bonusGraph = new("Catalyst perk behavior", CreationFieldKind.Rows, false, "Fresh normal perk behavior graph")
        { DrawRows = () => DrawBehavior(CatalystBehavior, "Catalyst perk behavior", bonusName.Text) };
        normalPerk = new("Catalyst bonus perk", CreationFieldKind.Tag, true, "An installed normal sandbox perk")
        {
            Tag = 421, TagLabel = "Outlaw", Search = SearchPerks,
            Part = new("New graph", "", [bonusName, bonusDescription, bonusGraph, bonusIcon])
        };
        normalPerk.MakeNew = true;
        file = new("Blender asset", CreationFieldKind.File, true, "Original weapon scene and its sidecars")
        { FileFilter = ExportFilter, Hint = ExportHint };
        collection = new("Collections", CreationFieldKind.Scalar, false, "New collectible and collection page")
        { Text = "0", Choices = CollectionChoices };
        firstGraph = new("Intrinsic behavior", CreationFieldKind.Rows, false, "Fresh native behavior graph")
        { DrawRows = () => DrawBehavior(FirstBehavior, "Intrinsic behavior", firstName.Text), Hint = NewGraphOnly };
        secondGraph = new("Trait behavior", CreationFieldKind.Rows, false, "Fresh native behavior graph")
        { DrawRows = () => DrawBehavior(SecondBehavior, "Trait behavior", secondName.Text), Hint = NewGraphOnly };
        stats = new("Catalyst stat bonuses", CreationFieldKind.Rows, false, "Signed stat contributions") { DrawRows = DrawStats };
        surfaces = new("Imported surfaces", CreationFieldKind.Rows, false, "Import validation report") { DrawRows = DrawSurfaces };
        package = PackageField(workspace); package.Visible = false;
        advanced = new("Package and sockets", CreationFieldKind.Rows, false, "Package and socket choices") { DrawRows = DrawAdvanced };
        required = [name, archetype, firstName, secondName, catalystName, normalPerk, file];
        sections =
        [
            NewSection("Weapon", baseWeapon, description, weaponIcon, hudIcon, collection),
            NewSection("Intrinsic", firstDescription, firstGraph, firstIcon),
            NewSection("Trait", secondDescription, secondGraph, secondIcon),
            NewSection("Catalyst", catalystDescription, stats, catalystIcon),
            NewSection("Art and import", surfaces),
            NewSection("Advanced", advanced)
        ];
    }

    public override string Title => "New exotic weapon";
    public override string CreateLabel => "Create exotic weapon";
    public override uint? AnchorTag => baseWeapon.Tag;
    /** Required fields first, then each section with its fields when open. */
    public override IReadOnlyList<CreationField> Fields =>
    [
        .. required,
        .. sections.SelectMany(section => new[] { section.Header }
            .Concat(openSections.Contains(section.Name) ? section.Members : [])),
        package
    ];
    /** Every input, including closed sections and the catalyst part's new fields. */
    public IReadOnlyList<CreationField> AllFields =>
        [.. required, .. normalPerk.Part!.NewFields, .. sections.SelectMany(section => section.Members), package];

    public override void Update(StudioWorkspace workspace)
    {
        // A read that ended without its result waits for the files to change.
        if (inspecting && !workspace.IsBusy)
        {
            inspecting = false;
            inspectionError = workspace.Error.Length != 0 ? workspace.Error : "Read stopped. Export again to retry.";
        }
        if (!InvestmentReady(workspace)) return;
        if (baseWeapon.Tag is null && workspace.Investment!.FindItem(CheckedBaseItem) is { } supported)
        { baseWeapon.Tag = supported.Tag; baseWeapon.TagLabel = supported.Name; }
        if (baseWeapon.Tag != loadedBase || loadedOverlay != workspace.Catalog?.OverlayRevision) LoadBase();
        InspectSource();
    }

    /** Default roles follow native intrinsic, trait and catalyst sockets of the chosen base. */
    private void LoadBase()
    {
        loadedBase = baseWeapon.Tag; source = null; detail = null; variants = [];
        loadedOverlay = workspace.Catalog?.OverlayRevision ?? -1;
        sourceError = ""; inspection = null; inspectedKey = ""; nextStamp = 0;
        firstSocket = secondSocket = catalystSocket = -1;
        firstTemplate = secondTemplate = catalystTemplate = ushort.MaxValue;
        if (loadedBase is not { } tag || workspace.Investment is not { } investment) return;
        try
        {
            source = investment.FindItem(tag) ?? throw new InvalidDataException("The base weapon is not in this catalog.");
            byte[] bytes = workspace.Catalog!.Read(source.Tag);
            detail = investment.ReadItem(source.Tag, bytes);
            var profile = WeaponArchetypeAuthoring.Read(workspace.Catalog, bytes);
            if (profile.EntityTag != WeaponPresentationAuthoring.GameplayTemplate
                || !WeaponAssetEntityCompiler.Supports(profile.Variants))
                throw new NotSupportedException("Tommy's Matchbook does not have the checked layout.");
            variants = profile.Variants;
            firstSocket = detail.Sockets.FirstOrDefault(socket => socket.Type == 176)?.Index ?? -1;
            secondSocket = detail.Sockets.FirstOrDefault(socket => socket.Type == 92)?.Index ?? -1;
            foreach (var socket in detail.Sockets)
            {
                if (socket.Type == ushort.MaxValue) continue;
                var templates = investment.SocketPlugTemplates(tag, bytes, socket);
                if (socket.Index == firstSocket && templates.Count != 0) firstTemplate = templates[0];
                if (socket.Index == secondSocket && templates.Count != 0) secondTemplate = templates[0];
                var catalyst = templates.FirstOrDefault(index => investment.ItemName(index).Contains("Catalyst", StringComparison.OrdinalIgnoreCase)
                    && !investment.ItemName(index).Contains("Empty", StringComparison.OrdinalIgnoreCase), ushort.MaxValue);
                if (catalyst != ushort.MaxValue) { catalystSocket = socket.Index; catalystTemplate = catalyst; }
            }
            collections = investment.CollectionNodes();
            int collectible = InvestmentCollections.FindCollectible(workspace.Catalog.Read(new InvestmentSources().Collectibles), source.Index);
            var node = collections.FirstOrDefault(value => value.Collectibles.Contains(collectible));
            collection.Text = node is null ? "0" : (node.Index + 1).ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException or OverflowException)
        { sourceError = error.Message; }
        // Retry reads the base again.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { sourceError = Unreadable(error); loadedOverlay = -1; }
    }

    /** Changes when Blender rewrites the scene, its sidecars or its arms scene. */
    internal static string SourceStamp(string path)
    {
        string stem = Path.ChangeExtension(Path.GetFullPath(path), null);
        return string.Join("|", new[] { path, stem + WeaponOriginalAssetImporter.SidecarSuffix,
            stem + OriginalAssetImporter.SidecarSuffix, stem + WeaponArmsRig.SourceSuffix }.Select(IconChecks.Stamp));
    }

    /** Reads the export on a worker when its files change; stamps are read twice a second. */
    private void InspectSource()
    {
        string path = file.Text.Trim();
        long now = Environment.TickCount64;
        if (path == stampedPath && now < nextStamp) return;
        stampedPath = path; nextStamp = now + StampInterval;
        string key = "";
        try { if (variants.Count != 0 && File.Exists(path)) key = SourceStamp(path); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { }
        if (key.Length == 0) { inspection = null; inspectionError = ""; inspectedKey = ""; return; }
        if (key == inspectedKey || workspace.IsBusy) return;
        inspectedKey = key; inspection = null; inspectionError = ""; inspecting = true;
        workspace.InspectWeaponExport(path, variants, (result, error) =>
        {
            inspecting = false;
            if (key == inspectedKey) { inspection = result; inspectionError = error; }
        });
    }

    protected override IEnumerable<string> Validate(StudioWorkspace workspace)
    {
        if (workspace.Investment is null) { yield return "Reading the item catalog."; yield break; }
        if (sourceError.Length != 0) yield return sourceError;
        if (source is null || detail is null)
        {
            if (sourceError.Length == 0) yield return "Tommy's Matchbook is not in this install.";
            yield break;
        }
        // The catalyst part checks its own new fields; closed sections are checked here.
        foreach (var field in sections.Where(section => !openSections.Contains(section.Name)).SelectMany(section => section.Members))
            foreach (string problem in field.Problems()) yield return problem;
        if (firstSocket < 0 || secondSocket < 0 || catalystSocket < 0
            || firstTemplate == ushort.MaxValue || secondTemplate == ushort.MaxValue || catalystTemplate == ushort.MaxValue)
            yield return "The base needs intrinsic, trait and catalyst sockets.";
        if (PackageProblem(workspace, package) is { } packageProblem) yield return packageProblem;
        foreach (var field in new[] { name, archetype, firstName, secondName, catalystName, bonusName }.Where(Active))
            if (field.Text.Trim().Length > 200) yield return field.Label + " is longer than 200 characters.";
        foreach (var field in new[] { description, firstDescription, secondDescription, catalystDescription, bonusDescription }.Where(Active))
            if (field.Text.Length > 4000) yield return field.Label + " is longer than 4000 characters.";
        foreach (string problem in FirstBehavior.Problems) yield return "Intrinsic: " + problem;
        foreach (string problem in SecondBehavior.Problems) yield return "Trait: " + problem;
        if (FirstBehavior.Document.OriginalBytes.Length != 0 || SecondBehavior.Document.OriginalBytes.Length != 0)
            yield return "Use new graphs for the intrinsic and unique trait.";
        if (NewBonus)
        {
            foreach (string problem in CatalystBehavior.Problems) yield return "Catalyst perk: " + problem;
            if (CatalystBehavior.Document.OriginalBytes.Length != 0) yield return "Use a new graph for the catalyst bonus.";
        }
        else if (normalPerk.Tag is not { } perk || perk >= workspace.Investment.PerkCount)
            yield return "Choose a normal perk for the catalyst.";
        else if (!IsNormalPerk(perk))
            yield return "Choose a perk with normal presentation for the catalyst.";
        if (inspectionError.Length != 0) yield return inspectionError;
        else if (inspecting) yield return "Reading the Blender export.";
        else if (inspection is null) yield return "Choose a Blender export.";
        else foreach (var surface in inspection.Surfaces.Where(value => value.Present && !value.Supported))
            yield return surface.Name + ": " + surface.Detail;
        if (!weaponIcon.IsEmpty && IconChecks.Size(weaponIcon.Text.Trim(), 96, 96) is { } weaponProblem)
            yield return weaponIcon.Label + ": " + weaponProblem;
        foreach (var field in new[] { firstIcon, secondIcon, catalystIcon, bonusIcon }
            .Where(field => Active(field) && !field.IsEmpty))
            if (IconChecks.Symbol(field.Text.Trim()) is { } problem) yield return field.Label + ": " + problem;
        if (!hudIcon.IsEmpty && IconChecks.Size(hudIcon.Text.Trim(), 137, 76) is { } hudProblem)
            yield return hudIcon.Label + ": " + hudProblem;
    }

    private bool IsNormalPerk(uint perk) => NormalPerks().Indexes.Contains((int)perk);

    /** Perks with normal presentation; the display table is read once per investment revision. */
    private (CreationChoice[] Choices, HashSet<int> Indexes) NormalPerks()
    {
        if (normalPerks.Revision == workspace.InvestmentRevision) return (normalPerks.Choices, normalPerks.Indexes);
        var investment = workspace.Investment!;
        byte[] displays = workspace.Catalog!.Read(InvestmentSandboxPerkAuthoring.DisplayTableTag);
        var normal = investment.PerkDefinitions.Where(perk => InvestmentSandboxPerkAuthoring.ReadPresentation(displays, perk.Index)
            == SandboxPerkPresentation.Normal).ToArray();
        normalPerks = (workspace.InvestmentRevision,
            normal.Where(perk => perk.Name.Length != 0).Select(perk => new CreationChoice((uint)perk.Index, perk.Name, perk.Description)).ToArray(),
            normal.Select(perk => perk.Index).ToHashSet());
        return (normalPerks.Choices, normalPerks.Indexes);
    }

    /** Sets the catalyst stat (0 removes it); throws for a stat this installation lacks. */
    public void SetCatalystStat(byte index, int value)
    {
        if (workspace.Investment?.StatDefinitions.Any(stat => stat.Index == index) != true)
            throw new InvalidDataException("Choose a stat from this installation.");
        if (value == 0) catalystStats.Remove(index); else catalystStats[index] = value;
    }

    private bool NewBonus => normalPerk.MakeNew;

    /** The catalyst part's new fields count only while New is chosen. */
    private bool Active(CreationField field) => NewBonus || !normalPerk.Part!.NewFields.Contains(field);

    /** The weapon definition built from the form fields. */
    public ExoticWeaponDefinition Definition()
    {
        var catalyst = new ExoticCatalystDefinition(catalystName.Text.Trim(), catalystDescription.Text.Trim(),
            checked((ushort)(normalPerk.Tag ?? ushort.MaxValue)), catalystStats.ToDictionary(), catalystSocket, catalystTemplate)
        {
            AuthoredBonus = NewBonus ? new(bonusName.Text.Trim(), bonusDescription.Text.Trim(), CatalystBehavior.Document)
                { IconPath = OptionalIcon(bonusIcon) } : null,
            IconPath = OptionalIcon(catalystIcon)
        };
        return new(name.Text.Trim(), description.Text.Trim(), archetype.Text.Trim(),
            baseWeapon.Tag ?? throw new InvalidDataException("Tommy's Matchbook is not in this install."),
            new(firstName.Text.Trim(), firstDescription.Text.Trim(), FirstBehavior.Document, firstSocket, firstTemplate)
                { IconPath = OptionalIcon(firstIcon) },
            new(secondName.Text.Trim(), secondDescription.Text.Trim(), SecondBehavior.Document, secondSocket, secondTemplate)
                { IconPath = OptionalIcon(secondIcon) },
            catalyst, PackageId(package), collection.TryParse(out ulong node) && node != 0 ? checked((int)node - 1) : null)
            { IconPath = OptionalIcon(weaponIcon), HudIconPath = OptionalIcon(hudIcon) };
    }

    private static CreationField IconField(string label, string hint) => new(label, CreationFieldKind.File,
        false, "Private native icon artwork") { FileFilter = "PNG image|*.png", Hint = hint };
    private static string? OptionalIcon(CreationField field) => field.IsEmpty ? null : field.Text.Trim();

    private Section NewSection(string title, params CreationField[] members) =>
        new(title, new("", CreationFieldKind.Rows, false, "Section heading") { DrawRows = () => DrawSection(title) }, members);

    /** The workspace owns cancellation, source checks and the one publication of this draft. */
    public override CreationFollowUp? Create(StudioWorkspace workspace)
    {
        var problems = Problems(workspace);
        if (problems.Count != 0) throw new InvalidDataException(string.Join("\n", problems));
        workspace.CreateExoticWeapon(Definition(), file.Text.Trim());
        return new(Page: "sunrise.weapon", Status: "Exotic weapon created", Background: true);
    }

    /** The catalyst picker offers only normal perk presentation. */
    private IEnumerable<CreationChoice> SearchPerks(string search)
    {
        if (workspace.Investment is null || workspace.Catalog is null) return [];
        return NormalPerks().Choices.Where(perk => search.Length == 0
            || perk.Label.Contains(search, StringComparison.OrdinalIgnoreCase)
            || perk.Detail.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<CreationChoice> CollectionChoices()
    {
        if (!ReferenceEquals(collectionChoices.Nodes, collections))
            collectionChoices = (collections, new[] { new CreationChoice(0, "Not in Collections") }
                .Concat(collections.Select(node => new CreationChoice((uint)node.Index + 1,
                    node.Name.Length != 0 ? node.Name : $"Node {node.Index}"))).ToArray());
        return collectionChoices.Rows;
    }

    /** The dialog draws an empty label line first; the heading takes that line. */
    private void DrawSection(string title)
    {
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetTextLineHeightWithSpacing());
        bool open = openSections.Contains(title);
        ImGui.SetNextItemOpen(open, ImGuiCond.Always);
        if (UiAuto.CollapsingHeader(title) == open) return;
        if (open) openSections.Remove(title); else openSections.Add(title);
    }

    private void DrawBase()
    {
        ImGui.SameLine();
        ImGui.TextUnformatted(baseWeapon.Tag is null ? "Tommy's Matchbook (not installed)" : baseWeapon.TagLabel);
        StudioUi.Tip("The only supported base.");
    }

    private void DrawBehavior(PerkBehaviorEditor editor, string title, string perkName)
    {
        var graph = editor.Document;
        if (UiAuto.Button("Edit nodes##" + title)) ImGui.OpenPopup(title);
        ImGui.SameLine();
        ImGui.TextDisabled($"{graph.Activation.Count} / {graph.Effects.Count} / {graph.Deactivation.Count} nodes");
        StudioUi.Tip("Activation / effect / deactivation");
        var size = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowSize(new(Math.Min(1180 * CreationWidgets.Scale, size.X - 40), Math.Min(760 * CreationWidgets.Scale, size.Y - 40)), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(title, ImGuiWindowFlags.NoSavedSettings)) return;
        ImGui.TextUnformatted(perkName.Length == 0 ? title : perkName);
        ImGui.BeginChild("perk-node-editor", new(0, -38 * CreationWidgets.Scale));
        editor.Draw(title, workspace.CanEdit);
        ImGui.EndChild();
        if (UiAuto.Button("Done")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawStats()
    {
        if (workspace.Investment is not { } investment) return;
        foreach (var (index, stored) in catalystStats.ToArray())
        {
            ImGui.PushID(index); int value = stored;
            ImGui.SetNextItemWidth(120 * CreationWidgets.Scale);
            if (UiAuto.InputInt(investment.StatName(index), ref value)) SetCatalystStat(index, value);
            ImGui.SameLine();
            if (StudioUi.IconButton("remove", StudioIcon.Remove, "Remove stat")) catalystStats.Remove(index);
            ImGui.PopID();
        }
        if (statChoices.Revision != workspace.InvestmentRevision)
            statChoices = (workspace.InvestmentRevision,
                investment.StatDefinitions.Select(stat => new CreationChoice(stat.Index, stat.Name)).ToArray());
        CreationWidgets.Choice("##bonus-stat", ref newStat, statChoices.Rows, 180);
        ImGui.SameLine(); ImGui.SetNextItemWidth(110 * CreationWidgets.Scale); UiAuto.InputInt("##bonus-value", ref newStatValue);
        ImGui.SameLine();
        if (StudioUi.IconButton("add-stat", StudioIcon.Plus, "Add stat")) SetCatalystStat(checked((byte)newStat), newStatValue);
    }

    private void DrawSurfaces()
    {
        if (inspection is null)
        {
            ImGui.TextDisabled(inspecting ? "Reading the export..." : inspectionError.Length != 0 ? "Read failed" : "Choose a Blender export");
            StudioUi.Tip(inspectionError);
            return;
        }
        CreationWidgets.Surfaces("##surfaces", inspection.Surfaces, WeaponOriginalAssetImporter.ClipBurstSurface);
    }

    private void DrawAdvanced()
    {
        string value = package.Text;
        ImGui.SetNextItemWidth(150 * CreationWidgets.Scale);
        if (UiAuto.InputText("Package ID", ref value, 4, ImGuiInputTextFlags.CharsHexadecimal)) package.Text = value;
        StudioUi.Help(package.Hint);
        if (detail is not null)
            ImGui.TextDisabled($"Sockets: intrinsic {firstSocket + 1}, trait {secondSocket + 1}, catalyst {catalystSocket + 1}");
    }
}
