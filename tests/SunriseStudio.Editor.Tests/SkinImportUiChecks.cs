using System.Reflection;
using SunriseStudio.Authoring;
using SunriseStudio.Editor;
using SunriseStudio.Projects;
using SunriseStudio.Rendering;

/** Checks the skin import UI: template discovery must not edit the project, and replacement is one undoable transaction. */
internal static class SkinImportUiChecks
{
    /** The real modal reviews an owned file, then one editor action can undo and redo the entire replacement. */
    internal static void Run(string gameRoot, string file, string root, string capture)
    {
        const uint entity = 0x80B82FE3, model = 0x80C4424F;
        var project = StudioProject.Create(Path.Combine(root, "skin-project"), "Skin UI fixture");
        string settings = Path.Combine(root, "skin-settings.json");
        new StudioSettings { BrowserWidth = 230, InspectorWidth = 250 }.Save(settings);
        Exception? failure = null;
        int phase = 0;
        int reviewFrames = 0;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new StudioForm(new(Game: gameRoot, Project: project.ManifestPath, Tag: entity,
                    Warp: true, Smoke: true, Frames: 40, SettingsPath: settings));
                const BindingFlags access = BindingFlags.Instance | BindingFlags.NonPublic;
                void Set(string field, object? value) => typeof(StudioForm).GetField(field, access)!.SetValue(form, value);
                PreparedCompatibleSkin? prepared = null;
                CompatibleSkinReplacementOptions? options = null;
                byte[]? original = null, edited = null;
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
                                Check(selected >= 0, "The checked template was not offered.");
                                Set("skinTemplates", templates); Set("skinTemplateIndex", selected);
                                Set("skinImportPath", file); Set("skinImportName", "Owned skin UI");
                                Set("skinImportPackage", package.ToString("X4")); Set("skinImportEntity", entity);
                                Set("skinImportDialog", true);
                                options = new(entity, model, 0x300, "Owned skin UI", package, 0);
                            });
                            phase = 1;
                        }
                        else if (phase == 1)
                        {
                            Check(workspace.Project!.AddedAssets.Count == 0 && workspace.Project.Changes.Count == 0,
                                "Template discovery edited the project.");
                            workspace.PrepareCompatibleSkin(file, options!, value =>
                            {
                                prepared = value; Set("preparedSkin", value);
                                typeof(StudioForm).GetMethod("CaptureSkinReviewIdentity", access)!.Invoke(form, null);
                            });
                            Set("skinReviewPending", true); phase = 2;
                        }
                        else if (phase == 2)
                        {
                            if (++reviewFrames < 3) return;
                            Check(prepared is { ConsumedBones: 63 } && workspace.Project!.AddedAssets.Count == 0,
                                "Review published changes or lost the rig limit.");
                            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capture))!);
                            ((Dx11Renderer)typeof(StudioForm).GetField("renderer", access)!.GetValue(form)!).CapturePng(capture);
                            workspace.ApplyAssetTransaction(prepared!.Additions,
                                _ => CompatibleSkinnedMesh.Edits(workspace.Catalog!, prepared), "Replaced skinned model");
                            phase = 3;
                        }
                        else if (phase == 3)
                        {
                            Check(workspace.Project!.AddedAssets.Count == 6 && workspace.Project.Changes.Count == 1 && workspace.CanUndo,
                                "Replacement did not create one undoable transaction.");
                            edited = workspace.Catalog!.Read(model); workspace.Undo(); phase = 4;
                        }
                        else if (phase == 4)
                        {
                            Check(workspace.Project!.AddedAssets.Count == 0 && workspace.Project.Changes.Count == 0
                                && workspace.Catalog!.Read(model).SequenceEqual(original!), "Undo left model or buffer changes.");
                            workspace.Redo(); phase = 5;
                        }
                        else if (phase == 5)
                        {
                            Check(workspace.Project!.AddedAssets.Count == 6 && workspace.Catalog!.Read(model).SequenceEqual(edited!),
                                "Redo did not restore the exact model and buffer identities.");
                            phase = 6; form.Close();
                        }
                    }
                    catch (Exception error) { failure = error; form.Close(); }
                };
                Application.Run(form);
                if (form.Failure is { } message) throw new InvalidOperationException(message);
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("Skin import UI failed.", failure);
        Check(phase == 6, "The skin UI did not complete review, apply, undo and redo.");
        Console.WriteLine("skin_import_ui review=pass source=unchanged apply=atomic undo=pass redo=pass");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
