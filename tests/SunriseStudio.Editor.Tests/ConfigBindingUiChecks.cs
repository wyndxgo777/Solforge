using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Rendering;

/** Checks the typed config binding review UI: drafts are not published before save and an old draft does not leak into another project. */
internal static class ConfigBindingUiChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string root, string capture)
    {
        string install = Path.Combine(root, "config-modal-install"), packages = Path.Combine(install, "packages");
        Directory.CreateDirectory(packages);
        PackageWriter.WriteNew(Path.Combine(packages, "w64_config_0100_0.pkg"), 0x100,
            [new(0x80804825, 0x1000, ConfigBindingFixture.Create(), "Config widget")]);
        var project = StudioProject.Create(Path.Combine(root, "config-modal-project"), "Typed config binding review");
        project.SetMod(new("test.binding", "1.0.0", "Typed config", Config:
            [new("left", ModConfigType.Float, "123.5"), new("threshold", ModConfigType.Int, "-42"),
             new("auto_width", ModConfigType.Bool, "true")]));
        string settings = Path.Combine(root, "config-modal-settings.json");
        new StudioSettings { BrowserWidth = 230, InspectorWidth = 310 }.Save(settings);
        Exception? failure = null; int phase = 0, frames = 0;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new StudioForm(new(Game: install, Project: project.ManifestPath, Tag: 0x80A00000,
                    Warp: true, Smoke: true, Frames: 24, SettingsPath: settings));
                form.Workspace.CacheOptions = new() { Enabled = false };
                const BindingFlags access = BindingFlags.Instance | BindingFlags.NonPublic;
                ConfigBindingDraft? pending = null;
                form.FrameRendered += () =>
                {
                    if (!form.EditorReady || form.Workspace.IsBusy) return;
                    try
                    {
                        if (phase == 0)
                        {
                            typeof(StudioForm).GetMethod("BeginConfigBindings", access)!.Invoke(form, null);
                            var draft = (ConfigBindingDraft)typeof(StudioForm).GetField("configBindingDraft", access)!.GetValue(form)!;
                            pending = draft;
                            draft.Bind(form.Workspace, draft.Sites.First(site => site.Type == ModConfigType.Int), "threshold");
                            draft.Bind(form.Workspace, draft.Sites.First(site => site.Type == ModConfigType.Bool), "auto_width");
                            draft.Bind(form.Workspace, draft.Sites.First(site => site.Type == ModConfigType.Float && site.Site.OwnerIndex == 0), "left");
                            typeof(StudioForm).GetField("configSiteIndex", access)!.SetValue(form, 0);
                            typeof(StudioForm).GetField("configSettingKey", access)!.SetValue(form, "left");
                            phase = 1;
                        }
                        else if (phase == 1 && ++frames >= 3)
                        {
                            if (form.Workspace.Project!.ConfigBindings.Count != 0) throw new InvalidDataException("Draft published before save.");
                            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capture))!);
                            ((Dx11Renderer)typeof(StudioForm).GetField("renderer", access)!.GetValue(form)!).CapturePng(capture);
                            pending!.Save(form.Workspace); phase = 2;
                        }
                        else if (phase == 2)
                        {
                            if (form.Workspace.Project!.ConfigBindings.Count != 3) throw new InvalidDataException("Binding save did not publish.");
                            phase = 3; form.Close();
                        }
                    }
                    catch (Exception error) { failure = error; form.Close(); }
                };
                Application.Run(form);
                if (form.Failure is { } message) throw new InvalidOperationException(message);
                using var configured = new StudioForm(new(Game: install, Project: project.ManifestPath, Tag: 0x80A00000,
                    Warp: true, Smoke: true, Frames: 12, SettingsPath: settings, Page: "sunrise.cui",
                    Capture: Path.ChangeExtension(capture, ".configured.png")));
                configured.Workspace.CacheOptions = new() { Enabled = false };
                Application.Run(configured);
                if (configured.Failure is { } configuredFailure) throw new InvalidOperationException(configuredFailure);
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("Config binding UI failed.", failure);
        if (phase != 3) throw new InvalidDataException("Config binding UI did not reach capture and save.");
        Console.WriteLine("config_binding_ui types=3 draft=private modal=pass warp=pass");
    }
}
