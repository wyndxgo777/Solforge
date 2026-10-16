using System.Buffers.Binary;
using SunriseStudio.Interchange;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

/** Checks that a bundle import layers its raw delta with object edits instead of writing object edit values into the delta. */
internal static class ObjectEditImportChecks
{
    /** A bundle holds compiled bytes; its raw delta must keep the raw value wherever an object edit writes. */
    internal static void Run(string root, PackageCatalog catalog, uint tag)
    {
        var project = StudioProject.Create(Path.Combine(root, "object-import-project"), "Object edit import");
        project.ObjectEditCompiler = new ScaleCompiler();
        byte[] original = catalog.ReadOriginal(tag);
        var target = new ObjectEditTarget(tag, ObjectEditLayout.StaticInstance, 4, 4, 0, 0,
            BinaryDelta.Hash(original.AsSpan(4, 4)));
        Check(project.RecordObjectEdit(new(ObjectEditKind.PlacementTransform, target, Scale: 3), catalog),
            "The object edit was not saved.");
        // The editor overlay: raw deltas with object edits on top.
        catalog.ReadOverride = value => project.CompileObjectEdits(catalog).Assets.TryGetValue(value, out var compiled)
            ? compiled.ToArray() : project.ContainsEdit(value) ? project.ReadAsset(value, catalog) : null;
        try
        {
            string folder = Path.Combine(root, "object-import-bundle");
            var bundle = AssetBundle.Export(catalog, tag, folder, new(Project: project));
            string path = Path.Combine(folder, bundle.Manifest.NativeAssets.Single(asset => asset.TagHash == tag).Path);
            byte[] exported = File.ReadAllBytes(path);
            Check(BinaryPrimitives.ReadSingleLittleEndian(exported.AsSpan(4)) == 3, "The bundle did not hold the compiled bytes.");
            exported[1] ^= 0x40;
            File.WriteAllBytes(path, exported);
            var prepared = AssetBundle.PrepareImport(project, catalog, bundle.ManifestPath, new(AssetBundleImportMode.ApplyDeltas));
            byte[] expected = original.ToArray(); expected[1] = exported[1];
            Check(prepared.Edits.Single().Value.SequenceEqual(expected), "A bundle import wrote an object edit value into the raw delta.");
            AssetBundle.ApplyImport(project, catalog, prepared);
            byte[] compiled = project.ReadCompiled(tag, catalog);
            Check(project.ReadAsset(tag, catalog).SequenceEqual(expected) && compiled[1] == exported[1]
                && BinaryPrimitives.ReadSingleLittleEndian(compiled.AsSpan(4)) == 3,
                "The imported raw delta and the object edit did not layer.");
            Console.WriteLine("interchange object_edit_import raw=object-free layered=pass");
        }
        finally { catalog.ReadOverride = null; }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    /** Writes Scale as a float at the target offset. */
    private sealed class ScaleCompiler : IObjectEditCompiler
    {
        /** Writes each edit Scale as a float at its target offset. */
        public ObjectEditCompilation Compile(IReadOnlyList<ObjectEdit> edits, Func<uint, byte[]> readRaw, PackageCatalog catalog)
        {
            var assets = new Dictionary<uint, byte[]>();
            foreach (var edit in edits)
            {
                uint tag = edit.Target.SourceTag;
                if (!assets.TryGetValue(tag, out var bytes)) assets[tag] = bytes = readRaw(tag).ToArray();
                if (edit.Scale is float scale) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(edit.Target.Offset), scale);
            }
            return new(assets, edits.Select(edit => new CompiledObjectEdit(edit, [edit.Target.SourceTag])).ToArray());
        }

        /** Keeps every edit unchanged. */
        public ObjectEdit? Normalize(ObjectEdit edit, Func<uint, byte[]> readRaw, PackageCatalog catalog) => edit;
    }
}
