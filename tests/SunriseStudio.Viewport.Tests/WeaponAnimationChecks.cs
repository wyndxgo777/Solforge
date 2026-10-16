using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;
using SunriseStudio.Animation;

/** Checks weapon animation palettes: non-identity inverse binds, first-person skin suffix handling. */
internal static class WeaponAnimationChecks
{
    /** Nonidentity weapon binds must not inherit the parent's first-person inverse bind. */
    public static void Math()
    {
        var weaponBind = Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(3, 0, 0);
        Check(Matrix4x4.Invert(weaponBind, out var inverse), "The fixture bind was singular.");
        var frames = Enumerable.Repeat(Matrix4x4.Identity, 77).ToArray();
        frames[72] = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(10, 4, 0);
        var palette = WeaponAnimationBinding.Compose([inverse], frames, 72);
        Vector3 point = Vector3.Transform(new Vector3(5, 0, 0), palette[0]);
        Check(Vector3.Distance(point, new Vector3(10, 5, 0)) < .00001f,
            "The weapon palette did not use its own nonidentity inverse bind.");
        Matrix4x4.Invert(weaponBind * Matrix4x4.CreateTranslation(20, 0, 0), out var parentInverse);
        var wrong = Vector3.Transform(new Vector3(5, 0, 0), parentInverse * frames[72]);
        Check(Vector3.Distance(point, wrong) > 9, "The fixture did not distinguish a first-person skin suffix.");
        Reject(() => WeaponAnimationBinding.Compose([inverse, inverse], frames, 76));
        Console.WriteLine("weapon_animation_palette nonidentity_bind=pass parent_inverse=rejected");
    }

    /** The installed rigs retain the native frame partition under a sampled reload pose. */
    public static void Owned(string install)
    {
        using var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        var weapon = new SkeletonReader(catalog).Read(0x81A6B325);
        var source = AssetAnimationSource.Prepare(catalog, 0x80BC1002);
        var binding = new WeaponAnimationBinding(weapon, source.Rig);
        var pose = source.Rig.CreateBindPose();
        var clip = source.ReadClip(source.Clips.First(row => row.Index == 113).ClipTag);
        source.Binding!.ApplyPreview(clip, clip.DurationSeconds * .45f, pose);
        var result = weapon.CreateBindPose();
        binding.Apply(pose, result);
        var expected = WeaponAnimationBinding.Compose(weapon.Bones.Select(bone => bone.InverseBindObject.Matrix).ToArray(),
            pose.ObjectTransforms, 72);
        for (int index = 0; index < 5; index++)
            Check(Vector3.Distance(Vector3.Transform(Vector3.One, expected[index]),
                Vector3.Transform(Vector3.One, result.SkinMatrices[index])) < .0001f,
                "The installed weapon frame composition differed.");
        Console.WriteLine("weapon_animation_rigs owned=pass weapon_bones=5 presentation_bones=77");
    }

    /** Tommy's Matchbook mixes declarations 137 and 139 and has a reticle; every drawn part must skin. */
    public static void Installed(string install)
    {
        using var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        var prepared = WeaponAnimationPreviewSource.Prepare(catalog, 0x81A6B325, 0x80BC1002,
            [0x81A6B357, 0x81A6B3A5, 0x81A6B3EC, 0x81A6B42E]);
        var natives = prepared.Scene.Instances.Select(row => row.Mesh.Native!).ToArray();
        Check(natives.All(native => NativeScenePlan.DrawnStages.Contains(native.RenderStage)),
            "A part in an undrawn stage, such as the reticle, entered the weapon scene.");
        Check(natives.Any(native => native.NativeDeclarationId == 137) && prepared.Animation?.RigidSkins.Count == 0,
            "The declaration 137 part was not bound through its checked palette index.");
        Prepared(prepared);
        var source = prepared.Animation!;
        var rigidSkin = source.SkinBindings[0];
        var rigid = AssetAnimationSource.ForWeapon(source, source.SkinBindings, source.WeaponBinding!,
            new Dictionary<ModelSkinBinding, int>(ReferenceEqualityComparer.Instance) { [rigidSkin] = 14 });
        using (var preview = new AssetAnimationPreview(rigid, prepared.Scene, (_, _) => { }, () => { }))
        {
            var instance = prepared.Scene.Instances.First(row => row.Mesh.TagHash == rigidSkin.ModelTag
                && rigidSkin.PartOffsets.Contains(row.Mesh.PartIndex));
            var palette = preview.SkinFor(instance)!;
            Check(palette.Count == 14 && palette.All(matrix => matrix == preview.DisplayPose.SkinMatrices[0]),
                "A rigid weapon part did not read weapon bone 0 at every palette entry.");
        }
        var dyeBits = prepared.Scene.Instances.Where(row => row.Mesh.TagHash == 0x81A6B350)
            .ToDictionary(row => row.Mesh.PartIndex, NativeGearDye.PartBit);
        Check(dyeBits[352] == 1 && dyeBits[480] == 0, "The gear-dye bit is not part byte 0x1A bit 0.");
        var parameters = new GameMaterialReader(catalog).ReadTfxParameterDefaults(NativeScenePlan.RenderGlobalsTag)
            .CreateSnapshot(null);
        var inputs = NativeScenePlan.MaterialInputs(0, parameters, exposureEv: 1.5f);
        Check(inputs.Floats[new(1, 8)] == 1.5f && inputs.Floats[new(1, 7)] == MathF.Pow(2, 1.5f)
            && inputs.Vectors[NativeGearDye.Key] == Vector4.Zero, "The exposure or gear-dye externs are wrong.");
        Console.WriteLine($"weapon_animation_installed parts={prepared.Scene.Instances.Count} reticle=left_out decl137=skinned gear_dye=pass");
        GearPlating(catalog, prepared.Scene);
        FourBoneWeapon(catalog);
        SurveyInputs(catalog, parameters);
    }

    /** Shader inputs: empty texture rows, view row 13, scene depth, target width and rounding opcodes. */
    private static void SurveyInputs(PackageCatalog catalog, TfxParameterLookup parameters)
    {
        var reader = new GameMaterialReader(catalog);
        // Rows with tag 0xFFFFFFFF bind a null view; the dye scope of 8152BB00 binds only t3 and t4.
        Check(reader.Read(0x8152BDB4).EmptyTextureSlots.SetEquals([(GameShaderStage.Pixel, 3u)])
            && reader.Read(0x8152BB00).EmptyTextureSlots.SetEquals([(GameShaderStage.Pixel, 5u)]),
            "The empty texture rows of 8152BDB4 and 8152BB00 were not read.");
        // 80C68E4C discards on CB12 row 13 Y, which is 1 in camera mode 0.
        var ps = reader.ReadExecution(0x80C68E4C).Material.PixelShader!;
        Check(NativeShaderContracts.CanUseKnownPixelView(ps) && NativeShaderContracts.MissingWords(
            reader.ReadShaderBytecode(ps), new(GameShaderStage.Pixel, 12), NativeViewInputs.UnknownPixelWords) is null,
            "The pixel view buffer does not cover 80C68E4C's row 13 read.");
        var camera = NativeViewInputs.EvaluateCameraScope(reader.ReadScopeExecution(0x80B356A5), 64, 32,
            Matrix4x4.CreateLookAt(new(0, -5, 0), Vector3.Zero, Vector3.UnitZ),
            Matrix4x4.CreatePerspectiveFieldOfView(1, 2, .1f, 100));
        Check(camera.PixelRows.Length == 14 && camera.PixelPass == new Vector4(1, 1, 0, 0), "PS view row 13 is wrong.");
        // 80EF2134 binds extern 3 resource 7 at t5; 8152AD17 reads extern 2 float 0, the target width.
        var cameraInputs = NativeViewInputs.CreateCameraInputs(64, 32, Matrix4x4.Identity, Matrix4x4.Identity);
        var depth = TfxEvaluator.Evaluate(reader.ReadExecution(0x80EF2134).Pixel, NativeScenePlan.MaterialInputs(0, parameters, cameraInputs));
        var width = TfxEvaluator.Evaluate(reader.ReadExecution(0x8152AD17).Pixel, NativeScenePlan.MaterialInputs(0, parameters, cameraInputs));
        Check(depth.IsComplete && depth.ResourceBindings[new(GameShaderStage.Pixel, 5)] == NativeScenePlan.SceneDepth
            && width.IsComplete, "The depth copy or the target width did not reach TFX.");
        // Opcode 0x18 is ceiling and 0x19 rounds ties to even; magnitudes from 2^23 pass through.
        static Vector4 Unary(byte opcode, Vector4 value) => TfxEvaluator.Evaluate(new MaterialStageData(GameShaderStage.Pixel,
            null, [0x34, 0x00, opcode, 0x43, 0x00], [value], [Vector4.Zero], 0, []), new TfxInputs()).Constants[0];
        Check(Unary(0x18, new(1.2f, -1.2f, 3, 1e9f)) == new Vector4(2, -1, 3, 1e9f)
            && Unary(0x19, new(2.5f, -1.5f, .5f, 1e9f)) == new Vector4(2, -2, 0, 1e9f), "TFX opcodes 0x18 or 0x19 are wrong.");
        Console.WriteLine("survey_inputs empty_rows=null pixel_row13=pass extern3_7=depth extern2_0=width tfx_18_19=pass");
    }

    /** The Wizened Rebuke's four weapon frames follow the 72-bone player partition. */
    private static void FourBoneWeapon(PackageCatalog catalog)
    {
        var prepared = WeaponAnimationPreviewSource.Prepare(catalog, 0x80BBAA74, 0x80BBAA64,
            [0x80BBA4D5, 0x80BBA5D5, 0x80BBA657, 0x80BBA7D7, 0x80BBA91A, 0x80BBA98E]);
        var animation = prepared.Animation ?? throw new InvalidDataException(
            "The four-bone weapon did not bind: " + string.Join(" ", prepared.Scene.Diagnostics));
        Check(animation.WeaponBinding is { BoneCount: 4, FirstBone: 72 }, "The four-bone partition was not found at 72.");
        var positions = new Dictionary<MeshData, Vector3[]>();
        using var preview = new AssetAnimationPreview(animation, prepared.Scene,
            (mesh, vertices) => positions[mesh] = vertices.Select(vertex => vertex.Position).ToArray(), () => { });
        float moved = 0;
        foreach (uint clip in animation.BaseClipTags.Take(12))
        {
            preview.SetClip(animation.ReadClip(clip), play: false);
            preview.Seek(0);
            var first = positions.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
            preview.Seek(preview.DurationSeconds * .5f);
            moved = MathF.Max(moved, positions.Max(pair => pair.Value.Select((position, index) =>
                Vector3.Distance(position, first[pair.Key][index])).Max()));
            if (moved > .001f) break;
        }
        Check(preview.Error is null && preview.SkinnedPartCount > 0 && moved > .001f, "No clip moved the four-bone weapon.");
        Console.WriteLine($"weapon_animation_four_bone parts={preview.SkinnedPartCount} moved={moved:G6}");
    }

    /** Tommy's default dyes fill gear_dye_0 to 2; its body plates bind their single source textures. */
    private static void GearPlating(PackageCatalog catalog, SceneDocument scene)
    {
        var dyes = NativeGearPlating.ReadItemDyes(catalog, catalog.Read(0x8132017D), out string? error)
            ?? throw new InvalidDataException("Tommy's default dyes did not read: " + error);
        Check(dyes.Records.SequenceEqual([0x81A696C8u, 0x81A696CCu, 0x81A696D0u, 0x80B3571Eu]),
            "Tommy's default dyes did not resolve to their gear_dye scope records.");
        NativeGearPlating.SetDyes(scene, dyes);
        var plan = NativeScenePlan.Prepare(catalog, scene, assetPreview: true);
        var body = plan.Groups.First(group => group.Mesh.MaterialTag == 0x8152A7FF).Material.Gear!;
        // Plate set 0 is one 2048 atlas; Tommy's body fills it with texture 0x8161F5E3 at the origin.
        Check(plan.PreparedPartCount == 12 && body.Scopes.Single() is { RecordTag: 0x81A696D0, ConstantBufferSlot: 5 } scope
            && scope.Textures.Keys.Order().SequenceEqual([7u, 8u])
            && plan.Textures[body.PlateTextures[0]] is { Width: 2048, Height: 2048 } atlas
            && atlas.Subresources[0].Data.AsSpan().SequenceEqual(new TextureDataReader(catalog).Read(0x8161F5E3).Subresources[0].Data),
            $"Tommy's gear inputs did not prepare: {plan.PreparedPartCount} parts, "
            + string.Join(" ", plan.SkippedReasons.Select(pair => $"{pair.Value} {pair.Key}")));
        // Opcode 0x1C scales by RSQRTPS of the XYZ length with one Newton step.
        var stage = new MaterialStageData(GameShaderStage.Pixel, null, [0x34, 0x00, 0x1C, 0x43, 0x00],
            [new Vector4(3, 4, 0, 7)], [Vector4.Zero], 0, []);
        var normalized = TfxEvaluator.Evaluate(stage, new TfxInputs()).Constants[0];
        Check(Vector4.Distance(normalized, new Vector4(.6f, .8f, 0, 1.4f)) < .001f, "TFX opcode 0x1C did not normalize XYZ.");
        Console.WriteLine($"gear_plating installed parts={plan.PreparedPartCount} dyes=item plates=atlas tfx_1c=pass");
    }

    /** In camera mode 0 no first-person draw sets stencil bit 3, so the stencil outline 81532F58 adds nothing. */
    public static void OutlineStencil(string install)
    {
        const int width = 320, height = 240;
        using var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        var scene = WeaponAnimationPreviewSource.Prepare(catalog, 0x80BB8264, 0x80BC5F6D,
            [0x80BB900F, 0x80BB9138, 0x80BB9265, 0x80BB979D, 0x80BB9899, 0x80BB947E]).Scene;
        var outline = scene.Instances.First(row => row.Mesh.Native?.MaterialTag == 0x81532F58);
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        foreach (var vertex in outline.Mesh.Vertices)
        {
            var point = Vector3.Transform(vertex.Position, outline.Transform);
            min = Vector3.Min(min, point); max = Vector3.Max(max, point);
        }
        // The sight's outline faces the eye down +X; from other sides the housing hides it.
        var centre = (min + max) * 0.5f;
        var eye = centre - Vector3.UnitX * (max - min).Length() * 2;
        var points = Enumerable.Range(0, height).SelectMany(y => Enumerable.Range(0, width).Select(x => (x, y))).ToArray();
        foreach (bool warp in new[] { false, true })
        {
            // The renderer empties the plan's textures on upload, so each adapter prepares its own.
            var plan = AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: 128L * 1024 * 1024, assetPreview: true);
            var stage0 = plan.Groups.Where(group => group.RenderStage == 0 && group.Mesh.Kind == NativeMeshKind.Entity).ToArray();
            Check(stage0.Length != 0 && stage0.All(group => group.Material.StencilReference == NativeStencil.EntityReference
                && group.Material.States.DepthStencil.StencilEnable
                && group.Material.States.DepthStencil.StencilWriteMask == NativeStencil.KnownBits),
                "First-person stage 0 entity draws do not write the known stencil bits.");
            using var renderer = new Dx11Renderer(IntPtr.Zero, width, height, warp);
            using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
            viewport.SetScene(scene);
            viewport.SetNativeScene(plan);
            viewport.Camera.LookFrom(eye, centre);
            viewport.RenderImage(width, height);
            Check(viewport.UseGameShaders && viewport.NativeError is null, viewport.NativeError ?? "Native rendering fell back to geometry.");
            var native = viewport.NativeRenderer!;
            native.AutoExposure = false;
            // Linear scene colour; the warmup measures the sky at once, so no frame eases toward the last one.
            Vector4[] Frame()
            {
                for (int frame = 0; frame < 2; frame++)
                {
                    native.RequestExposureWarmup();
                    viewport.RenderImage(width, height);
                }
                return native.SamplePixels(points).Select(sample => sample.Scene).ToArray();
            }
            viewport.Visibility.Filter = item => !ReferenceEquals(item, outline);
            var hidden = Frame();
            int hiddenParts = native.LastFrame.DrawnParts;
            viewport.Visibility.Filter = null;
            var shown = Frame();
            Check(native.LastFrame.FailedParts == 0 && native.LastFrame.DrawnParts == hiddenParts + 1,
                $"The outline part was not drawn on {(warp ? "WARP" : "hardware")}.");
            float difference = hidden.Zip(shown, (a, b) => Vector4.Abs(a - b)).Max(delta => MathF.Max(MathF.Max(delta.X, delta.Y), delta.Z));
            Check(difference < 1e-4f, $"The stencil outline changed the image by {difference} in camera mode 0.");
            // The clear leaves 4; a first-person entity draw stores (0x23 & 0x2C) = 0x20.
            var stencil = native.ReadStencilCopy();
            int weapon = stencil.Count(value => value == 0x20);
            Check(stencil.All(value => value is 4 or 0x20) && weapon > 0,
                $"The stencil copy holds {string.Join(",", stencil.Distinct().Select(v => $"0x{v:X2}"))}.");
            // Positive control: a clear with bit 3 set leaves bit 3 off only where the weapon wrote, so edges exist.
            native.StencilClear = 0x0C;
            viewport.Visibility.Filter = item => !ReferenceEquals(item, outline);
            var controlHidden = Frame();
            viewport.Visibility.Filter = null;
            var controlShown = Frame();
            var controlStencil = native.ReadStencilCopy();
            Check(controlStencil.All(value => value is 0x0C or 0x20) && controlStencil.Contains((byte)0x0C),
                $"The control stencil holds {string.Join(",", controlStencil.Distinct().Select(v => $"0x{v:X2}"))}.");
            static float Sum(Vector4 p) => p.X + p.Y + p.Z;
            int outlined = 0, darker = 0;
            for (int i = 0; i < points.Length; i++)
            {
                float delta = Sum(controlShown[i]) - Sum(controlHidden[i]);
                if (delta > .003f) outlined++;
                else if (delta < -.003f) darker++;
            }
            Check(outlined > 0 && darker == 0, $"With bit 3 set the outline brightened {outlined} pixels and darkened {darker}.");
            native.StencilClear = NativeStencil.ClearValue;
            Console.WriteLine($"outline_stencil adapter={(warp ? "warp" : "hardware")} drawn=yes changed=0 weapon_pixels={weapon} "
                + $"control_outlined={outlined} result=pass");
        }
    }

    /** Native first-person clips move imported geometry through the mechanical inverse binds. */
    public static void Prepared(PreparedWeaponAnimationPreview prepared)
    {
        var animation = prepared.Animation ?? throw new InvalidDataException(
            "The weapon did not bind its animation: " + string.Join(" ", prepared.Scene.Diagnostics));
        Check(prepared.Scene.Instances.Count > 4 && animation.WeaponBinding is not null,
            "The external preview did not assemble the weapon's art parts.");
        var uploaded = new Dictionary<MeshData, RenderVertex[]>();
        using var preview = new AssetAnimationPreview(animation, prepared.Scene,
            (mesh, vertices) => uploaded[mesh] = vertices.ToArray(), () => { });
        var clipTag = animation.Clips.First(clip => clip.Index == 113).ClipTag;
        preview.SetClip(animation.ReadClip(clipTag), play: false);
        preview.Seek(0);
        var before = uploaded.ToDictionary(pair => pair.Key, pair => pair.Value.Select(vertex => vertex.Position).ToArray());
        preview.Seek(preview.DurationSeconds * .45f);
        Check(preview.Error is null && preview.SkinnedPartCount == prepared.Scene.Instances.Select(row => row.Mesh).Distinct().Count(),
            "Some weapon parts did not receive their mechanical palette.");
        float displacement = uploaded.Max(pair => pair.Value.Select((vertex, index) =>
            Vector3.Distance(vertex.Position, before[pair.Key][index])).Max());
        Check(displacement > .001f, "The first-person reload clip did not move the weapon geometry.");
        var expected = WeaponAnimationBinding.Compose(animation.WeaponBinding!.WeaponRig.Bones
            .Select(bone => bone.InverseBindObject.Matrix).ToArray(), preview.Pose.ObjectTransforms, 72);
        var skin = preview.SkinFor(prepared.Scene.Instances[0])!;
        for (int index = 0; index < 5; index++)
            Check(Vector3.Distance(Vector3.Transform(Vector3.One, expected[index]), Vector3.Transform(Vector3.One, skin[index])) < .0001f,
                "The displayed palette differs from the mechanical frame composition.");
        Check(preview.DisplayPose.Rig.Bones.Count == 5 && preview.Pose.Rig.Bones.Count == 77,
            "The clip rig and displayed weapon rig were conflated.");
        Console.WriteLine($"weapon_animation_preview owned=pass parts={preview.SkinnedPartCount} displacement={displacement:G6} mechanical_palette=pass");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("An invalid palette partition was accepted."); }
}
