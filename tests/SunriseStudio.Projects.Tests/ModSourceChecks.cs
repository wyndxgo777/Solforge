using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

/** Checks project mod source files: SDK modules are installed at their require path and source files never enter package deltas. */
internal static class ModSourceChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string parent, PackageCatalog catalog)
    {
        var project = StudioProject.Create(Path.Combine(parent, "module-project"), "Module sources");
        project.SetMod(new("dev.projectsunrise.sources", "1.0.0", "Module sources"));
        byte[] controller = Encoding.UTF8.GetBytes("return { on_start=function() end }\r\n");
        ModScriptFile[] controllers = [new("alpha/alpha.lua", controller)];
        Write(project, "scripts/alpha/alpha.lua", controller);
        byte[] helper = Encoding.UTF8.GetBytes("return { value=1 }\n");
        byte[] sdk = Encoding.UTF8.GetBytes("return { object='owned:one' }\n");
        string helperSource = Write(project, "scripts/lib/tools.lua", helper);
        string sdkSource = Write(project, "sdk/dev/example/bindings.lua", sdk);
        var install = ModInstaller.BuildAndInstall(project, catalog, controllers);
        string scripts = Path.Combine(install.ModDirectory, "scripts");
        string helperTarget = Path.Combine(scripts, "lib", "tools.lua");
        string receipt = Path.Combine(install.ModDirectory, ".studio-sources.json");
        Check(install.ScriptFiles == 2 && install.SdkFiles == 1 && File.Exists(receipt)
            && File.ReadAllBytes(helperTarget).SequenceEqual(helper)
            && File.ReadAllBytes(Path.Combine(install.ModDirectory, "sdk", "dev", "example", "bindings.lua")).SequenceEqual(sdk),
            "The project helper or SDK module was not installed at its require path.");
        Check(StudioProject.Open(project.DirectoryPath).AddedAssets.Count == 0 && project.Changes.Count == 0,
            "Owned source files entered package deltas.");

        byte[] next = Encoding.UTF8.GetBytes("return { value=2 }\n");
        File.WriteAllBytes(helperSource, next);
        var updated = ModInstaller.BuildAndUpdate(project, catalog, controllers);
        Check(updated.Written == 1 && updated.Deleted == 0 && File.ReadAllBytes(helperTarget).SequenceEqual(next),
            "A helper change did not enter the script reload update.");
        string manual = Path.Combine(scripts, "manual.lua"); File.WriteAllBytes(manual, helper);
        File.Delete(helperSource);
        string replacement = Write(project, "scripts/other/helper.lua", next);
        var changed = ModInstaller.BuildAndUpdate(project, catalog, controllers);
        Check(changed.Written == 1 && changed.Deleted == 1 && !File.Exists(helperTarget)
            && File.ReadAllBytes(Path.Combine(scripts, "other", "helper.lua")).SequenceEqual(next)
            && File.ReadAllBytes(manual).SequenceEqual(helper), "Helper removal deleted an unowned installed file.");

        byte[] beforeReceipt = File.ReadAllBytes(receipt);
        if (OperatingSystem.IsWindows())
        {
            string pending = Write(project, "scripts/new_folder/new_helper.lua", helper);
            using (var held = new FileStream(receipt, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => ModInstaller.BuildAndUpdate(project, catalog, controllers));
            Check(!File.Exists(Path.Combine(scripts, "new_folder", "new_helper.lua"))
                && File.ReadAllBytes(receipt).SequenceEqual(beforeReceipt), "Receipt failure retained a partial helper update.");
            File.Delete(pending);
        }
        File.WriteAllBytes(sdkSource, next);
        File.WriteAllBytes(replacement, helper);
        Reject(() => ModInstaller.BuildAndUpdate(project, catalog, controllers));
        Check(File.ReadAllBytes(Path.Combine(scripts, "other", "helper.lua")).SequenceEqual(next)
            && File.ReadAllBytes(receipt).SequenceEqual(beforeReceipt), "An SDK restart refusal still changed scripts.");
        File.WriteAllBytes(sdkSource, sdk);
        string invalid = Write(project, "scripts/unreachable.name.lua", helper);
        Reject(() => ModInstaller.BuildAndInstall(project, catalog, controllers));
        File.Delete(invalid);
        Check(File.ReadAllBytes(Path.Combine(scripts, "alpha", "alpha.lua")).SequenceEqual(controller),
            "A refused source path replaced the installed mod.");
        Reject(() => ModInstaller.BuildAndInstall(project, catalog, [new("large/large.lua", new byte[128 * 1024 + 1])]));
        Reject(() => ModInstaller.BuildAndInstall(project, catalog, [new("empty/empty.lua", [])]));
        Reject(() => ModInstaller.BuildAndInstall(project, catalog, [new("alpha/alpha.lua", helper)]));

        var sdkOnly = StudioProject.Create(Path.Combine(parent, "sdk-project"), "Owned SDK");
        sdkOnly.SetMod(new("dev.projectsunrise.sdk", "1.0.0", "Owned SDK"));
        Write(sdkOnly, "sdk/custom.lua", sdk);
        var library = ModInstaller.BuildAndInstall(sdkOnly, catalog, []);
        Check(library.Build is null && library.ScriptFiles == 0 && library.SdkFiles == 1
            && !Directory.Exists(Path.Combine(library.ModDirectory, "scripts")), "An SDK-only mod created a controller root.");
        string outside = Path.Combine(parent, "module-outside"); Directory.CreateDirectory(outside);
        string protectedFile = Path.Combine(outside, "outside.lua"); File.WriteAllBytes(protectedFile, helper);
        string linkedPath = Path.Combine(sdkOnly.DirectoryPath, "sdk", "linked"); bool linked = false;
        try
        {
            Directory.CreateSymbolicLink(linkedPath, outside); linked = true;
            Reject(() => ModInstaller.BuildAndInstall(sdkOnly, catalog, []));
            Check(!File.Exists(Path.Combine(library.ModDirectory, "sdk", "linked", "outside.lua"))
                && File.ReadAllBytes(protectedFile).SequenceEqual(helper), "SDK installation followed a source link.");
        }
        catch (Exception error) when (!linked && error is IOException or UnauthorizedAccessException)
        { Console.WriteLine("mod_sources linked_source=unavailable"); }
        finally { if (linked) Directory.Delete(linkedPath); }
        File.Delete(receipt);
        var legacy = ModInstaller.BuildAndUpdate(project, catalog, controllers);
        Check(legacy.Written == 1 && File.Exists(receipt) && File.ReadAllBytes(manual).SequenceEqual(helper),
            "An older install could not acquire source ownership without deleting manual helpers.");
        File.WriteAllText(receipt, "{\"format\":\"sunrise-mod-sources\",\"version\":1,\"scripts\":[\"../outside.lua\"]}");
        Reject(() => ModInstaller.BuildAndUpdate(project, catalog, controllers));
        Check(File.ReadAllBytes(manual).SequenceEqual(helper), "An invalid source receipt changed installed files.");
        Console.WriteLine("mod_sources helper=installed helper_update=pass owned_removal=pass manual=retained receipt_rollback=pass sdk=installed sdk_change=restart controller_size=bounded source_race=refused");
    }

    private static string Write(StudioProject project, string relative, byte[] bytes)
    {
        string path = Path.Combine(project.DirectoryPath, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
        return path;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Invalid module operation succeeded.");
    }
}
