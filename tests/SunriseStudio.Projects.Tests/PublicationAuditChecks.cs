using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

/** Publication succeeds independently of obsolete-file cleanup and preserves dependent edits. */
internal static class PublicationAuditChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(string parent, PackageCatalog catalog, uint tag)
    {
        var failures = new List<string>();
        Case("update-locked-old", () => LockedCleanup(parent, catalog, tag, "update"));
        Case("remove-locked-old", () => LockedCleanup(parent, catalog, tag, "remove"));
        Case("revert-locked-old", () => LockedCleanup(parent, catalog, tag, "revert"));
        Case("failed-update-staging", () => FailedUpdate(parent));
        Case("dependent-raw-revert", () => DependentRevert(parent, catalog, tag));
        Case("case-alias-storage", () => CaseAlias(parent));
        if (failures.Count != 0) throw new InvalidDataException(string.Join("; ", failures));

        void Case(string name, Action test)
        {
            try { test(); Console.WriteLine($"publication_audit case={name} result=pass"); }
            catch (Exception error)
            {
                failures.Add(name + ": " + error.Message);
                Console.WriteLine($"publication_audit case={name} result=fail error=\"{error.Message}\"");
            }
        }
    }

    private static void LockedCleanup(string parent, PackageCatalog catalog, uint tag, string action)
    {
        if (!OperatingSystem.IsWindows()) return;
        var project = StudioProject.Create(Path.Combine(parent, "publication-" + action), action);
        string obsolete;
        AddedProjectAsset? asset = null;
        if (action == "revert")
        {
            byte[] bytes = catalog.ReadOriginal(tag); bytes[0] ^= 1;
            project.ApplyEdit(tag, bytes, catalog);
            obsolete = project.Changes.Single().DeltaPath;
        }
        else
        {
            asset = project.AddAsset("payload", [1, 2]); obsolete = asset.Path;
        }
        string path = Path.Combine(project.DirectoryPath, obsolete);
        Exception? failure = null;
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                if (action == "update") project.UpdateAddedAsset(asset!.Id, [3, 4]);
                else if (action == "remove") project.RemoveAddedAsset(asset!.Id);
                else project.Revert(tag, catalog);
            }
            catch (Exception error) { failure = error; }
            var reopened = StudioProject.Open(project.DirectoryPath);
            bool committed = action switch
            {
                "update" => reopened.ReadAddedAsset(asset!.Id).SequenceEqual(new byte[] { 3, 4 }),
                "remove" => reopened.AddedAssets.Count == 0,
                _ => reopened.Changes.Count == 0
            };
            Check(committed, "The new manifest did not commit.");
            Check(failure is null, "The manifest committed but cleanup reported failure: " + failure?.GetType().Name);
            Check(File.Exists(path), "The locked obsolete storage was not retained.");
        }
    }

    private static void FailedUpdate(string parent)
    {
        if (!OperatingSystem.IsWindows()) return;
        var project = StudioProject.Create(Path.Combine(parent, "publication-failed-update"), "Failed update");
        var asset = project.AddAsset("payload", [1, 2]);
        byte[] manifest = File.ReadAllBytes(project.ManifestPath);
        string[] files = Directory.GetFiles(project.DirectoryPath, "*", SearchOption.AllDirectories).Order().ToArray();
        using (var held = new FileStream(project.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { project.UpdateAddedAsset(asset.Id, [3, 4]); throw new InvalidOperationException("Expected a locked-manifest refusal."); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        Check(File.ReadAllBytes(project.ManifestPath).AsSpan().SequenceEqual(manifest)
            && project.ReadAddedAsset(asset.Id).SequenceEqual(new byte[] { 1, 2 }), "A failed save changed the old state.");
        Check(Directory.GetFiles(project.DirectoryPath, "*", SearchOption.AllDirectories).Order().SequenceEqual(files),
            "A failed update left an unreferenced payload.");
    }

    private static void DependentRevert(string parent, PackageCatalog catalog, uint tag)
    {
        var project = StudioProject.Create(Path.Combine(parent, "publication-dependent-revert"), "Dependent edit");
        var original = catalog.ReadOriginal(tag);
        var raw = original.ToArray(); raw[0] = 0x11;
        project.ApplyEdit(tag, raw, catalog);
        project.ObjectEditCompiler = new DependencyCompiler();
        var target = new ObjectEditTarget(tag, ObjectEditLayout.StaticInstance, 4, 4, 0, 0,
            BinaryDelta.Hash(original.AsSpan(4, 4)));
        project.RecordObjectEdit(new(ObjectEditKind.PlacementTransform, target, Scale: 2), catalog);
        byte[] manifest = File.ReadAllBytes(project.ManifestPath);
        Reject<InvalidDataException>(() => project.Revert(tag, catalog));
        Check(File.ReadAllBytes(project.ManifestPath).AsSpan().SequenceEqual(manifest)
            && project.ReadAsset(tag, catalog)[0] == 0x11, "Refused revert changed saved raw data.");
        project.RevertObjectEdit(target.Key); project.Revert(tag, catalog);
        Check(project.Changes.Count == 0, "Removing the dependent edit did not permit revert.");
    }

    private static void CaseAlias(string parent)
    {
        if (!OperatingSystem.IsWindows()) return;
        var project = StudioProject.Create(Path.Combine(parent, "publication-case-path"), "Case paths");
        var first = project.AddAsset("first", [1, 2]);
        var second = project.AddAsset("second", [1, 2]);
        var json = JsonNode.Parse(File.ReadAllBytes(project.ManifestPath))!;
        json["addedAssets"]![1]!["path"] = "assets/" + Path.GetFileName(first.Path).ToUpperInvariant();
        File.WriteAllText(project.ManifestPath, json.ToJsonString());
        project = StudioProject.Open(project.DirectoryPath);
        project.RemoveAddedAsset(first.Id);
        Check(project.ReadAddedAsset(second.Id).SequenceEqual(new byte[] { 1, 2 }), "Cleanup deleted storage still used through a case alias.");
    }

    private sealed class DependencyCompiler : IObjectEditCompiler
    {
        /** Copies each edit source bytes after requiring the saved raw dependency. */
        public ObjectEditCompilation Compile(IReadOnlyList<ObjectEdit> edits, Func<uint, byte[]> readRaw, PackageCatalog catalog)
        {
            var result = new Dictionary<uint, byte[]>();
            foreach (var edit in edits)
            {
                byte[] bytes = readRaw(edit.Target.SourceTag);
                if (bytes[0] != 0x11) throw new InvalidDataException("This object edit requires the saved raw dependency.");
                result.Add(edit.Target.SourceTag, bytes);
            }
            return new(result, edits.Select(edit => new CompiledObjectEdit(edit, [edit.Target.SourceTag])).ToArray());
        }
        /** Keeps every edit unchanged. */
        public ObjectEdit? Normalize(ObjectEdit edit, Func<uint, byte[]> readRaw, PackageCatalog catalog) => edit;
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }
}
