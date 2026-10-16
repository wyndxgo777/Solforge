using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

/** Checks the CUI comparison draft editor: a failed save must report failure and keep the edited operand and comparison. */
internal static class CuiComparisonDraftChecks
{
    private const BindingFlags Access = BindingFlags.Instance | BindingFlags.NonPublic;
    /** A failed comparison transaction retains inputs and leaves no partial assets. */
    public static void Run()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "sunrise-cui-comparison-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temporary, "install", "packages"));
        try
        {
            const uint rootTag = 0x80A00000, programTag = rootTag + 1;
            byte[] resource = new byte[536]; Q(resource, 0, resource.Length);
            Array(resource, 24, 352, 2, 0x80804618); U(resource, 368, 1); U(resource, 372, 2);
            Array(resource, 56, 384, 2, 0x80804622);
            Q(resource, 400, (long)CuiTypeRegistry.FrameType); S(resource, 410, 1);
            Q(resource, 416, (long)CuiTypeRegistry.TextType); S(resource, 426, 2);
            Array(resource, 88, 448, 1, 0x808046D8);
            S(resource, 464, 1); S(resource, 466, 1); Q(resource, 480, 0x202);
            S(resource, 488, 2); S(resource, 490, 2); Q(resource, 504, 0x201);
            Q(resource, 512, 16); U(resource, 524, 0x808046DC); resource[528] = 1; U(resource, 532, 3);
            byte[] program = new byte[76]; Q(program, 0, program.Length);
            Array(program, 8, 48, 3, 0x80804616); S(program, 24, 3); S(program, 26, 3); U(program, 28, rootTag);
            S(program, 64, 0x7FFF); S(program, 70, 1); S(program, 74, 2);
            string install = Path.Combine(temporary, "install");
            PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_cui_draft_0100_0.pkg"), 0x100,
                [new(0x80804825, 8 << 9, resource), new(0x8080496A, 8 << 9, program)]);
            using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
            workspace.OpenInstallation(install); Wait(workspace);
            workspace.Select(programTag); Wait(workspace);
            workspace.CreateProject(Path.Combine(temporary, "project"), "Comparison retry"); Wait(workspace);
            var parsed = CuiDocument.Read(programTag, 0x8080496A, workspace.Document!.Bytes);
            var scene = CuiComposition.Read(parsed, tag => CuiDocument.Read(tag, 0x80804825, workspace.Catalog!.Read(tag)));
            using var page = new CuiPage();
            typeof(CuiPage).GetField("document", Access)!.SetValue(page, parsed);
            typeof(CuiPage).GetField("composition", Access)!.SetValue(page, scene);
            typeof(CuiPage).GetField("structure", Access)!.SetValue(page, new CuiWidgetAuthoring(parsed, scene));
            CheckComparisonDraft(page, new(workspace, null!, _ => { }), scene);
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    /**
     * Retry must publish the retained comparison through the same private-root transaction.
     * @param page Owns the comparison draft.
     * @param context Owns the temporary project.
     * @param scene Synthetic root and linked widget identities.
     */
    private static void CheckComparisonDraft(CuiPage page, StudioPageContext context, CuiComposition scene)
    {
        var workspace = context.Workspace;
        var document = workspace.Document!;
        var rows = CuiComposedBindings.Read(scene);
        int position = Enumerable.Range(0, rows.Count).First(index => rows[index].Instance.Root
            && rows[index].Local.Transform.Kind == CuiBindingTransformKind.CompareInteger);
        var binding = rows[position];
        var selected = scene.Nodes.Single(node => node.Id == binding.Composed.Target.Node);
        var comparison = binding.Local.Transform.Comparison == CuiComparison.Equal ? CuiComparison.NotEqual : CuiComparison.Equal;
        Set("editingBinding", position); Set("bindingComparison", (int)comparison); Set("bindingOperand", "7");
        byte[] original = scene.Resources[binding.Instance.Resource].Data.ToArray();
        byte[] manifest = File.ReadAllBytes(workspace.Project!.ManifestPath);
        using (var held = new FileStream(workspace.Project.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Check(!Save(), "A failed comparison save reported success.");
        Check(workspace.Error.Length != 0 && (int)Field("editingBinding")! == position
            && (int)Field("bindingComparison")! == (int)comparison && (string)Field("bindingOperand")! == "7",
            "A failed comparison save discarded its edited operand or comparison.");
        Check(workspace.Project.Changes.Count == 0 && workspace.Project.AddedAssets.Count == 0 && !workspace.CanUndo
            && File.ReadAllBytes(workspace.Project.ManifestPath).AsSpan().SequenceEqual(manifest)
            && workspace.Catalog!.Read(binding.Instance.Resource).AsSpan().SequenceEqual(original),
            "A failed comparison save published partial bytes.");
        Check(Save() && (int)Field("editingBinding")! == -1, "Comparison retry did not clear the saved draft.");
        Wait(workspace);
        var hierarchy = CuiDocument.Read(document.Entry.TagHash, document.Entry.Reference, workspace.Document!.Bytes);
        var saved = CuiBindings.Read(workspace.Catalog!.Read(hierarchy.RootResource))[binding.Local.Index];
        Check(saved.Transform.Comparison == comparison && saved.Transform.Operand == 7 && workspace.CanUndo,
            "Comparison retry did not save the retained draft.");
        workspace.Undo(); Wait(workspace);
        Check(workspace.Project.Changes.Count == 0 && workspace.Project.AddedAssets.Count == 0
            && workspace.Document!.Bytes.AsSpan().SequenceEqual(document.Bytes), "Comparison retry undo retained a root or delta.");
        Console.WriteLine("cui_comparison_draft failure=retained retry=pass undo=pass source=unchanged");

        bool Save() => (bool)typeof(CuiPage).GetMethod("SaveComparisonDraft", Access)!.Invoke(page, [context, scene, selected, binding])!;
        object? Field(string name) => typeof(CuiPage).GetField(name, Access)!.GetValue(page);
        void Set(string name, object value) => typeof(CuiPage).GetField(name, Access)!.SetValue(page, value);
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var time = Stopwatch.StartNew();
        while (workspace.IsBusy && time.Elapsed < TimeSpan.FromSeconds(30)) { workspace.Poll(); Thread.Sleep(1); }
        workspace.Poll();
        Check(!workspace.IsBusy && workspace.Error.Length == 0, workspace.Error.Length == 0 ? "Workspace timed out." : workspace.Error);
    }
    private static void Array(byte[] b, int field, int header, int count, uint type)
    { Q(b, field, count); Q(b, field + 8, header - field - 8); U(b, header - 4, 0x80809FBD); Q(b, header, count); U(b, header + 8, type); }
    private static void Q(byte[] b, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(at), value);
    private static void U(byte[] b, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), value);
    private static void S(byte[] b, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), value);
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}
