using System.Globalization;
using ImGuiNET;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** A failed save keeps its draft and refuses a changed project or target identity. */
public sealed class WideHashAliasDraft
{
    private readonly StudioProject project;
    private readonly AddedProjectAsset baseline;
    private readonly SortedSet<ulong> values;
    private IReadOnlyList<ulong>? snapshot;
    public string AssetId => baseline.Id;
    public string AssetName => baseline.Name;
    public uint Tag => baseline.Native!.Tag();
    public IReadOnlyList<ulong> Values => snapshot ??= Array.AsReadOnly(values.ToArray());

    public WideHashAliasDraft(StudioProject project, string id)
    {
        this.project = project;
        baseline = project.AddedAssets.FirstOrDefault(value => value.Id == id)
            ?? throw new KeyNotFoundException("Added asset is not in the project.");
        if (baseline.Native is null) throw new InvalidOperationException("Wide hashes need a native package asset.");
        values = new(baseline.Native.WideHashes ?? WideHashAliases.Empty);
    }

    /** Adds a 16-digit hex wide hash to the draft; throws on bad or duplicate input. */
    public void Add(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (text.Length != 16 || !ulong.TryParse(text, NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture, out ulong value))
            throw new InvalidDataException("Enter exactly 16 hexadecimal digits.");
        if (!values.Add(value)) throw new InvalidDataException("That wide hash is already in this draft.");
        snapshot = null;
    }

    /** Removes a wide hash from the draft. */
    public void Remove(ulong value) { if (values.Remove(value)) snapshot = null; }

    /** Saves the aliases to the added asset; refuses when the project or asset changed. */
    public void Save(StudioWorkspace workspace)
    {
        if (!ReferenceEquals(workspace.Project, project)
            || project.AddedAssets.FirstOrDefault(value => value.Id == AssetId) != baseline)
            throw new InvalidOperationException("The project or asset changed. Reopen the alias editor before saving.");
        workspace.SetAddedWideHashAliases(AssetId, values);
    }
}

/** StudioForm part: the wide hash alias editor dialog. */
public sealed partial class StudioForm
{
    private WideHashAliasDraft? wideHashDraft;
    private string wideHashText = "", wideHashError = "";
    private bool openWideHashes;

    /** Hex input field for one wide hash. */
    internal static bool WideHashInput(ref string value) => UiAuto.InputText("Wide hash", ref value, 19);

    private void BeginWideHashes(string id)
    {
        if (workspace.Project is not { } project) return;
        wideHashDraft = new(project, id);
        wideHashText = wideHashError = "";
        openWideHashes = true;
    }

    /** The draft closes only after a successful save or an explicit cancel. */
    private unsafe void DrawWideHashes()
    {
        if (openWideHashes) { ImGui.OpenPopup("Wide-hash aliases"); openWideHashes = false; }
        if (!ImGui.BeginPopupModal("Wide-hash aliases", ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (wideHashDraft is { } draft)
        {
            ImGui.TextUnformatted($"{draft.AssetName}  [{draft.Tag:X8}]");
            ImGui.TextDisabled("64-bit reference keys");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Exact keys only; names are not converted.");
            ImGui.SetNextItemWidth(360 * scale);
            WideHashInput(ref wideHashText);
            ImGui.SameLine();
            if (UiAuto.Button("Add"))
            {
                try { draft.Add(wideHashText); wideHashText = wideHashError = ""; }
                catch (Exception error) when (error is InvalidDataException or ArgumentException) { wideHashError = error.Message; }
            }
            if (ImGui.BeginChild("Alias list", new(450 * scale, 200 * scale), ImGuiChildFlags.Borders))
            {
                var values = draft.Values;
                var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                try
                {
                    clipper.Begin(values.Count);
                    while (clipper.Step())
                        for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                        {
                            ulong value = values[i];
                            ImGui.PushID(value.ToString("X16", CultureInfo.InvariantCulture));
                            ImGui.TextUnformatted(value.ToString("X16", CultureInfo.InvariantCulture)); ImGui.SameLine();
                            if (UiAuto.SmallButton("Remove")) draft.Remove(value);
                            ImGui.PopID();
                        }
                }
                finally { clipper.End(); clipper.Destroy(); }
                if (values.Count == 0) ImGui.TextDisabled("No authored aliases");
            }
            ImGui.EndChild();
            if (wideHashError.Length > 0) ImGui.TextWrapped(wideHashError);
            ImGui.BeginDisabled(!workspace.CanEdit);
            if (UiAuto.Button("Save aliases"))
            {
                try { draft.Save(workspace); wideHashDraft = null; ImGui.CloseCurrentPopup(); }
                catch (Exception error) when (error is InvalidDataException or InvalidOperationException
                    or IOException or UnauthorizedAccessException or KeyNotFoundException) { wideHashError = error.Message; }
            }
            ImGui.EndDisabled(); ImGui.SameLine();
        }
        if (UiAuto.Button("Cancel")) { wideHashDraft = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }
}
