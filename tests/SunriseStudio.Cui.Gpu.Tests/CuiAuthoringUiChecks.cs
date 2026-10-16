using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Projects;

/** GPU-backed UI check of CUI authoring: structural edits must isolate the root atomically and a failed publish must leave no clone or undo step. */
internal static class CuiAuthoringUiChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string install, string capture)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "sunrise-cui-authoring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var project = StudioProject.Create(Path.Combine(temporary, "project"), "CUI authoring");
            string settings = Path.Combine(temporary, "settings.json");
            new StudioSettings { DiskCacheEnabled = false }.Save(settings);
            using var form = new StudioForm(new(Game: install, Project: project.ManifestPath, Tag: 0x80BC6F5A,
                Page: "CUI", Warp: true, Smoke: true, Frames: 90, SettingsPath: settings, Capture: capture));
            int phase = 0, undo = 0, redo = 0; ushort panel = 0, text = 0, duplicate = 0; uint originalRoot = 0, privateRoot = 0;
            byte[]? original = null;
            form.FrameRendered += () =>
            {
                var work = form.Workspace;
                if (work.Error.Length != 0) throw new InvalidDataException(work.Error);
                if (!form.EditorReady || work.IsBusy || work.Document?.Cui?.Composition is not { } composition) return;
                var page = form.Pages.Pages.OfType<CuiPage>().Single();
                var author = Get<CuiWidgetAuthoring>(page, "structure");
                if (author is null) return;
                var current = work.Document.Cui.Document!;
                var root = composition.Resources[current.RootResource];
                var context = new StudioPageContext(work, form.SceneViewport!, _ => { });
                void Apply(CuiWidgetMutation mutation, string? name = null) => typeof(CuiPage).GetMethod("ApplyStructure", Access)!
                    .Invoke(page, [context, mutation, name, "CUI test edit"]);
                switch (phase)
                {
                    case 0:
                        originalRoot = current.RootResource; original = work.Catalog!.ReadOriginal(originalRoot);
                        var first = author.Create(CuiWidgetKind.Frame, 11, 0, new(300, 160, 720, 180)); panel = first.SelectedNode;
                        byte[] manifest = File.ReadAllBytes(work.Project!.ManifestPath);
                        string[] files = Directory.GetFiles(work.Project.DirectoryPath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
                        bool refused = false;
                        using (var locked = new FileStream(work.Project.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                            try { Apply(first, "Sunrise panel"); }
                            catch (TargetInvocationException error) when (error.InnerException is IOException or UnauthorizedAccessException) { refused = true; }
                        Check(refused && work.Project.Changes.Count == 0 && work.Project.AddedAssets.Count == 0
                            && !work.CanUndo && File.ReadAllBytes(work.Project.ManifestPath).AsSpan().SequenceEqual(manifest)
                            && Directory.GetFiles(work.Project.DirectoryPath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).SequenceEqual(files)
                            && work.Catalog.Read(current.TagHash).AsSpan().SequenceEqual(current.Data.Span)
                            && work.Catalog.Read(originalRoot).AsSpan().SequenceEqual(original),
                            "Failed root publication retained a clone, labels, hierarchy change, or undo step.");
                        Apply(first, "Sunrise panel"); phase++; break;
                    case 1:
                        privateRoot = current.RootResource;
                        Check(privateRoot != originalRoot && work.Project!.Changes.Count == 1 && work.Project.AddedAssets.Count == 2,
                            "First structural edit did not isolate the root and labels atomically.");
                        Check(work.Project!.AddedAssets.Single(asset => asset.Native is not null).BaseTagHash == originalRoot
                            && work.Catalog!.Read(originalRoot).SequenceEqual(original!), "Private root copied data or changed the original.");
                        var second = author.Create(CuiWidgetKind.Text, 12, panel, new(20, 20, 680, 100)); text = second.SelectedNode;
                        Apply(second, "Status"); phase++; break;
                    case 2:
                        var component = root.Objects.Where(owner => owner.IsDefaultProfile).SelectMany(owner => owner.Components).Single(row => row.Key == text);
                        var edited = CuiPropertyEdits.Upsert(root.Data.Span, root, component,
                            [new(0x405, 232, BitConverter.GetBytes(0xF9C88800u)), new(0x420, 152, BitConverter.GetBytes(36u)),
                                new(0x414, 216, MemoryMarshal.AsBytes(new[] { new Vector4(.7f, .9f, 1, 1) }.AsSpan()).ToArray())]);
                        Apply(new(current.Data.ToArray(), edited, text, new HashSet<ushort>())); phase++; break;
                    case 3:
                        Check(work.Document.Cui.ComposedContent[text].Text == "Waiting for Other Players", "New text did not resolve or render its stored content.");
                        var copied = author.Duplicate(text, 13, panel); duplicate = copied.SelectedNode; Apply(copied, "Status copy"); phase++; break;
                    case 4:
                        Check(Label(page, composition, duplicate) == "Status copy", "New widget labels were not read from the project.");
                        Apply(author.Remove(duplicate)); phase++; break;
                    case 5:
                        Check(!composition.Nodes.Any(node => node.Id == duplicate), "Deleted widget remained active.");
                        work.Undo(); phase++; break;
                    case 6:
                        Check(Label(page, composition, duplicate) == "Status copy", "Undo did not restore the widget and label together.");
                        work.Redo(); phase++; break;
                    case 7:
                        Check(!composition.Nodes.Any(node => node.Id == duplicate), "Redo did not remove the duplicate."); phase++; break;
                    case 8:
                        if (work.CanUndo) { work.Undo(); undo++; }
                        else
                        {
                            Check(undo == 5 && work.Project!.Changes.Count == 0 && work.Project.AddedAssets.Count == 0
                                && current.RootResource == originalRoot, "Undo retained a private clone, labels, or hierarchy delta.");
                            phase++;
                        }
                        break;
                    case 9:
                        if (work.CanRedo) { work.Redo(); redo++; }
                        else
                        {
                            Check(redo == 5 && current.RootResource == privateRoot && Label(page, composition, text) == "Status",
                                "Redo changed native IDs or lost authoring labels.");
                            var reopened = StudioProject.Open(project.DirectoryPath);
                            Check(reopened.Changes.Count == 1 && reopened.AddedAssets.Count == 2
                                && BinaryPrimitives.ReadUInt32LittleEndian(reopened.ReadAsset(current.TagHash, work.Catalog!).AsSpan(28)) == privateRoot,
                                "Reopening the saved project lost its private root reference.");
                            Check(work.Catalog!.ReadOriginal(originalRoot).SequenceEqual(original!), "Authoring changed an installed resource.");
                            typeof(CuiPage).GetField("selectedComposed", Access)!.SetValue(page, (int)text);
                            typeof(CuiPage).GetField("focusComposed", Access)!.SetValue(page, true);
                            phase++;
                        }
                        break;
                }
            };
            Application.Run(form);
            if (form.Failure is { } failure) throw new InvalidDataException(failure);
            Check(phase == 10, "CUI GUI did not finish its structural authoring checks.");
            Console.WriteLine("cui_authoring_ui create=pass content=pass duplicate=pass delete=pass labels=pass rollback=pass atomic_undo=pass redo=pass reopen=pass source=unchanged");
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    private const BindingFlags Access = BindingFlags.Instance | BindingFlags.NonPublic;

    private static T? Get<T>(CuiPage page, string name) where T : class => typeof(CuiPage).GetField(name, Access)!.GetValue(page) as T;
    private static string Label(CuiPage page, CuiComposition composition, int id) => (string)typeof(CuiPage).GetMethod("StructureName", Access)!
        .Invoke(page, [composition.Nodes.Single(node => node.Id == id)])!;
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}
