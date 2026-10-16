using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Animation;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Cloth and bone-attached effects of the Centurion follow its animation pose. */
internal static class ClothAnimationChecks
{
    private const uint Centurion = 0x80C19B1F, Clip = 0x80C19B62;
    private const int Width = 320, Height = 240;

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string gameRoot)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        var source = AssetAnimationSource.Prepare(catalog, Centurion);
        Cloth(catalog, source, new AssetSceneLoader(catalog).Load(Centurion));
        Effects(source, new AssetSceneLoader(catalog).Load(Centurion));
    }

    /** Game-shader cloth draws where the plain cloth does, in bind pose and under a clip. */
    private static void Cloth(PackageCatalog catalog, AssetAnimationSource source, SceneDocument scene)
    {
        var plan = AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: 128L * 1024 * 1024, assetPreview: true);
        var groups = plan.Groups.Where(group => group.ClothDisplayStream is not null).ToArray();
        Require(groups.Length >= 2 && groups.All(group => group.Mesh.NativeDeclarationId == 18 && group.Material.Palette == NativeEntityPalette.None),
            $"Expected two cloth display groups, found {groups.Length}.");
        float bindError = 0;
        foreach (var group in groups)
        {
            var lanes = MemoryMarshal.Cast<byte, Vector4>(group.ClothDisplayStream);
            foreach (var part in group.Parts)
                for (int i = 0; i < part.Instance.Mesh.Vertices.Length; i++)
                {
                    var stream = lanes[(int)part.Instance.Mesh.SourceVertexIndices[i] * 3];
                    bindError = MathF.Max(bindError, Vector3.Distance(new(stream.X, stream.Y, stream.Z),
                        part.Instance.Mesh.Vertices[i].Position));
                }
        }
        Require(bindError < 1e-5f, $"The cloth bind stream is {bindError} from the decoded mesh.");
        var cloth = groups.SelectMany(group => group.Parts.Select(part => part.Instance)).ToHashSet();
        using var renderer = new Dx11Renderer(IntPtr.Zero, Width, Height, true);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        viewport.SetScene(scene);
        viewport.SetNativeScene(plan);
        viewport.SetAnimation(source);
        // The cloth and its swing room fill the frame, so a misplaced cloth misses by many pixels.
        var around = cloth.SelectMany(instance => instance.Mesh.Vertices.Select(v => Vector3.Transform(v.Position, instance.Transform))).ToArray();
        viewport.Camera.Frame(around.Aggregate(Vector3.Min) - new Vector3(.75f), around.Aggregate(Vector3.Max) + new Vector3(.75f),
            (float)Width / Height);
        var native = viewport.NativeRenderer!;
        native.AutoExposure = false;
        var animation = viewport.Animation!;
        Require(animation.ClothDisplayCount > 0, "No cloth display stream was bound to its skin weights.");
        var points = Enumerable.Range(0, Height).SelectMany(y => Enumerable.Range(0, Width).Select(x => (x, y))).ToArray();
        var bindNative = Bounds(viewport, renderer, points, cloth, true);
        var bindPlain = Bounds(viewport, renderer, points, cloth, false);
        Require(Matches(bindNative, bindPlain), $"Bind-pose cloth draws at {bindNative} with game shaders and {bindPlain} plain.");
        animation.SetClip(source.ReadClip(Clip), false);
        animation.Seek(0);
        var header = groups[0].Mesh.VertexStreams[0].HeaderTag;
        var first = animation.ClothStreamFor(header)!.Value.ToArray();
        var startNative = Bounds(viewport, renderer, points, cloth, true);
        animation.Seek(.7f);
        Require(animation.Error is null, animation.Error ?? "Clip evaluation failed.");
        var later = animation.ClothStreamFor(header)!.Value.ToArray();
        Require(!first.AsSpan().SequenceEqual(later), "The posed cloth stream did not change under the clip.");
        Require(native.PosedClothStreams > 0, "The renderer did not write a posed cloth stream.");
        RequirePlainMatch(source, animation, groups, header, later);
        var laterNative = Bounds(viewport, renderer, points, cloth, true);
        var laterPlain = Bounds(viewport, renderer, points, cloth, false);
        Require(Matches(laterNative, laterPlain), $"Posed cloth draws at {laterNative} with game shaders and {laterPlain} plain.");
        Require(laterNative != startNative, "The game-shader cloth did not move under the clip.");
        Console.WriteLine($"cloth_animation asset={Centurion:X8} groups={groups.Length} bind_error={bindError:0.######} "
            + $"bind={bindNative} posed={laterNative} plain={laterPlain} result=pass");
    }

    /** The posed stream matches the plain path's skin of the decoded vertices. */
    private static void RequirePlainMatch(AssetAnimationSource source, AssetAnimationPreview animation,
        IReadOnlyList<NativeSceneGroup> groups, uint header, byte[] posed)
    {
        var binding = source.SkinBindings.Single(x => x.PositionHeaderTag == header && x.PaletteKind == SkinPaletteKind.ClothInput);
        var palette = animation.Pose.ClothInputSkinMatrices;
        var lanes = MemoryMarshal.Cast<byte, Vector4>(posed);
        float error = 0;
        foreach (var part in groups.Where(g => g.Mesh.VertexStreams[0].HeaderTag == header).SelectMany(g => g.Parts))
        {
            var mesh = part.Instance.Mesh;
            for (int i = 0; i < mesh.Vertices.Length; i++)
            {
                int index = (int)mesh.SourceVertexIndices[i];
                var weights = binding.Vertices[index];
                var expected = Vector3.Zero;
                for (int lane = 0; lane < 4; lane++)
                {
                    int bone = lane switch { 0 => weights.Bone0, 1 => weights.Bone1, 2 => weights.Bone2, _ => weights.Bone3 };
                    if (weights.Weights[lane] > 0) expected += Vector3.Transform(mesh.Vertices[i].Position, palette[bone]) * weights.Weights[lane];
                }
                var actual = lanes[index * 3];
                error = MathF.Max(error, Vector3.Distance(expected, new(actual.X, actual.Y, actual.Z)));
            }
        }
        Require(error < 1e-4f, $"The posed cloth stream is {error} from the plain skin.");
    }

    /** Pixel box of the cloth: frames with and without it shown differ only there. */
    private static (int X0, int Y0, int X1, int Y1) Bounds(AssetViewport viewport, Dx11Renderer renderer,
        (int x, int y)[] points, HashSet<MeshInstance> cloth, bool gameShaders)
    {
        viewport.UseGameShaders = gameShaders;
        Vector4[] Frame()
        {
            viewport.RenderImage(Width, Height);
            return renderer.ReadTexturePixels(viewport.RenderImage(Width, Height), points);
        }
        viewport.Visibility.Filter = item => item is MeshInstance instance && cloth.Contains(instance);
        var shown = Frame();
        viewport.Visibility.Filter = _ => false;
        var hidden = Frame();
        viewport.Visibility.Filter = null;
        Require(viewport.UseGameShaders == gameShaders, viewport.NativeError ?? "Game shaders were turned off.");
        int x0 = Width, y0 = Height, x1 = -1, y1 = -1;
        for (int i = 0; i < points.Length; i++)
        {
            var d = shown[i] - hidden[i];
            if (MathF.Abs(d.X) + MathF.Abs(d.Y) + MathF.Abs(d.Z) < .02f) continue;
            var (x, y) = points[i];
            x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
        }
        Require(x1 >= 0, $"The cloth drew no pixel with game shaders {(gameShaders ? "on" : "off")}.");
        return (x0, y0, x1, y1);
    }

    // Box edges may differ by this many pixels between shaders; unscaled cloth misses by more.
    private const int EdgeTolerance = 6;

    private static bool Matches((int X0, int Y0, int X1, int Y1) a, (int X0, int Y0, int X1, int Y1) b) =>
        Math.Abs(a.X0 - b.X0) <= EdgeTolerance && Math.Abs(a.Y0 - b.Y0) <= EdgeTolerance
        && Math.Abs(a.X1 - b.X1) <= EdgeTolerance && Math.Abs(a.Y1 - b.Y1) <= EdgeTolerance;

    /**
     * Each emitter spawns at its own posed spawn slot and returns to the bind frame on detach.
     * A world-space simulation frame stays the world while the bone moves.
     */
    private static void Effects(AssetAnimationSource source, SceneDocument scene)
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, Width, Height, true);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        viewport.SetScene(scene);
        viewport.SetAnimation(source);
        var animation = viewport.Animation!;
        animation.SetClip(source.ReadClip(Clip), false);
        animation.Seek(0);
        viewport.RenderImage(Width, Height);
        static EffectFrame? Spawn(EffectEmitterSetup e) => e.SpawnSlot < e.Frames.Count ? e.Frames[e.SpawnSlot] : null;
        var attached = viewport.EffectEmitters.Where(e => Spawn(e) is { Bone: >= 0 }).ToArray();
        Require(attached.Length > 0, "The Centurion has no bone-attached emitter.");
        var world = attached.Where(e => e.Simulation is not null
            && e.Frames.ElementAtOrDefault(e.Marker.Effect!.Systems[e.System].Emitter!.EmitterSlot) is { World: true }).ToArray();
        Require(world.Length > 0, "The Centurion has no bone-spawned emitter with a world simulation frame.");
        Vector3[] Check(Func<EffectEmitterSetup, Matrix4x4> bone, string state)
        {
            var origins = attached.Select(e => e.Origin).ToArray();
            for (int i = 0; i < attached.Length; i++)
            {
                var e = attached[i];
                var expected = (Spawn(e)!.Local * bone(e) * e.Marker.Effect!.Placement).Translation;
                Require(Vector3.Distance(origins[i], expected) < 1e-4f, $"Emitter {i} is at {origins[i]} {state}, expected {expected}.");
            }
            foreach (var e in world)
            {
                var frame = e.Simulation!.Transform;
                Require(Vector3.Distance(frame.Translation, Vector3.Zero) < 1e-4f
                        && Vector3.Distance(Vector3.TransformNormal(Vector3.UnitX, frame), Vector3.UnitX) < 1e-4f,
                    $"A world-space simulation frame moved {state}: {frame}.");
            }
            return origins;
        }
        Matrix4x4 Posed(EffectEmitterSetup e) => animation.Pose.ObjectTransforms[Spawn(e)!.Bone];
        var start = Check(Posed, "at 0 s");
        animation.Seek(.7f);
        viewport.RenderImage(Width, Height);
        var later = Check(Posed, "at 0.7 s");
        float moved = start.Zip(later, Vector3.Distance).Max();
        Require(moved > 1e-3f, "Bone-attached emitters did not move under the clip.");
        viewport.DetachAnimation();
        viewport.RenderImage(Width, Height);
        Check(e => Spawn(e)!.BoneTransform, "after detach");
        Console.WriteLine($"effect_attachment asset={Centurion:X8} emitters={attached.Length} world={world.Length} "
            + $"moved={moved:0.###} detach=bind result=pass");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
