using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using SunriseStudio.Authoring;
using SunriseStudio.Editor;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;

/** Checks static prop file inspection: malformed glTF reports reach the creation panel, refresh when the file changes, and are not recomputed every frame. */
internal static class StaticPropInspectionChecks
{
    /** Replacing either exported file refreshes the report; malformed user input stays a UI error. */
    public static void Run(string root, StudioWorkspace workspace)
    {
        string path = Path.Combine(root, "original-inspection.gltf");
        string sidecar = Path.ChangeExtension(path, null) + OriginalAssetImporter.SidecarSuffix;
        var workflow = new StaticPropWorkflow(workspace);
        workflow.Fields.Single(field => field.Label == "Source").Text = "1";
        workflow.Fields.Single(field => field.Label == "Mesh file").Text = path;
        File.WriteAllText(path, "{"); Update();
        Check(Inspection() is null && Error().Length > 0, "Malformed glTF escaped the creation report.");
        GltfInterchange.Write(path, [new InterchangeMesh("Triangle", [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0, 1, 2])]);
        Update();
        var first = Inspection();
        Check(first?.CanImportAll == true && Error().Length == 0, "Replacing the invalid file did not refresh its report.");
        workflow.Update(workspace);
        Check(!workspace.IsBusy && ReferenceEquals(first, Inspection()), "An unchanged file was re-inspected every frame.");
        File.WriteAllText(sidecar, "{"); Update();
        Check(Inspection() is null && Error().Length > 0, "A new malformed sidecar left the old report active.");
        File.WriteAllText(sidecar, $$"""{"format":"sunrise-original-asset","version":1,"scene":"{{Path.GetFileName(path)}}","emitters":[]}""");
        Update();
        Check(Inspection()?.CanImportAll == true && Error().Length == 0, "A corrected sidecar retained its prior error.");
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["materials"] = JsonNode.Parse("""[{"alphaMode":"BLEND"}]""");
        json["meshes"]![0]!["primitives"]![0]!["material"] = 0;
        File.WriteAllText(path, json.ToJsonString()); Update();
        Check(Inspection()!.Surfaces.Single(surface => surface.Name == "Materials") is { Present: true, Supported: false },
            "Replacing the same scene with unsupported alpha kept a supported report.");
        File.Delete(sidecar);
        File.WriteAllText(path, """{"asset":{"version":"2.0"},"scene":2147483648,"scenes":[{"nodes":[]}],"nodes":[]}""");
        Update();
        Check(Inspection() is null && Error().Length > 0, "An overflowing JSON integer escaped the creation report.");
        File.Delete(path); Update();
        Check(Inspection() is null && Error().Length == 0, "Removing a file retained stale inspection data.");
        Console.WriteLine("static_prop_inspection same_path=refreshed sidecar=refreshed malformed=contained overflow=contained unchanged=cached");

        // The read runs on the workspace worker; its result lands when the worker is polled.
        void Update()
        {
            workflow.Update(workspace);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (workspace.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(20)) { workspace.Poll(); Thread.Sleep(1); }
            workspace.Poll();
            Check(!workspace.IsBusy, "The original asset read did not finish.");
        }
        object? Field(string name) => typeof(StaticPropWorkflow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workflow);
        OriginalAssetInspection? Inspection() => (OriginalAssetInspection?)Field("inspection");
        string Error() => (string)Field("inspectionError")!;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
