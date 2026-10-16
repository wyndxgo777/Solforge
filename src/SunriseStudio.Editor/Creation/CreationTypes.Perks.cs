namespace SunriseStudio.Editor.Creation;

/** CreationTypes part: registers perk behavior new and clone types. */
public static partial class CreationTypes
{
    static partial void AddPerkTypes()
    {
        Add(StudioCategories.PerksName, CreationMode.New,
            new("Perk behavior", "A blank perk program. No item uses it yet. May not work in game.", "",
                workspace => new PerkBehaviorWorkflow(workspace)));
        Add(StudioCategories.PerksName, CreationMode.Clone,
            new("Clone perk behavior", "A copy in a new entry. No item uses it yet. May not work in game.", "",
                workspace => new PerkBehaviorWorkflow(workspace, copy: true)) { ClonesProject = true });
    }
}
