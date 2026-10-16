namespace SunriseStudio.Editor.Creation;

/** CreationTypes part: registers enemy and other entity types; all are blocked because nothing can write them yet. */
public static partial class CreationTypes
{
    public const string GuardianBlocker = "Guardians need character data Studio cannot write.";

    /** Nothing writes a new or copied entity that the game spawns, so every action here stays blocked. */
    static partial void AddEntityTypes()
    {
        Add(StudioCategories.EnemiesCategory, CreationMode.New,
            new("Enemy", "", "No AI, health or loot field is known."));
        Add(StudioCategories.EnemiesCategory, CreationMode.Clone, new("Enemy copy", "", CombatantBlocker));
        Add(StudioCategories.EntityCategory, CreationMode.New,
            new("Entity", "", "Use Import skinned model (Hob rig)."));
        Add(StudioCategories.EntityCategory, CreationMode.Clone,
            new("Entity copy", "", "Nothing spawns or places a copy yet."));
        Add(StudioCategories.GuardiansName, CreationMode.New, new("Guardian", "", GuardianBlocker));
        Add(StudioCategories.GuardiansName, CreationMode.Clone, new("Guardian copy", "", GuardianBlocker));
    }
}
