using SunriseStudio.Packages;

namespace SunriseStudio.Editor.Creation;

/** CreationTypes part: registers the exotic weapon type. */
public static partial class CreationTypes
{
    static partial void AddExoticTypes() =>
        Add(InvestmentItemKinds.Weapons, CreationMode.New, new("Exotic weapon",
            "A new exotic auto rifle from a Blender export.", "", workspace => new ExoticWeaponWorkflow(workspace)));
}
