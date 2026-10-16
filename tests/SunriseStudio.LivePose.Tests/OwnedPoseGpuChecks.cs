using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Animation;
using SunriseStudio.Editor;
using SunriseStudio.Live;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;
using Vortice.DXGI;

/** GPU check of posed original models: admitted parts, captured lighting, and exclusion of unsupported native parts. */
internal static class OwnedPoseGpuChecks
{
    private const int Width = 192, Height = 160;
    private static readonly (int X, int Y)[] Pixels = Enumerable.Range(0, Height)
        .SelectMany(y => Enumerable.Range(0, Width).Select(x => (x, y))).ToArray();

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(PackageCatalog catalog)
    {
        foreach (var fixture in new[] { (Entity: 0x80C0FA98u, Model: 0x80FEE881u, Kind: LivePaletteKind.DualQuaternion),
            (Entity: 0x80B82FE3u, Model: 0x80C4424Fu, Kind: LivePaletteKind.Matrix3x4) })
            foreach (bool warp in new[] { false, true }) Run(catalog, fixture.Entity, fixture.Model, fixture.Kind, warp);
    }

    /** Owned shaders and decoded world vertices must agree on coverage and depth for the same rows. */
    private static void Run(PackageCatalog catalog, uint entity, uint model, LivePaletteKind kind, bool warp)
    {
        var scene = new AssetSceneLoader(catalog) { IncludeLowerLods = false }.Load(entity);
        var rig = new SkeletonReader(catalog).Read(entity);
        var binding = LivePoseModelBinding.Prepare(catalog, entity, model);
        var plan = AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: long.MaxValue, assetPreview: true);
        using var renderer = new Dx11Renderer(IntPtr.Zero, Width, Height, warp);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false, UseGameShaders = true };
        viewport.SetScene(scene); viewport.SetNativeScene(plan);
        var native = viewport.NativeRenderer!;
        NativeEntityPalette paletteKind = kind == LivePaletteKind.Matrix3x4 ? NativeEntityPalette.Matrix : NativeEntityPalette.DualQuaternion;
        var part = plan.Groups.Where(group => group.RenderStage == 0 && group.Material.Palette == paletteKind)
            .SelectMany(group => group.Parts).Select(part => part.Instance).Distinct()
            .Where(instance => instance.Mesh.TagHash == model && native.CanApplyLivePalette(instance, paletteKind))
            .OrderByDescending(instance => instance.Mesh.Indices.Length).FirstOrDefault()
            ?? throw new InvalidOperationException($"No admitted {kind} part for {entity:X8}/{model:X8}.");
        viewport.Visibility.Filter = item => ReferenceEquals(item, part);
        var sourceVertices = part.Mesh.Vertices.ToArray(); var sourceTransform = part.Transform;
        byte[] header = part.Mesh.Native!.SourceHeader.ToArray();
        Vector4[] fixedRows = [Vector(header, 0x60), Vector(header, 0x70), new(.2f, .3f, .37f, .18f)];
        var rows = new List<Vector4>();
        for (int bone = 0; bone < rig.Bones.Count; bone++)
        {
            var rotation = Quaternion.CreateFromYawPitchRoll(.09f * MathF.Sin(bone), .13f * MathF.Cos(bone), .07f * (bone % 3));
            var translation = new Vector3(1.2f + bone % 5 * .027f, -.7f + MathF.Sin(bone) * .11f, .4f + bone % 3 * .031f);
            if (kind == LivePaletteKind.Matrix3x4)
            {
                var matrix = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
                rows.Add(new(matrix.M11, matrix.M21, matrix.M31, matrix.M41));
                rows.Add(new(matrix.M12, matrix.M22, matrix.M32, matrix.M42));
                rows.Add(new(matrix.M13, matrix.M23, matrix.M33, matrix.M43));
            }
            else
            {
                var dual = Quaternion.Multiply(new Quaternion(translation, 0), rotation);
                rows.Add(new(rotation.X, rotation.Y, rotation.Z, rotation.W));
                rows.Add(new Vector4(dual.X, dual.Y, dual.Z, dual.W) * .5f);
            }
        }
        var pose = new LiveDraw(1000, 42, 1, model, kind, rig.Bones.Count, rig.Bones.Count, fixedRows, rows, 7, 11);
        var draw = binding.Build(pose, [part], native);
        Check(draw.Palettes[part].Rows[7] == fixedRows[2], "Captured lighting vector was replaced by a default.");
        var modelParts = scene.Instances.Where(instance => instance.Mesh.TagHash == model).ToArray();
        var originalModel = modelParts.ToDictionary(instance => instance,
            instance => (Vertices: instance.Mesh.Vertices.ToArray(), instance.Transform));
        var full = binding.Build(pose, scene.Instances, native);
        Check(full.TotalParts == modelParts.Length && full.Geometry.Count == full.Palettes.Count
            && full.Geometry.Keys.ToHashSet().SetEquals(full.Palettes.Keys)
            && full.Geometry.Count + full.SkippedReasons.Values.Sum() == full.TotalParts,
            "Full-model admission lost or duplicated a part.");
        Check(full.SkippedReasons.All(reason => reason.Key.Length > 0 && reason.Value > 0)
            && full.Geometry.Keys.All(instance => modelParts.Contains(instance) && native.CanApplyLivePalette(instance, paletteKind)),
            "Full-model output contains an unrelated or unsupported native part.");
        var individualReasons = new Dictionary<string, int>();
        foreach (var instance in modelParts)
        {
            try
            {
                var one = binding.Build(pose, [instance], native);
                Check(full.Geometry.ContainsKey(instance) && one.Geometry.Count == 1, "An admitted individual part disappeared from the complete model.");
            }
            catch (NotSupportedException error)
            {
                Check(!full.Geometry.ContainsKey(instance) && !full.Palettes.ContainsKey(instance), "A skipped part entered live geometry or constants.");
                individualReasons[error.Message] = individualReasons.GetValueOrDefault(error.Message) + 1;
            }
        }
        Check(individualReasons.Count == full.SkippedReasons.Count && individualReasons.All(reason =>
            full.SkippedReasons.GetValueOrDefault(reason.Key) == reason.Value), "Full-model skip reasons disagree with individual part admission.");
        var bounds = SelectionBounds.Empty;
        foreach (var vertex in draw.Geometry[part]) bounds = bounds.Include(vertex.Position);
        viewport.Camera.Frame(bounds.Minimum, bounds.Maximum, Width / (float)Height);
        viewport.Camera.LookFrom(bounds.Center + Vector3.Normalize(new Vector3(1, -2, .6f)) * Math.Max(.5f, bounds.Radius * 3), bounds.Center);
        viewport.RenderImage(Width, Height);
        var baseline = renderer.ReadNativeDepthPixels(native.Targets, Pixels);
        native.SetLivePalettes(draw.Palettes); viewport.SetLivePoseGeometry(draw.Geometry);
        viewport.RenderImage(Width, Height);
        Check(viewport.UseGameShaders && native.LivePaletteCount == 1, viewport.NativeError ?? "Captured native palette was not drawn.");
        var actual = renderer.ReadNativeDepthPixels(native.Targets, Pixels);
        using var diagnostic = renderer.CreateNativeTargets(Width, Height, [Format.R8G8B8A8_UNorm], Format.D32_Float, true);
        using var mesh = renderer.CreateMesh(draw.Geometry[part], part.Mesh.Indices);
        renderer.BeginOffscreen(diagnostic, Vector4.Zero);
        renderer.DrawMesh(mesh, Matrix4x4.Identity, viewport.Camera.ViewProjection(Width / (float)Height), Vector4.One);
        renderer.RestoreMainTarget();
        var expected = renderer.ReadNativeDepthPixels(diagnostic, Pixels);
        int nativeCoverage = actual.Count(pixel => pixel.X < .99999f), coverage = expected.Count(pixel => pixel.X < .99999f);
        int common = 0, mismatches = 0, changed = 0;
        float maximumDepthError = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            bool a = actual[i].X < .99999f, b = expected[i].X < .99999f;
            if (a != b) mismatches++;
            if (a && b) { common++; maximumDepthError = Math.Max(maximumDepthError, Math.Abs(actual[i].X - expected[i].X)); }
            if (Math.Abs(actual[i].X - baseline[i].X) > 1e-5f) changed++;
        }
        Check(common > 40 && changed > 40, "Owned pose fixture did not cover or move enough pixels.");
        Check(mismatches <= Math.Max(12, coverage / 50) && maximumDepthError < 2e-4f,
            $"Native/world pose differs: kind={kind} native={nativeCoverage} expected={coverage} mismatch={mismatches} depth={maximumDepthError:G8}.");
        native.ClearLivePalettes(); viewport.ClearLivePoseGeometry(); viewport.RenderImage(Width, Height);
        var restored = renderer.ReadNativeDepthPixels(native.Targets, Pixels);
        Check(restored.SequenceEqual(baseline), "Clearing captured rows did not restore original native geometry.");
        Check(part.Mesh.Vertices.SequenceEqual(sourceVertices) && part.Transform == sourceTransform
            && part.Mesh.Native.SourceHeader.SequenceEqual(header), "Pose binding changed source vertices, placement, or fixed rows.");
        native.SetLivePalettes(full.Palettes); viewport.SetLivePoseGeometry(full.Geometry);
        Check(native.LivePaletteCount == full.Geometry.Count && viewport.LivePoseGeometryCount == full.Geometry.Count
            && viewport.LivePrototypeCount == 0, "Renderer replacement fixture did not use a complete authored model.");
        int revision = viewport.LivePrototypeRevision;
        viewport.SetNativeScene(AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: long.MaxValue, assetPreview: true));
        var replacement = viewport.NativeRenderer!;
        Check(!ReferenceEquals(native, replacement) && native.LivePaletteCount == 0 && replacement.LivePaletteCount == 0
            && viewport.LivePoseGeometryCount == 0 && viewport.LivePrototypeRevision > revision,
            "Replacing an authored-only native renderer retained a pose or its generation.");
        viewport.RenderImage(Width, Height);
        Check(renderer.ReadNativeDepthPixels(replacement.Targets, Pixels).SequenceEqual(baseline), "Renderer replacement retained posed pixels.");
        var reapplied = binding.Build(pose, [part], replacement);
        replacement.SetLivePalettes(reapplied.Palettes); viewport.SetLivePoseGeometry(reapplied.Geometry);
        viewport.RenderImage(Width, Height);
        Check(renderer.ReadNativeDepthPixels(replacement.Targets, Pixels).SequenceEqual(actual), "The same captured pose could not reapply after renderer replacement.");
        Check(originalModel.All(original => original.Key.Transform == original.Value.Transform
            && original.Key.Mesh.Vertices.SequenceEqual(original.Value.Vertices)), "Full-model pose admission changed an authored part.");
        Console.WriteLine($"live_pose_owned adapter={(warp ? "warp" : "hardware")} kind={kind} entity={entity:X8} model={model:X8} part={part.Mesh.PartIndex:X} coverage={coverage} mismatches={mismatches} depth_error={maximumDepthError:G8} fixed_lighting=preserved clear=pass");
        Console.WriteLine($"live_pose_model adapter={(warp ? "warp" : "hardware")} model={model:X8} applied={full.Geometry.Count} total={full.TotalParts} skipped={System.Text.Json.JsonSerializer.Serialize(full.SkippedReasons)} renderer_replace=cleared same_snapshot=reapplied");
    }

    private static Vector4 Vector(byte[] bytes, int offset) => new(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset)),
        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 4)), BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 8)),
        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 12)));
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}
