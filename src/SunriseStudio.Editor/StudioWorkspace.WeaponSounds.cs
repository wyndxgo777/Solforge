using SunriseStudio.Audio;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** StudioWorkspace part: replaces a weapon's sounds. */
public sealed partial class StudioWorkspace
{
    /**
     * Each chosen sound event gets a new bank and event of the weapon's own. One undo step.
     * A NAME.release.wav beside NAME.wav is what the paired release event plays.
     */
    public void ReplaceWeaponSounds(uint weaponTag, IReadOnlyDictionary<uint, string> waves)
    {
        RequireEditable();
        var investment = Investment ?? throw new InvalidOperationException("The investment catalog is not loaded.");
        var weapon = investment.FindItem(weaponTag) ?? throw new InvalidDataException("The weapon is not in the investment index.");
        var project = Project!; var catalog = Catalog!;
        var (package, next) = WeaponEntryCursor(project, weaponTag);
        int revision = ProjectRevision; long overlay = catalog.OverlayRevision;
        BeginWork("Replacing sounds", () =>
        {
            var slots = WeaponSoundAuthoring.Find(project, catalog, weaponTag);
            var plan = WeaponSoundAuthoring.Prepare(project, catalog, weaponTag, slots,
                waves.ToDictionary(pair => pair.Key, pair => PcmWave.Read(pair.Value)), $"sunrise/{weapon.Hash:x8}",
                package, next, waves.Select(pair => (pair.Key, Path: Path.ChangeExtension(pair.Value, ".release.wav")))
                    .Where(pair => File.Exists(pair.Path)).ToDictionary(pair => pair.Key, pair => PcmWave.Read(pair.Path)));
            return () =>
            {
                RequireSameProjectState(project, catalog, revision, overlay);
                ApplyAssetTransaction(plan.Assets, _ => new ProjectAssetEdits(plan.Changes, plan.AddedChanges),
                    $"Replaced {waves.Count} sounds of {weapon.Name}");
            };
        }, blocking: false);
    }
}
