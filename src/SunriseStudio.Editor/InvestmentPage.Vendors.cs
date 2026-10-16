using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** InvestmentPage part: edits the sale and cost rows of a vendor. */
public sealed partial class InvestmentPage
{
    private InvestmentSale? costSale;
    private InvestmentCost? costRow;
    private StudioDocument? costDocument;
    private StudioProject? costProject;
    private long costOverlayRevision;
    private bool openCostEditor;
    private int costItem = -1;
    private string costQuantity = "", costSearch = "", costMatchedSearch = "";
    private InvestmentCatalog? costCatalog;
    private InvestmentItem[] costMatches = [];

    private void InvalidateCostDraft(StudioWorkspace workspace)
    {
        if (costDocument is not null && (!ReferenceEquals(costDocument, workspace.Document)
            || !ReferenceEquals(costProject, workspace.Project) || costOverlayRevision != workspace.Catalog?.OverlayRevision))
            ClearCostDraft();
    }

    private void ClearCostDraft()
    { costDocument = null; costProject = null; costSale = null; costRow = null; openCostEditor = false; }

    private void DrawCostRow(StudioPageContext context, InvestmentSale sale, InvestmentCost cost)
    {
        if (UiAuto.SmallButton($"{cost.Quantity:N0} {cost.Name}##cost")) BeginCostEditor(context, sale, cost);
        if (cost.Conditional)
        {
            ImGui.SameLine(); ImGui.TextDisabled("Conditional");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Base quantity; native expressions remain attached");
        }
        ImGui.SameLine(); ImGui.BeginDisabled(!context.Workspace.CanEdit);
        if (CostAction("duplicate", true, "Duplicate cost")) context.Workspace.Run(() => context.Workspace.DuplicateInvestmentCost(sale, cost));
        ImGui.SameLine();
        if (CostAction("remove", false, "Remove cost")) context.Workspace.Run(() => context.Workspace.RemoveInvestmentCost(sale, cost));
        ImGui.EndDisabled();
    }

    private static void DrawSaleActions(StudioPageContext context, InvestmentSale sale)
    {
        ImGui.BeginDisabled(!context.Workspace.CanEdit);
        if (CostAction("duplicate-sale", true, "Duplicate sale")) context.Workspace.Run(() => context.Workspace.DuplicateInvestmentSale(sale));
        ImGui.SameLine();
        if (CostAction("remove-sale", false, "Remove sale")) context.Workspace.Run(() => context.Workspace.RemoveInvestmentSale(sale));
        ImGui.EndDisabled();
    }

    /** Primitive icons keep these controls independent of an installed symbol font. */
    private static bool CostAction(string id, bool duplicate, string tooltip)
    {
        Vector2 point = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(id, new(22, 20));
        var draw = ImGui.GetWindowDrawList();
        if (ImGui.IsItemHovered()) draw.AddRectFilled(point, point + new Vector2(22, 20), ImGui.GetColorU32(ImGuiCol.ButtonHovered), 3);
        uint color = ImGui.GetColorU32(ImGuiCol.Text);
        if (duplicate)
        {
            draw.AddRect(point + new Vector2(5, 4), point + new Vector2(14, 12), color);
            draw.AddRect(point + new Vector2(8, 7), point + new Vector2(17, 15), color);
        }
        else draw.AddLine(point + new Vector2(6, 10), point + new Vector2(16, 10), color, 2);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(tooltip);
        return clicked;
    }

    private void BeginCostEditor(StudioPageContext context, InvestmentSale sale, InvestmentCost cost)
    {
        costSale = sale; costRow = cost; costItem = cost.ItemIndex;
        costQuantity = cost.Quantity.ToString(CultureInfo.InvariantCulture); costSearch = "";
        costCatalog = null; openCostEditor = true;
        costDocument = context.Workspace.Document; costProject = context.Workspace.Project;
        costOverlayRevision = context.Workspace.Catalog!.OverlayRevision;
    }

    /** Payment edits retain the selected sale identity and wait for explicit application. */
    private unsafe void DrawCostEditor(StudioPageContext context, InvestmentCatalog catalog)
    {
        if (openCostEditor) { ImGui.OpenPopup("Edit cost"); openCostEditor = false; }
        if (!ImGui.BeginPopupModal("Edit cost", ImGuiWindowFlags.AlwaysAutoResize)) return;
        InvalidateCostDraft(context.Workspace);
        if (costSale is not { } sale || costRow is not { } row)
        { ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }
        ImGui.SetNextItemWidth(380); UiAuto.InputTextWithHint("##cost-search", "Find item or currency", ref costSearch, 128);
        if (!ReferenceEquals(costCatalog, catalog) || costMatchedSearch != costSearch)
        {
            costCatalog = catalog; costMatchedSearch = costSearch;
            costMatches = catalog.Items.Where(item => costSearch.Length == 0
                || item.Name.Contains(costSearch, StringComparison.OrdinalIgnoreCase)
                || item.TypeName.Contains(costSearch, StringComparison.OrdinalIgnoreCase)
                || item.Index.ToString(CultureInfo.InvariantCulture) == costSearch)
                .OrderByDescending(item => item.Category == InvestmentCategory.Currencies).ThenBy(item => item.Name).ToArray();
        }
        ImGui.BeginChild("cost-items", new(380, 210), ImGuiChildFlags.Borders);
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(costMatches.Length);
        while (clipper.Step())
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var item = costMatches[i]; ImGui.PushID(item.Index);
                if (UiAuto.Selectable(item.Name, costItem == item.Index)) costItem = item.Index;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{item.Name}\n{item.TypeName}");
                ImGui.PopID();
            }
        clipper.End(); clipper.Destroy(); ImGui.EndChild();
        if ((uint)costItem < catalog.Items.Count) ImGui.TextUnformatted(catalog.Items[costItem].Name);
        ImGui.SetNextItemWidth(180); UiAuto.InputText(row.Conditional ? "Base quantity" : "Quantity", ref costQuantity, 10, ImGuiInputTextFlags.CharsDecimal);
        bool valid = (uint)costItem < catalog.Items.Count && uint.TryParse(costQuantity, NumberStyles.None, CultureInfo.InvariantCulture, out _);
        ImGui.BeginDisabled(!context.Workspace.CanEdit || !valid);
        if (UiAuto.Button("Save delta") && SaveCostDraft(context.Workspace)) ImGui.CloseCurrentPopup();
        ImGui.EndDisabled(); ImGui.SameLine();
        if (UiAuto.Button("Cancel")) { ClearCostDraft(); ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    /**
     * @param workspace Owns the selected document and project.
     * @return True after a successful save clears the draft.
     */
    private bool SaveCostDraft(StudioWorkspace workspace)
    {
        if (costSale is not { } sale || costRow is not { } row) return false;
        workspace.Run(() => workspace.EditInvestmentCost(sale, row, checked((ushort)costItem),
            uint.Parse(costQuantity, CultureInfo.InvariantCulture)));
        if (workspace.Error.Length != 0) return false;
        ClearCostDraft();
        return true;
    }
}
