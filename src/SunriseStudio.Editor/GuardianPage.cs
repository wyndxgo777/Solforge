using ImGuiNET;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Every guardian choice; each change reopens the body with a new loadout. Draw reads no package. */
public sealed class GuardianPage : IStudioPage
{
    private const string ArmorNote = "Lists every class's items.";
    private const string WeaponNote = "Right hand, in the game's stand pose. " + StudioUi.MayBeInaccurate;
    private const string WeaponWip = "Hand cannons, sidearms and swords hold with an almost open hand. Bows crouch.";
    private const string PlaceholderNote = "Draws near black up close.";
    // Armor slot buckets 3..7, in GuardianLoadout.Armor order.
    private static readonly string[] ArmorLabels = ["Helmet", "Gauntlets", "Chest", "Legs", "Class item"];
    private static readonly string[] ArmorKinds =
        [.. Enumerable.Range(3, 5).Select(bucket => $"{InvestmentItemKinds.Armor} / {InvestmentItemKinds.SlotName((byte)bucket)}")];
    private static readonly string[] ColourLabels = [.. Enumerable.Range(1, 6).Select(index => $"Colour {index}")];
    private readonly Dictionary<string, ItemPicker> pickers = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> itemLabels = [];
    private BrowserAsset[]? armorSource;
    private Dictionary<string, BrowserAsset[]> armorBySlot = new(StringComparer.Ordinal);
    private int labelRevision = -1;
    private View? view;

    /**
     * What Draw needs from one document, worked out once. Notes maps a slot label to its notes.
     * Placeholders holds the class's gear-preview item of each armor slot, 0 when it has none.
     */
    private sealed record View(StudioDocument Document, GuardianAppearance Tables, GuardianLoadout Loadout, GuardianRaceGender Row,
        bool HeadHidden, IReadOnlyList<uint> Placeholders, IReadOnlyDictionary<string, string> Notes);

    /** One row above an item list; Value is null for None, which takes the helmet off. */
    internal sealed record LeadingRow(string Label, uint? Value);

    public string Id => StudioCategories.GuardianPageId;
    public string Title => "Guardian";
    public bool CanOpen(StudioDocument document) => document.OpenContext?.Guardian is not null;

    public void Draw(StudioPageContext context)
    {
        var workspace = context.Workspace;
        if (workspace.Document is not { OpenContext.Guardian: { } open } document) return;
        if (labelRevision != workspace.NameRevision)
        {
            itemLabels.Clear();
            foreach (var picker in pickers.Values) picker.Invalidate();
            labelRevision = workspace.NameRevision;
        }
        uint body = document.Entry.TagHash;
        var current = ViewOf(document, workspace.GuardianAppearance);
        var loadout = current?.Loadout ?? open;
        void Change(GuardianLoadout next)
        {
            if (next != loadout) workspace.Run(() => workspace.OpenGuardian(body, next));
        }

        ImGui.BeginDisabled(workspace.IsBusy);
        DrawToolbar(context, body, loadout, Change);
        if (current is null)
        {
            ImGui.EndDisabled();
            if (document.PreviewError.Length != 0)
            {
                ImGui.TextColored(StudioTheme.Accent, "Guardian did not load");
                StudioUi.Tip(document.PreviewError);
            }
            else ImGui.TextDisabled("Reading guardian...");
            return;
        }
        DrawBody(current, Change);
        DrawGear(workspace, current, Change);
        DrawHead(current, Change);
        DrawColours(current, Change);
        ImGui.EndDisabled();
        var notes = document.Guardian!.Diagnostics;
        if (notes.Count != 0 && ImGui.CollapsingHeader($"Notes ({notes.Count})###notes"))
            StudioUi.Facts("guardian-notes", notes);
    }

    /** The cached view of document; null until the guardian and its tables are read. */
    private View? ViewOf(StudioDocument document, GuardianAppearance? tables)
    {
        if (document.Guardian is not { } guardian || tables is null || document.OpenContext?.Guardian is not { } open) return null;
        if (view is { } held && ReferenceEquals(held.Document, document) && ReferenceEquals(held.Tables, tables)) return held;
        var loadout = tables.Clamp(open);
        var parts = guardian.Parts.Parts;
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string label in ArmorLabels.Append("Weapon"))
            if (guardian.SlotNotes.GetValueOrDefault(label) is { Count: > 0 } lines)
                notes[label] = string.Join("\n", lines.Select(line => line.StartsWith(label + ": ", StringComparison.Ordinal)
                    ? line[(label.Length + 2)..] : line));
        byte? classByte = GuardianClasses.ForBody(document.Entry.TagHash)?.ClassByte;
        uint[] placeholders = [.. ArmorLabels.Select((_, slot) => classByte is { } value ? tables.PreviewDefault(value, slot) : 0)];
        return view = new(document, tables, loadout, tables.Rows[loadout.RaceGender],
            loadout.ShowHelmet && parts.Any(part => part is { Kind: GuardianPartKind.Armor, Label: "Helmet" }), placeholders, notes);
    }

    /** Armor slots 0..4 start with Default and only the helmet offers None; slot 5, the weapon, starts with None. */
    internal static IReadOnlyList<LeadingRow> LeadingRows(int slot) => slot switch
    {
        0 => [new("Default", 0), new("None", null)],
        >= 1 and <= 4 => [new("Default", 0)],
        5 => [new("None", 0)],
        _ => throw new ArgumentOutOfRangeException(nameof(slot))
    };

    /** The loadout after a pick in armor slot 0..4; null is None, which takes the helmet off. */
    internal static GuardianLoadout Pick(GuardianLoadout loadout, int slot, uint? item) => item switch
    {
        null when slot == 0 => loadout with { ShowHelmet = false },
        null => throw new ArgumentException("Only the helmet slot offers None.", nameof(item)),
        { } tag when slot == 0 => loadout.WithArmor(slot, tag) with { ShowHelmet = true },
        { } tag => loadout.WithArmor(slot, tag)
    };

    private static void DrawToolbar(StudioPageContext context, uint body, GuardianLoadout loadout, Action<GuardianLoadout> change)
    {
        var workspace = context.Workspace;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7);
        if (UiAuto.BeginCombo("##guardian-class", GuardianClasses.ForBody(body)?.Name ?? "Class"))
        {
            foreach (var item in GuardianClasses.All)
                if (UiAuto.Selectable(item.Name, item.BodyTag == body) && item.BodyTag != body)
                    workspace.Run(() => workspace.OpenGuardian(item.BodyTag, loadout));
            UiAuto.EndCombo();
        }
        StudioUi.Tip("Classes share one body; armor shows the class.");
        ImGui.SameLine();
        if (ImGuiIcons.Button("guardian-helmet", loadout.ShowHelmet ? StudioIcon.Eye : StudioIcon.EyeOff,
                "Show helmet. " + StudioUi.MayBeInaccurate, loadout.ShowHelmet))
            change(loadout with { ShowHelmet = !loadout.ShowHelmet });
        ImGui.SameLine();
        if (StudioUi.IconButton("guardian-export", StudioIcon.Export, "Export to Blender", context.ExportFullAsset is not null))
            context.ExportFullAsset!();
    }

    private static void DrawBody(View current, Action<GuardianLoadout> change)
    {
        StudioUi.Heading("Body");
        var (tables, row, loadout) = (current.Tables, current.Row, current.Loadout);
        int race = row.Race, gender = row.Gender;
        string unnamed = $"Row {row.Row}";
        // An unnamed row has no known gender; a race pick opens that race's male row.
        int keepGender = Math.Max(gender, 0);
        if (Combo("Race", race, GuardianAppearance.RaceNames.Count, index => GuardianAppearance.RaceNames[index], out int pickedRace,
                tip: index => index != 0 ? StudioUi.MayBeInaccurate : "", usable: index => RowFor(tables, index, keepGender) >= 0, blank: unnamed)
            && RowFor(tables, pickedRace, keepGender) is >= 0 and var raceRow)
            change(loadout with { RaceGender = raceRow });
        if (race != 0) StudioUi.Tip(StudioUi.MayBeInaccurate);
        if (Combo("Gender", gender, GuardianAppearance.GenderNames.Count, index => GuardianAppearance.GenderNames[index],
                out int pickedGender, tip: index => index != 0 ? StudioUi.MayBeInaccurate : "", usable: index => RowFor(tables, race, index) >= 0,
                blank: unnamed)
            && RowFor(tables, race, pickedGender) is >= 0 and var genderRow)
            change(loadout with { RaceGender = genderRow });
        if (gender != 0) StudioUi.Tip(StudioUi.MayBeInaccurate);
        if (row.Presets.Count != 0 && Combo("Face", loadout.Preset, row.Presets.Count, index => $"Face {index + 1}", out int face))
            change(loadout with { Preset = face });
    }

    /** Hair, markings and colours only tint head parts, so a drawn helmet disables them. */
    private static void DrawHead(View current, Action<GuardianLoadout> change)
    {
        if (!Section("Head", "Part names are assumed.", ImGuiTreeNodeFlags.DefaultOpen)) return;
        if (current.HeadHidden) ImGui.TextDisabled("Hidden under the helmet");
        var (row, loadout) = (current.Row, current.Loadout);
        if (row.Presets.Count == 0) return;
        var counts = row.Presets[loadout.Preset].VariantCounts;
        ImGui.BeginDisabled(current.HeadHidden);
        if (counts.GetValueOrDefault(GuardianAppearance.HairSelector) is > 0 and var hair
            && Combo("Hair", loadout.Variant2, hair, index => $"Hair {index + 1}", out int pickedHair))
            change(loadout with { Variant2 = pickedHair });
        // Entry 0 of the marking group is empty.
        if (counts.GetValueOrDefault(GuardianAppearance.MarkingSelector) is > 0 and var markings
            && Combo("Markings", loadout.Variant4, markings, index => index == 0 ? "None" : $"Markings {index}", out int pickedMarking))
            change(loadout with { Variant4 = pickedMarking });
        ImGui.EndDisabled();
    }

    private static void DrawColours(View current, Action<GuardianLoadout> change)
    {
        if (!Section("Colours", "Which part each colour tints is assumed.")) return;
        var (row, loadout) = (current.Row, current.Loadout);
        int[] positions = loadout.DyePositions;
        ImGui.BeginDisabled(current.HeadHidden);
        for (int list = 0; list < row.Dyes.Count && list < positions.Length; list++)
        {
            var choices = row.Dyes[list].Choices;
            if (choices.Count != 0 && Combo(ColourLabels[list], positions[list], choices.Count, index => $"{index + 1}", out int picked,
                    tip: index => "Dyes " + string.Join(", ", choices[index])))
                change(loadout.WithDye(list, picked));
        }
        ImGui.EndDisabled();
    }

    /** The five armor slots, then the weapon as a sixth row. */
    private void DrawGear(StudioWorkspace workspace, View current, Action<GuardianLoadout> change)
    {
        StudioUi.Heading("Armor and weapon");
        var loadout = current.Loadout;
        var rows = workspace.CategoryRows.GetValueOrDefault(InvestmentItemKinds.Armor) ?? [];
        if (!ReferenceEquals(rows, armorSource))
        {
            armorSource = rows;
            armorBySlot = rows.GroupBy(row => row.Kind).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        }
        for (int slot = 0; slot < ArmorLabels.Length; slot++)
        {
            string label = ArmorLabels[slot];
            uint? item = slot == 0 && !loadout.ShowHelmet ? null : loadout.Armor(slot);
            if (PickItem(workspace, label, armorBySlot.GetValueOrDefault(ArmorKinds[slot]) ?? [], LeadingRows(slot),
                    current.Placeholders[slot], item, out uint? picked, ArmorNote,
                    item is not null && current.Notes.TryGetValue(label, out var found) ? found : null))
                change(Pick(loadout, slot, picked));
        }
        var weapons = workspace.CategoryRows.GetValueOrDefault(InvestmentItemKinds.Weapons) ?? [];
        if (PickItem(workspace, "Weapon", weapons, LeadingRows(ArmorLabels.Length), 0, loadout.Weapon, out uint? weapon, WeaponNote,
                loadout.Weapon != 0 && current.Notes.TryGetValue("Weapon", out var weaponNotes) ? weaponNotes : null, WeaponWip))
            change(loadout with { Weapon = weapon ?? 0 });
    }

    /** A collapsing section with tip on its header. */
    private static bool Section(string label, string tip, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
    {
        bool open = ImGui.CollapsingHeader(label, flags);
        StudioUi.Tip(tip);
        return open;
    }

    /** The row named for this race and gender; -1 when no row carries the name. */
    private static int RowFor(GuardianAppearance tables, int race, int gender) =>
        race < 0 || gender < 0 ? -1 : tables.Rows.FirstOrDefault(row => row.Race == race && row.Gender == gender)?.Row ?? -1;

    /** Picker width that leaves room for an Open button and one tag, so every row ends at the same x. */
    private static float PickerWidth() => Math.Max(ImGui.GetFontSize() * 8,
        ImGui.GetContentRegionAvail().X - 2 * (ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X));

    /**
     * A labelled combo over count numbered choices.
     * @param tip The tooltip of a choice in the list; empty for none.
     * @param usable False for a choice that shows disabled.
     * @param blank Shown when current is not a choice.
     * @return True when the user picked a different choice.
     */
    private static bool Combo(string label, int current, int count, Func<int, string> name, out int picked,
        Func<int, string>? tip = null, Func<int, bool>? usable = null, string blank = "")
    {
        picked = current;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.SameLine(ImGui.GetFontSize() * 6);
        ImGui.SetNextItemWidth(PickerWidth());
        if (!UiAuto.BeginCombo("##" + label, (uint)current < (uint)count ? name(current) : blank)) return false;
        for (int index = 0; index < count; index++)
        {
            ImGui.PushID(index);
            var flags = usable?.Invoke(index) == false ? ImGuiSelectableFlags.Disabled : ImGuiSelectableFlags.None;
            if (UiAuto.Selectable(name(index), index == current, flags)) picked = index;
            if (tip is not null) StudioUi.Tip(tip(index));
            ImGui.PopID();
        }
        UiAuto.EndCombo();
        return picked != current;
    }

    /**
     * A searchable item combo with its leading rows first and an Open button; labels are read once per row array.
     * @param placeholder The item the leading 0 row stands for; Open opens it. 0 for none.
     * @param current The chosen item; 0 is the leading 0 row and null is None.
     * @param note The slot's notes, shown as a warning mark after Open; null for none.
     * @param wip What is known broken in this slot, shown as a WIP tag after the label; empty for none.
     */
    private unsafe bool PickItem(StudioWorkspace workspace, string label, BrowserAsset[] rows, IReadOnlyList<LeadingRow> leading,
        uint placeholder, uint? current, out uint? picked, string labelTip = "", string? note = null, string wip = "")
    {
        picked = current;
        if (!pickers.TryGetValue(label, out var picker)) pickers.Add(label, picker = new());
        ImGui.PushID(label);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        StudioUi.Tip(labelTip);
        if (wip.Length != 0) StudioUi.Wip(wip);
        ImGui.SameLine(ImGui.GetFontSize() * 6);
        ImGui.SetNextItemWidth(PickerWidth());
        string preview = current is { } chosen and not 0 ? LabelOf(workspace, chosen)
            : leading.FirstOrDefault(row => row.Value == current)?.Label ?? "None";
        string placeholderTip = placeholder != 0 ? $"Class default: {LabelOf(workspace, placeholder)}. {PlaceholderNote}" : "";
        if (UiAuto.BeginCombo("##item", preview, ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
            ImGui.SetNextItemWidth(-1);
            UiAuto.InputTextWithHint("##search", "Search", ref picker.Search, 128);
            picker.Refresh(rows);
            foreach (var row in leading)
            {
                if (UiAuto.Selectable(row.Label, row.Value == current)) picked = row.Value;
                if (row.Value != 0 || placeholder == 0) continue;
                StudioUi.Tip(placeholderTip);
            }
            float right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
            var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
            clipper.Begin(picker.Hits.Length);
            while (clipper.Step())
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    int at = picker.Hits[i];
                    uint tag = rows[at].Entry.TagHash;
                    if (UiAuto.Selectable(picker.Ids[at], tag == current)) picked = tag;
                    ImGui.SameLine(right - ImGui.CalcTextSize(picker.Tags[at]).X);
                    ImGui.TextDisabled(picker.Tags[at]);
                }
            clipper.End(); clipper.Destroy();
            if (picker.Hits.Length == 0) ImGui.TextDisabled("No matches");
            UiAuto.EndCombo();
        }
        ImGui.SameLine();
        uint open = current == 0 ? placeholder : current ?? 0;
        if (StudioUi.IconButton("open", StudioIcon.Open, "Open item", open != 0 && !workspace.IsBusy))
            workspace.Run(() => workspace.Select(open));
        if (note is not null)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(StudioTheme.Accent, "(!)");
            StudioUi.Tip(note);
        }
        ImGui.PopID();
        return picked != current;
    }

    private string LabelOf(StudioWorkspace workspace, uint tag)
    {
        if (!itemLabels.TryGetValue(tag, out string? label)) itemLabels.Add(tag, label = workspace.DisplayName(tag));
        return label;
    }

    /** Search state of one picker; labels, ids, tags and hits follow the row array they were built from. */
    private sealed class ItemPicker
    {
        public string Search = "";
        public string[] Labels = [];
        public string[] Ids = [];
        public string[] Tags = [];
        public int[] Hits = [];
        private string searched = "\0";
        private BrowserAsset[]? source;

        public void Invalidate() => source = null;

        public void Refresh(BrowserAsset[] rows)
        {
            if (!ReferenceEquals(rows, source))
            {
                source = rows;
                Labels = [.. rows.Select(row => row.Name)];
                Tags = [.. rows.Select(row => row.Entry.TagHash.ToString("X8"))];
                Ids = [.. Labels.Zip(Tags, (name, tag) => $"{name}##{tag}")];
                searched = "\0";
            }
            if (Search == searched) return;
            searched = Search;
            string[] words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Hits = [.. Enumerable.Range(0, rows.Length).Where(index => words.All(word =>
                Labels[index].Contains(word, StringComparison.OrdinalIgnoreCase)
                || Tags[index].Contains(word, StringComparison.OrdinalIgnoreCase)))];
        }
    }
}
