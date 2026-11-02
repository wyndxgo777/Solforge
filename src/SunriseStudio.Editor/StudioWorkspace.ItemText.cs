/**
 * File: StudioWorkspace partial: sets an item's display name, description or type text (the MCP apply_item_edit set_text
 * op). Works on the display record of any item, weapon or plug. Bank additions and edits and the display edit commit as
 * one undo step.
 */
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** Item text edits (part of StudioWorkspace). */
public sealed partial class StudioWorkspace
{
    /** The display text fields set_text accepts. */
    public static readonly string[] ItemTextFields = ["name", "description", "type"];

    /** The display record the item shows its text from, or null when the item or record is unknown. */
    public uint? ItemDisplayTag(uint item) =>
        Investment?.FindItem(item) is { DisplayTag: not 0 } found ? found.DisplayTag : null;

    /**
     * Plans the bank string off-thread and commits it with the display edit as one undoable transaction.
     * @param field name, description or type
     * @param sources Where the item and string bank tables live; null for the installed ones.
     */
    public void EditItemText(uint item, string field, string text, ushort package, InvestmentSources? sources = null)
    {
        RequireEditable();
        int offset = field switch
        {
            "name" => InvestmentItemClone.DisplayName,
            "description" => InvestmentItemClone.DisplayFlavor,
            "type" => InvestmentItemClone.DisplayType,
            _ => throw new ArgumentException("field must be name, description or type.", nameof(field))
        };
        text = text.Trim();
        if (text.Length == 0 || text.Length > 4000 || field != "description" && text.Length > 200)
            throw new InvalidDataException("Give 1 to 200 characters (4000 for a description).");
        uint displayTag = ItemDisplayTag(item) ?? throw new InvalidDataException("The item has no display record.");
        var banks = sources ?? new InvestmentSources();
        CommitPlan("Saving item text", (project, catalog) =>
        {
            using var plan = new ExoticWeaponAuthoring.PlanCatalog(catalog, project, package);
            byte[] display = plan.Catalog.Read(displayTag);
            var (bank, _) = InvestmentItemClone.ReadDisplayString(display, InvestmentItemClone.DisplayName);
            var rows = InvestmentItemClone.ReadBankRows(plan.Catalog.Read(banks.StringBanks));
            var template = bank < rows.Count ? new[] { rows[(int)bank].ContainerTag } : [];
            var planned = ProjectStringBank.Plan(plan.Catalog, project, package, plan.NextEntry,
                [new StringBankText(StringBankWriter.Hash(text), text)], template, banks);
            plan.Add(planned.Additions); plan.Edit(planned.Edits);
            display = InvestmentItemClone.SetDisplayString(display, offset, checked((uint)planned.BankIndex), StringBankWriter.Hash(text));
            plan.Edit(new Dictionary<uint, byte[]> { [displayTag] = display });
            return new PreparedItemText(field, plan.Assets.ToArray(), plan.Changes.ToDictionary());
        }, plan => plan.Assets, plan => plan.Changes, plan => $"Item {field} saved");
    }
}

/** A planned item text edit: the bank additions and edits plus the display edit. */
internal sealed record PreparedItemText(string Field, IReadOnlyList<ProjectAssetAddition> Assets,
    IReadOnlyDictionary<uint, byte[]> Changes);
