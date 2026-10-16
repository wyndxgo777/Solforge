using System.Collections;
using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Projects;
using SunriseStudio.Rendering;

/** Checks the vendor workflow UI: New is registered disabled with its reason, Clone is available, and blockers are explained. */
internal static class VendorWorkflowUiChecks
{
    /** The registered modal must retain a failed draft and adopt the created native identity. */
    internal static void Run(string install, string temporary, string capture)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Frames(install, temporary, capture); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("Vendor workflow UI failed.", failure);
    }

    private static void Frames(string install, string temporary, string capture)
    {
        var project = StudioProject.Create(Path.Combine(temporary, "vendor-ui"), "Vendor UI");
        string settings = Path.Combine(temporary, "vendor-ui-settings.json");
        new StudioSettings().Save(settings);
        using var form = new StudioForm(new(Game: install, Project: project.ManifestPath, Tag: 0x8131907F,
            Page: "Investment", Warp: true, Smoke: true, Frames: 600, WaitForNames: true, SettingsPath: settings));
        const BindingFlags access = BindingFlags.Instance | BindingFlags.NonPublic;
        var dialog = (CreationDialog)typeof(StudioForm).GetField("creation", access)!.GetValue(form)!;
        var start = typeof(StudioForm).GetMethod("StartCreation", access, [typeof(CreationType), typeof(uint?), typeof(string)])!;
        VendorWorkflow? workflow = null;
        int phase = 0, pause = 0;
        string? id = null;
        form.FrameRendered += () =>
        {
            var work = form.Workspace;
            if (!form.EditorReady || work.IsBusy) return;
            if (phase != 3 && work.Error.Length != 0) throw new InvalidDataException(work.Error);
            switch (phase)
            {
                case 0:
                    var blocked = CreationTypes.For("Vendors", CreationMode.New);
                    Check(!blocked.Available && blocked.Name == "Vendor" && blocked.Blocker.Contains("Use Clone", StringComparison.Ordinal),
                        "Vendors New is not registered as disabled with its reason.");
                    var type = CreationTypes.For("Vendors", CreationMode.Clone);
                    Check(type.Available, "The vendor Clone action is unavailable.");
                    start.Invoke(form, [type, (uint?)0x8131907F, ""]);
                    phase++; break;
                case 1:
                    if (work.Investment is null) return;
                    workflow = (VendorWorkflow)typeof(CreationDialog).GetField("workflow", access)!.GetValue(dialog)!;
                    Check(workflow.CloneSource?.Tag == 0x8131907F, "Clone did not preset the base vendor.");
                    var interaction = workflow.Fields.Single(field => field.Label == "Existing interaction");
                    workflow.Update(work);
                    Check(interaction.RowFilter is null && interaction.Rows!().Any(row => row.Entry.TagHash == 0x8153331F),
                        "The interaction picker does not list the vendor interaction.");
                    workflow.Fields.Single(field => field.Label == "Name").Text = "Workshop Quartermaster";
                    interaction.Tag = 0x8153331F;
                    workflow.Update(work);
                    var rows = (IList)typeof(VendorWorkflow).GetField("sales", access)!.GetValue(workflow)!;
                    while (rows.Count > 1) rows.RemoveAt(rows.Count - 1);
                    Check(workflow.Problems(work).Count == 0, string.Join("\n", workflow.Problems(work)));
                    phase++; break;
                case 2:
                    if (++pause < 3) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capture))!);
                    ((Dx11Renderer)typeof(StudioForm).GetField("renderer", access)!.GetValue(form)!).CapturePng(capture);
                    using (var locked = new FileStream(work.Project!.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        typeof(CreationDialog).GetMethod("Start", access)!.Invoke(dialog, [work]);
                    Check(work.Error.Length != 0 && dialog.IsOpen && work.Project.AddedAssets.Count == 0,
                        "A failed modal save closed the draft or published assets.");
                    phase++; break;
                case 3:
                    Check(ReferenceEquals(workflow, typeof(CreationDialog).GetField("workflow", access)!.GetValue(dialog)),
                        $"Failed vendor modal dropped its workflow; open={dialog.IsOpen}.");
                    Check(workflow!.Fields.Single(field => field.Label == "Name").Text == "Workshop Quartermaster",
                        "Failed vendor modal lost its name draft.");
                    typeof(CreationDialog).GetMethod("Start", access)!.Invoke(dialog, [work]);
                    Check(work.Error.Length == 0, work.Error);
                    phase++; break;
                case 4:
                    if (dialog.IsOpen || work.Document?.AddedAssetId is not { } selected) return;
                    id = selected;
                    typeof(StudioForm).GetMethod("EditInWorkflow", access)!.Invoke(form, null);
                    phase++; break;
                case 5:
                    if (typeof(CreationDialog).GetField("workflow", access)!.GetValue(dialog) is not VendorWorkflow edit) return;
                    Check(edit.Title == "Edit vendor", "The selected added vendor did not route to its edit workflow.");
                    edit.Fields.Single(field => field.Label == "Secondary text").Text = "Tools and supplies";
                    edit.Update(work);
                    Check(edit.Problems(work).Count == 0, string.Join("\n", edit.Problems(work)));
                    typeof(CreationDialog).GetMethod("Start", access)!.Invoke(dialog, [work]);
                    Check(work.Error.Length == 0, work.Error);
                    phase++; break;
                case 6:
                    if (dialog.IsOpen) return;
                    Check(work.Document?.AddedAssetId == id && work.CanUndo, "Vendor edit lost its added selection identity.");
                    work.Undo(); phase++; break;
                case 7: work.Redo(); phase++; break;
                case 8:
                    Check(work.Document?.AddedAssetId == id, "Vendor redo changed selection identity.");
                    phase++; form.Close(); break;
            }
        };
        Application.Run(form);
        if (form.Failure is { } error) throw new InvalidDataException(error);
        Check(phase == 9, $"Vendor modal stopped at phase {phase}.");
        Console.WriteLine("vendor_ui create=registered failure=draft_retained retry=pass edit=registered undo=pass redo=identity");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
