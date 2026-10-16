using System.Globalization;
using ImGuiNET;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** Solforge Phase 16: base tag values next to the project's authored values, with revert to base. */
public sealed partial class StudioForm
{
    private ProjectTagDiff? tagDiff;
    private string tagDiffError = "";
    private bool openTagDiff;

    private void BeginTagDiff(uint tag)
    {
        tagDiff = null;
        tagDiffError = "";
        openTagDiff = true;
        if (workspace.Project is not { } project || workspace.Catalog is not { } catalog)
        {
            tagDiffError = "Open the installation to compare with base.";
            return;
        }
        try { tagDiff = ProjectTagDiff.Compare(project, tag, catalog); }
        catch (Exception error) when (error is IOException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        { tagDiffError = error.Message; }
    }

    private unsafe void DrawTagDiff()
    {
        if (openTagDiff) { ImGui.OpenPopup("Compare with base"); openTagDiff = false; }
        if (!ImGui.BeginPopupModal("Compare with base", ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (tagDiff is { } diff)
        {
            ImGui.TextUnformatted($"[{diff.TagHash:X8}]  base {diff.Base.Length} bytes, authored {diff.Authored.Length} bytes");
            ImGui.TextDisabled($"{diff.ChangedBytes} bytes in {diff.ChangedWords} words differ{(diff.Truncated ? $"; first {diff.Words.Count} shown" : "")}");
            if (ImGui.BeginChild("Tag diff rows", new(620 * scale, 320 * scale), ImGuiChildFlags.Borders)
                && ImGui.BeginTable("tag-diff", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY))
            {
                ImGui.TableSetupColumn("Offset");
                ImGui.TableSetupColumn("Base");
                ImGui.TableSetupColumn("Authored");
                ImGui.TableSetupColumn("Base float");
                ImGui.TableSetupColumn("Authored float");
                ImGui.TableHeadersRow();
                var words = diff.Words;
                var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                try
                {
                    clipper.Begin(words.Count);
                    while (clipper.Step())
                        for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                        {
                            var word = words[i];
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn(); ImGui.TextUnformatted(word.Offset.ToString("X6", CultureInfo.InvariantCulture));
                            ImGui.TableNextColumn(); ImGui.TextUnformatted(Hex(word.Base));
                            ImGui.TableNextColumn(); ImGui.TextUnformatted(Hex(word.Authored));
                            ImGui.TableNextColumn(); ImGui.TextDisabled(Real(word.BaseFloat));
                            ImGui.TableNextColumn(); ImGui.TextUnformatted(Real(word.AuthoredFloat));
                        }
                }
                finally { clipper.End(); clipper.Destroy(); }
                ImGui.EndTable();
            }
            ImGui.EndChild();
            ImGui.BeginDisabled(!workspace.CanEdit || workspace.Project?.ContainsEdit(diff.TagHash) != true);
            if (UiAuto.Button("Revert to base"))
            {
                uint tag = diff.TagHash;
                workspace.Run(() => workspace.Revert(tag));
                tagDiff = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
        }
        else if (tagDiffError.Length > 0) ImGui.TextWrapped(tagDiffError);
        if (UiAuto.Button("Close")) { tagDiff = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    private static string Hex(uint? value) => value is uint word ? word.ToString("X8", CultureInfo.InvariantCulture) : "-";

    private static string Real(float? value) => value is float number && float.IsFinite(number)
        ? number.ToString("G7", CultureInfo.InvariantCulture) : "-";
}
