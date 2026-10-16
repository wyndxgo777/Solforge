using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

/** Checks that mod script updates are all-or-nothing: a failed write or deletion leaves earlier scripts at their old version. */
internal static class ModUpdateSafetyChecks
{
    /** Failed updates and linked paths must preserve every preexisting script. */
    public static void Run(string parent, PackageCatalog catalog)
    {
        var project = StudioProject.Create(Path.Combine(parent, "mod-update-safety"), "Update safety");
        project.SetMod(new("dev.projectsunrise.updatesafety", "1.0.0", "Update safety"));
        byte[] oldA = Encoding.UTF8.GetBytes("return { version = 1 }"), oldB = Encoding.UTF8.GetBytes("return { version = 2 }");
        byte[] next = Encoding.UTF8.GetBytes("return { version = 3 }");
        var installed = ModInstaller.BuildAndInstall(project, catalog, [new("alpha/alpha.lua", oldA), new("beta/beta.lua", oldB)]);
        string root = Path.Combine(installed.ModDirectory, "scripts");
        string alpha = Path.Combine(root, "alpha", "alpha.lua"), beta = Path.Combine(root, "beta", "beta.lua");
        byte[] manifest = File.ReadAllBytes(Path.Combine(installed.ModDirectory, "mod.json"));
        if (OperatingSystem.IsWindows())
        {
            using (var held = new FileStream(beta, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => ModInstaller.BuildAndUpdate(project, catalog, [new("alpha/alpha.lua", next), new("beta/beta.lua", next)]));
            Check(File.ReadAllBytes(alpha).SequenceEqual(oldA) && File.ReadAllBytes(beta).SequenceEqual(oldB),
                "A rejected second write retained the first script's new version.");
            using (var held = new FileStream(beta, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => ModInstaller.BuildAndUpdate(project, catalog, [new("alpha/alpha.lua", next)]));
            Check(File.ReadAllBytes(alpha).SequenceEqual(oldA) && File.ReadAllBytes(beta).SequenceEqual(oldB),
                "A failed deletion left earlier scripts updated.");
            Check(!Directory.EnumerateDirectories(installed.ModDirectory, ".script-update-*").Any(),
                "Successful rollback retained its recovery staging folder.");
        }
        string outside = Path.Combine(parent, "mod-update-outside"); Directory.CreateDirectory(outside);
        string protectedFile = Path.Combine(outside, "rogue.lua"); File.WriteAllBytes(protectedFile, oldB);
        string alias = Path.Combine(root, "rogue"); bool linked = false;
        try
        {
            Directory.CreateSymbolicLink(alias, outside); linked = true;
            Reject(() => ModInstaller.BuildAndUpdate(project, catalog, [new("alpha/alpha.lua", next), new("beta/beta.lua", next)]));
            Check(File.ReadAllBytes(protectedFile).SequenceEqual(oldB)
                && File.ReadAllBytes(alpha).SequenceEqual(oldA), "A linked installed folder escaped the update boundary.");
            Console.WriteLine("mod_update linked_directory=refused outside=unchanged");
        }
        catch (Exception error) when (!linked && error is IOException or UnauthorizedAccessException)
        { Console.WriteLine("mod_update linked_directory=unavailable"); }
        finally { if (linked) Directory.Delete(alias); }
        string helper = Path.Combine(root, "alpha", "helper.lua"); File.WriteAllBytes(helper, oldB);
        var complete = ModInstaller.BuildAndUpdate(project, catalog, [new("alpha/alpha.lua", next)]);
        Check(complete.Written == 1 && complete.Deleted == 1 && File.ReadAllBytes(alpha).SequenceEqual(next)
            && !File.Exists(beta) && File.ReadAllBytes(helper).SequenceEqual(oldB)
            && File.ReadAllBytes(Path.Combine(installed.ModDirectory, "mod.json")).SequenceEqual(manifest),
            "A successful script-only update changed the wrong files.");
        Console.WriteLine("mod_update failure_rollback=pass deletion_rollback=pass manifest=unchanged");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Unsafe or locked script update succeeded.");
    }
}
