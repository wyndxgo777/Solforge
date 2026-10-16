using System.Numerics;
using ImGuiNET;
using SunriseStudio.Authoring;

namespace SunriseStudio.Editor.Creation;

/** Picker widgets shared by the dialog and by workflows that draw their own rows. */
public static class CreationWidgets
{
    private sealed class PickerState
    {
        public string Search = "", Searched = "\0";
        public int Revision = -1;
        public CreationChoice[] Results = [];
        public string Error = "";
    }

    private sealed class RowPickerState
    {
        public string Search = "", Searched = "\0";
        public int Revision = -1;
        public double Edited;
        /** The row array last filtered; a new array refilters. */
        public IReadOnlyList<BrowserAsset>? Rows;
        public BrowserAsset[] Results = [];
        public Task<BrowserAsset[]>? Work;
        public CancellationTokenSource? Cancel;
        public string Error = "";
    }

    /** A picker lists at most this many matches; searching narrows the rest. */
    private const int PickerRows = 200;
    /** Seconds a row search waits for typing to stop. */
    private const double SearchSettle = 0.2;
    private static readonly Dictionary<uint, PickerState> Pickers = [];
    private static readonly Dictionary<uint, RowPickerState> RowPickers = [];
    public static float Scale { get; set; } = 1;

    /** Forgets search text and cached results, so a new workflow starts clean. */
    public static void Reset()
    {
        Pickers.Clear();
        foreach (var state in RowPickers.Values) { state.Cancel?.Cancel(); state.Cancel?.Dispose(); }
        RowPickers.Clear();
    }

    /**
     * A searchable list of candidates.
     * @param id ImGui identity of this picker; state is kept per full ID stack.
     * @param revision Data revision; a change reruns the search.
     * @param empty What an empty list says, such as why its data is missing; null tells no data from no matches.
     * @return True when the user chose a row.
     */
    public static bool Picker(string id, ref uint? value, ref string label, Func<string, IEnumerable<CreationChoice>> search,
        int revision, float height = 130, string? empty = null)
    {
        // Keyed by the full ImGui ID stack, so pickers that share an id under different parents keep their own search.
        uint key = ImGui.GetID(id);
        if (!Pickers.TryGetValue(key, out var state)) Pickers.Add(key, state = new());
        bool changed = false;
        ImGui.PushID(id);
        ImGui.TextDisabled(value is null ? "None selected" : label);
        if (value is not null)
        {
            ImGui.SameLine();
            if (UiAuto.SmallButton("Clear")) { value = null; label = ""; changed = true; }
        }
        ImGui.SetNextItemWidth(-1);
        UiAuto.InputTextWithHint("##search", "Search", ref state.Search, 256);
        if (state.Search != state.Searched || state.Revision != revision)
        {
            state.Searched = state.Search; state.Revision = revision;
            state.Error = "";
            try { state.Results = search(state.Search.Trim()).Take(PickerRows).ToArray(); }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
            { state.Results = []; state.Error = "Search failed: " + error.Message; }
        }
        ImGui.BeginChild("##rows", new Vector2(0, height * Scale), ImGuiChildFlags.Borders);
        foreach (var choice in state.Results)
        {
            ImGui.PushID(unchecked((int)choice.Value));
            if (UiAuto.Selectable(choice.Label.Length == 0 ? $"{choice.Value:X8}" : choice.Label, value == choice.Value))
            { value = choice.Value; label = choice.Label; changed = true; }
            StudioUi.Tip(choice.Detail);
            ImGui.PopID();
        }
        if (state.Error.Length != 0)
        {
            ImGui.TextWrapped(state.Error);
            if (UiAuto.SmallButton("Retry search")) state.Revision = -1;
        }
        else if (state.Results.Length == 0) ImGui.TextDisabled(Empty(empty, state.Search));
        else if (state.Results.Length == PickerRows) ImGui.TextDisabled("Search to see more");
        ImGui.EndChild();
        ImGui.PopID();
        return changed;
    }

    /**
     * A searchable list over a cached row array; a search or a large array filters on a worker.
     * @param filter Keeps the rows that fit; it must not read packages.
     * @param revision Data revision; a change refilters at once.
     * @param isProject True for a project row; called only for drawn rows. Other rows show their tag.
     * @param clearable False hides Clear, for a value that cannot be empty.
     * @param empty What an empty list says, such as why its data is missing; null tells no data from no matches.
     * @return True when the user chose a row.
     */
    public static unsafe bool RowPicker(string id, ref uint? value, ref string label, IReadOnlyList<BrowserAsset> rows,
        Func<BrowserAsset, bool>? filter, int revision, Func<uint, bool> isProject, float height = 130, bool clearable = true,
        string? empty = null)
    {
        uint key = ImGui.GetID(id);
        if (!RowPickers.TryGetValue(key, out var state)) RowPickers.Add(key, state = new());
        bool changed = false;
        ImGui.PushID(id);
        ImGui.TextDisabled(value is null ? "None selected" : label);
        if (value is not null && clearable)
        {
            ImGui.SameLine();
            if (UiAuto.SmallButton("Clear")) { value = null; label = ""; changed = true; }
        }
        ImGui.SetNextItemWidth(-1);
        if (UiAuto.InputTextWithHint("##search", "Search", ref state.Search, 256)) state.Edited = ImGui.GetTime();
        TakeRows(state);
        if (state.Revision != revision || !ReferenceEquals(state.Rows, rows)
            || state.Search != state.Searched && ImGui.GetTime() - state.Edited > SearchSettle)
        {
            state.Searched = state.Search; state.Revision = revision; state.Rows = rows; state.Error = "";
            state.Cancel?.Cancel();
            state.Cancel?.Dispose();
            state.Cancel = new();
            (var inline, state.Work) = BrowserRowFilter.Start(rows, BrowserRowFilter.Words(state.Search), filter, state.Cancel.Token);
            if (inline is not null) state.Results = inline;
        }
        float line = ImGui.GetTextLineHeight();
        ImGui.BeginChild("##rows", new Vector2(0, height * Scale), ImGuiChildFlags.Borders);
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(state.Results.Length, line + ImGui.GetStyle().ItemSpacing.Y);
        while (clipper.Step())
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var row = state.Results[i];
                uint tag = row.Entry.TagHash;
                // The tag tells rows with the same name apart.
                string name = row.Name, detail = isProject(tag) ? "Project" : $"{tag:X8}";
                ImGui.PushID(i);
                var start = ImGui.GetCursorScreenPos();
                float width = ImGui.GetContentRegionAvail().X, detailWidth = ImGui.CalcTextSize(detail).X;
                if (UiAuto.Selectable("##row", value == tag, ImGuiSelectableFlags.None, new(0, line)))
                { value = tag; label = name; changed = true; }
                StudioUi.Tip($"{name}\n{row.Entry.PackageName}  {tag:X8}");
                var draw = ImGui.GetWindowDrawList();
                draw.PushClipRect(start, start + new Vector2(width - detailWidth - 8 * Scale, line), true);
                draw.AddText(start, ImGui.GetColorU32(ImGuiCol.Text), name);
                draw.PopClipRect();
                draw.AddText(new(start.X + width - detailWidth, start.Y), ImGui.GetColorU32(ImGuiCol.TextDisabled), detail);
                ImGui.PopID();
            }
        clipper.End(); clipper.Destroy();
        if (state.Error.Length != 0) ImGui.TextWrapped(state.Error);
        else if (state.Work is not null) ImGui.TextDisabled("Searching");
        else if (state.Results.Length == 0) ImGui.TextDisabled(Empty(empty, state.Search));
        ImGui.EndChild();
        ImGui.PopID();
        return changed;
    }

    private static string Empty(string? reason, string search) =>
        reason ?? (search.Trim().Length == 0 ? "None available" : "No matches");

    /** Adopts a finished worker filter; a cancelled one is dropped. */
    private static void TakeRows(RowPickerState state)
    {
        if (state.Work is not { IsCompleted: true } work) return;
        state.Work = null;
        try { state.Results = work.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { state.Results = []; state.Error = "Search failed: " + error.Message; }
    }

    /** A combo over fixed choices; returns true when the value changed. */
    public static bool Choice(string id, ref uint value, IReadOnlyList<CreationChoice> choices, float width = 260)
    {
        uint current = value;
        var selected = choices.FirstOrDefault(choice => choice.Value == current);
        bool changed = false;
        ImGui.SetNextItemWidth(width * Scale);
        if (UiAuto.BeginCombo(id, selected?.Label ?? $"{value}"))
        {
            foreach (var choice in choices)
            {
                ImGui.PushID(unchecked((int)choice.Value));
                if (UiAuto.Selectable(choice.Label, choice.Value == value)) { value = choice.Value; changed = true; }
                StudioUi.Tip(choice.Detail);
                ImGui.PopID();
            }
            UiAuto.EndCombo();
        }
        return changed;
    }

    /** The field label; its hint shows on hover. */
    public static void Label(CreationField field)
    {
        ImGui.TextUnformatted(field.Required ? field.Label + " *" : field.Label);
        StudioUi.Tip(field.Hint);
    }

    /**
     * Import surfaces as a name and status table under a count line; the detail shows on hover.
     * @param skipped Surface that is read but not written; it gets a WIP tag.
     */
    public static void Surfaces(string id, IReadOnlyList<OriginalAssetSurface> surfaces, string skipped = "")
    {
        int ready = surfaces.Count(surface => surface.Present && surface.Supported && surface.Name != skipped);
        int unsupported = surfaces.Count(surface => surface.Present && !surface.Supported);
        ImGui.TextDisabled($"{ready} ready, {unsupported} unsupported");
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("##name", ImGuiTableColumnFlags.WidthStretch, .4f);
        ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch, .6f);
        foreach (var surface in surfaces)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextDisabled(surface.Name);
            StudioUi.Tip(surface.Detail);
            ImGui.TableNextColumn();
            if (!surface.Present) ImGui.TextDisabled("Absent");
            else if (!surface.Supported) ImGui.TextColored(StudioTheme.Accent, "Unsupported");
            else if (skipped.Length != 0 && surface.Name == skipped) ImGui.TextDisabled("Skipped");
            else ImGui.TextUnformatted("Ready");
            StudioUi.Tip(surface.Detail);
            if (skipped.Length != 0 && surface.Name == skipped) StudioUi.Wip(StudioUi.NotShown);
        }
        ImGui.EndTable();
    }
}
