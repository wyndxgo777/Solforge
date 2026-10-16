using System.Reflection;
using SunriseStudio.Authoring;
using SunriseStudio.Editor;
using SunriseStudio.Projects;
using SunriseStudio.Rendering;

/** Checks the skin entity creation UI: template offering, review expiry and per-project review identity while the worker runs. */
internal static class SkinEntityUiChecks
{
    /** The create review expires with project identity and one undo restores every allocated identity. */
    internal static void Run(string gameRoot, string file, string root, string capture)
    {
        const uint entity = 0x80B82FE3, model = 0x80C4424F;
        var project = StudioProject.Create(Path.Combine(root, "entity-project"), "Entity UI fixture");
        string settings = Path.Combine(root, "entity-settings.json");
        new StudioSettings { BrowserWidth = 230, InspectorWidth = 250 }.Save(settings);
        Exception? failure = null;
        int phase = 0, reviewFrames = 0, createdFrames = 0;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new StudioForm(new(Game: gameRoot, Project: project.ManifestPath, Tag: entity,
                    Warp: true, Smoke: true, Frames: 80, SettingsPath: settings));
                const BindingFlags access = BindingFlags.Instance | BindingFlags.NonPublic;
                void Set(string field, object? value) => typeof(StudioForm).GetField(field, access)!.SetValue(form, value);
                object? Get(string field) => typeof(StudioForm).GetField(field, access)!.GetValue(form);
                void Identity() => typeof(StudioForm).GetMethod("CaptureSkinReviewIdentity", access)!.Invoke(form, null);
                PreparedCompatibleEntity? prepared = null;
                CompatibleSkinReplacementOptions? options = null;
                string? createdId = null;
                byte[]? original = null, created = null;
                form.FrameRendered += () =>
                {
                    if (!form.EditorReady || form.Workspace.IsBusy) return;
                    try
                    {
                        var workspace = form.Workspace;
                        if (workspace.Error.Length != 0) throw new InvalidOperationException(workspace.Error);
                        if (phase == 0)
                        {
                            original = workspace.Catalog!.ReadOriginal(model);
                            ushort package = Enumerable.Range(0x100, 0xC00).Select(value => (ushort)value)
                                .First(id => !workspace.Catalog.ContainsInstalledPackage(id));
                            workspace.ReadCompatibleSkinTemplates(entity, templates =>
                            {
                                int selected = templates.ToList().FindIndex(value => value.ModelTag == model && value.PartOffset == 0x300);
                                Check(selected >= 0, "The entity creation template was not offered.");
                                Set("skinTemplates", templates); Set("skinTemplateIndex", selected);
                                Set("skinImportPath", file); Set("skinImportName", "Owned entity UI");
                                Set("skinImportPackage", package.ToString("X4")); Set("skinImportEntity", entity);
                                Set("skinCreateAsset", true); Set("skinImportDialog", true);
                                options = new(entity, model, 0x300, "Owned entity UI", package, 0);
                            });
                            phase = 1;
                        }
                        else if (phase == 1)
                        {
                            workspace.PrepareCompatibleSkinEntity(file, options!, value =>
                            { prepared = value; Set("preparedSkinEntity", value); Identity(); });
                            try { workspace.CloseProject(); throw new InvalidDataException("A project closed while its skin worker was active."); }
                            catch (InvalidOperationException) { }
                            Set("skinReviewPending", true); phase = 2;
                        }
                        else if (phase == 2)
                        {
                            if (++reviewFrames < 3) return;
                            Check(prepared is { Additions.Count: 9 } && Get("preparedSkinEntity") is not null
                                && workspace.Project!.AddedAssets.Count == 0, "Review published changes or expired unexpectedly.");
                            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capture))!);
                            ((Dx11Renderer)typeof(StudioForm).GetField("renderer", access)!.GetValue(form)!).CapturePng(capture);
                            Set("skinPreparedProject", null); phase = 3;
                        }
                        else if (phase == 3)
                        {
                            Check(Get("preparedSkinEntity") is null, "A different project identity retained the prior review.");
                            Set("preparedSkinEntity", prepared); Identity(); phase = 4;
                        }
                        else if (phase == 4)
                        {
                            typeof(StudioForm).GetMethod("ApplySkinEntity", access)!.Invoke(form, [prepared]);
                            createdId = workspace.Project!.AddedAssets[^1].Id;
                            created = workspace.Catalog!.Read(prepared!.EntityTag); phase = 5;
                        }
                        else if (phase == 5)
                        {
                            if (++createdFrames < 3) return;
                            Check(workspace.Project!.AddedAssets.Count == 9 && workspace.Project.Changes.Count == 0
                                && workspace.CanUndo && workspace.Document?.AddedAssetId == createdId,
                                "Created entity did not retain its undo or navigation identity.");
                            Check(workspace.Catalog!.Read(model).SequenceEqual(original!), "Creation changed the installed model.");
                            Check(!(bool)typeof(StudioForm).GetProperty("CanReplaceSkin", access)!.GetValue(form)!,
                                "An added clone offered an installed-only replacement workflow.");
                            ((Dx11Renderer)typeof(StudioForm).GetField("renderer", access)!.GetValue(form)!)
                                .CapturePng(Path.ChangeExtension(capture, ".created.png"));
                            workspace.Undo(); phase = 6;
                        }
                        else if (phase == 6)
                        {
                            Check(workspace.Project!.AddedAssets.Count == 0 && workspace.Project.Changes.Count == 0
                                && !workspace.Catalog!.TryGetEntry(prepared!.EntityTag, out _), "Undo retained part of the created entity.");
                            workspace.Redo(); phase = 7;
                        }
                        else if (phase == 7)
                        {
                            Check(workspace.Project!.AddedAssets.Count == 9 && workspace.Catalog!.Read(prepared!.EntityTag).SequenceEqual(created!),
                                "Redo changed the entity identity or bytes.");
                            phase = 8; form.Close();
                        }
                    }
                    catch (Exception error) { failure = error; form.Close(); }
                };
                Application.Run(form);
                if (form.Failure is { } message) throw new InvalidOperationException(message);
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("Entity create UI failed.", failure);
        Check(phase == 8, "The entity UI did not complete review, apply, undo and redo.");
        Console.WriteLine("skin_entity_ui review=pass project_identity=pass source=unchanged apply=atomic navigation=pass undo=pass redo=pass");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
