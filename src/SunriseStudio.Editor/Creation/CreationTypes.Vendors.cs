namespace SunriseStudio.Editor.Creation;

/** CreationTypes part: registers vendor types; New is blocked, Clone works. */
public static partial class CreationTypes
{
    static partial void AddVendorTypes()
    {
        Add(StudioCategories.VendorsName, CreationMode.New, new("Vendor", "", "No NPC writer. Use Clone."));
        Add(StudioCategories.VendorsName, CreationMode.Clone, new("Vendor", "A new vendor from an installed one. May not work in game.", "",
            workspace => new VendorWorkflow(workspace)));
    }
}
