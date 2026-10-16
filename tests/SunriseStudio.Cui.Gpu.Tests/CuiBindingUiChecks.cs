using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Formats;

/** GPU-backed UI check that CUI bindings place and hide frames (label, ready source) as the authored input state demands. */
internal static class CuiBindingUiChecks
{
    /** The waiting-HUD label hides through its native NOT binding when the ready input is true. */
    public static void Run(string install, string capture)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "sunrise-cui-bindings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string settings = Path.Combine(temporary, "settings.json");
            new StudioSettings { DiskCacheEnabled = false }.Save(settings);
            using var form = new StudioForm(new(Game: install, Tag: 0x80BC6F5A, Page: "CUI", Warp: true, Smoke: true,
                Frames: 90, SettingsPath: settings, Capture: capture));
            int phase = 0, hidden = 0, label = 0, source = 0, treeRows = 0;
            form.FrameRendered += () =>
            {
                var work = form.Workspace;
                if (work.Error.Length != 0) throw new InvalidDataException(work.Error);
                if (!form.EditorReady || work.IsBusy || work.Document?.Cui?.Composition is not { } composition) return;
                var page = form.Pages.Pages.OfType<CuiPage>().Single();
                if (!ReferenceEquals(Field(page, "bindingScene"), composition)) return;
                switch (phase)
                {
                    case 0:
                        var instance = composition.Instances.Single(row => row.Resource == 0x80BC722A);
                        label = composition.ComposedId(instance, 0x51C) ?? throw new InvalidDataException("The label frame is not placed.");
                        source = composition.ComposedId(instance, 0x552) ?? throw new InvalidDataException("The ready source is not placed.");
                        var inputs = (Dictionary<(ushort Node, string Path), CuiPreviewValue>)Field(page, "bindingInputs")!;
                        inputs[((ushort)source, "202")] = CuiPreviewValue.FromBool(true);
                        Set(page, "bindingValuesStale", true); Set(page, "applyBindings", true);
                        Set(page, "selectedComposed", label); Set(page, "focusComposed", true);
                        phase++; break;
                    case 1:
                        var set = (HashSet<int>)Field(page, "bindingHidden")!;
                        var children = composition.Nodes.Where(node => node.ParentId == label).Select(node => node.Id).ToArray();
                        Check(set.Contains(label) && children.All(set.Contains), "The ready input did not hide the label frame and its children.");
                        inputs = (Dictionary<(ushort Node, string Path), CuiPreviewValue>)Field(page, "bindingInputs")!;
                        inputs[((ushort)source, "202")] = CuiPreviewValue.FromBool(false); Set(page, "bindingValuesStale", true);
                        hidden = set.Count; phase++; break;
                    case 2:
                        Check(!((HashSet<int>)Field(page, "bindingHidden")!).Contains(label), "A false ready input left the label hidden.");
                        Set(page, "applyBindings", false); Set(page, "revealComposed", true);
                        ((HashSet<int>)Field(page, "hiddenWidgets")!).Add(label); Set(page, "treeDirty", true);
                        phase++; break;
                    case 3:
                        var parents = composition.Nodes.ToDictionary(node => node.Id, node => node.ParentId);
                        var expanded = (HashSet<int>)Field(page, "expandedWidgets")!;
                        for (int id = parents[label]; id > 0; id = parents.GetValueOrDefault(id, -1))
                            Check(expanded.Contains(id), "Reveal did not expand the label's ancestors.");
                        var rows = (Array)Field(page, "widgetRows")!;
                        Check(rows.Cast<(int Id, int Depth, bool HasChildren)>().Any(row => row.Id == label), "The revealed label has no tree row.");
                        var tree = (HashSet<int>)Field(page, "hiddenWidgetTree")!;
                        Check(tree.Contains(label) && composition.Nodes.Where(node => node.ParentId == label).All(node => tree.Contains(node.Id)),
                            "Hiding the label did not hide its children.");
                        treeRows = rows.Length; phase++; break;
                }
            };
            Application.Run(form);
            if (form.Failure is { } failure) throw new InvalidDataException(failure);
            Check(phase == 4, "CUI binding preview did not finish.");
            Console.WriteLine($"cui_binding_ui hierarchy=80BC6F5A resource=80BC722A label={label} source={source} hidden={hidden} "
                + $"ready=hidden not_ready=shown capture={capture}");
            Console.WriteLine($"cui_tree_ui rows={treeRows} reveal=expanded hide=recursive");
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    private const BindingFlags Access = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(CuiPage page, string name) => typeof(CuiPage).GetField(name, Access)!.GetValue(page);
    private static void Set(CuiPage page, string name, object value) => typeof(CuiPage).GetField(name, Access)!.SetValue(page, value);
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}
