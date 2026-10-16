using SunriseStudio.Creation.Tests;

string root = Path.Combine(Path.GetTempPath(), "sunrise-creation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "install", "packages"));
try
{
    if (args is ["--item-icon", var iconGame, var iconKeys, var iconProject, var iconPng])
    {
        ItemIconWorkflowChecks.Run(iconGame, iconKeys, iconProject, iconPng, root);
        return;
    }
    if (args is ["--perk-presentation", var perkGame, var perkKeys])
    {
        ExoticPerkPresentationChecks.Run(perkGame, perkKeys, root);
        return;
    }
    CreationFieldChecks.Run();
    PerkBehaviorEditorChecks.Run();
    CustomPerkAuthoringChecks.Run();
    ItemCloneChecks.Run();
    StringBankChecks.Run(root);
    ItemWorkflowChecks.Run(root);
    int game = Array.IndexOf(args, "--game-root");
    int keyOption = Array.IndexOf(args, "--keys");
    string? keys = keyOption >= 0 && keyOption + 1 < args.Length ? args[keyOption + 1] : null;
    if (game >= 0 && game + 1 < args.Length) ItemSocketWorkflowChecks.Owned(args[game + 1], root, keys);
    int weaponSource = Array.IndexOf(args, "--weapon-source");
    if (game >= 0 && game + 1 < args.Length && weaponSource >= 0 && weaponSource + 1 < args.Length)
        ExoticWeaponWorkflowChecks.Owned(args[game + 1], args[weaponSource + 1], root, keys);
    Console.WriteLine("creation synthetic=pass");
}
catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
finally
{
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(root).StartsWith("sunrise-creation-", StringComparison.Ordinal)) Directory.Delete(root, true);
}
