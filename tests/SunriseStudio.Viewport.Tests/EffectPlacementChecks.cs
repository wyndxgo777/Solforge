using System.Numerics;
using SunriseStudio.Animation;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Particle nodes sit where the location table puts them, and spawn in their spawn frame. */
internal static class EffectPlacementChecks
{
    private const uint Centurion = 0x80C19B1F;
    // Config 0x80FEE123 puts its particle nodes on fx_jetpack; 0x80FEE12E keeps them at the root.
    private const uint JetpackConfig = 0x80FEE123, RootConfig = 0x80FEE12E;
    private const uint Jetpack = 0x2074F50C;
    private const int JetpackBone = 9;
    // Authored fx_jetpack offset from bone 9 in point set 0x80BFDB3C.
    private static readonly Vector3 JetpackOffset = new(0.047701f, 0, 0);

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string gameRoot)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        var scene = new AssetSceneLoader(catalog).Load(Centurion);
        var nodes = scene.Markers.Where(m => m.Effect is { Kind: EffectKind.ParticleSystem }).ToArray();
        Require(nodes.Length == 14, $"80C19B1F has {nodes.Length} particle nodes, expected 14: "
            + string.Join("; ", scene.Diagnostics.Where(line => line.Contains("effects", StringComparison.Ordinal))));
        var bind = new SkeletonReader(catalog).Read(Centurion).Bones[JetpackBone].BindObject;
        var expected = bind.Translation + bind.Scale * Vector3.Transform(JetpackOffset, bind.Rotation);
        int jetpack = 0, root = 0, world = 0;
        foreach (var marker in nodes)
        {
            var effect = marker.Effect!;
            world += effect.Locations.Count(slots => slots.Any(slot => slot.World));
            if (effect.SourceTag == JetpackConfig)
            {
                // fx_jetpack has count 2, but the entity holds one point with that name.
                var spawn = effect.SlotLocation(0, effect.Systems[0].Emitter!.SpawnSlot);
                Require(spawn is { Kind: 2, Point: Jetpack, Count: 2, Frames: [{ Bone: JetpackBone }] } && effect.Bone == JetpackBone,
                    $"Node 0x{effect.NodeName:X8} is not on fx_jetpack bone {JetpackBone}.");
                Require(Vector3.Distance(marker.Position, expected) < 1e-3f
                        && Vector3.Distance(effect.Transform.Translation, expected) < 1e-3f,
                    $"Node 0x{effect.NodeName:X8} sits at {marker.Position}, expected {expected}.");
                jetpack++;
            }
            else
            {
                Require(effect.SourceTag == RootConfig && effect.Bone == -1 && marker.Position.Length() < 1e-5f,
                    $"Node 0x{effect.NodeName:X8} of 0x{effect.SourceTag:X8} is not at the root: {marker.Position}.");
                root++;
            }
        }
        Require(jetpack == 11 && root == 3, $"80C19B1F places {jetpack} nodes on the jetpack and {root} at the root.");
        // Two root nodes and four jetpack nodes simulate in world space.
        Require(world == 6, $"80C19B1F has {world} systems with a world slot, expected 6.");
        int setups = Setups(scene).Count;
        Require(setups == 14, $"80C19B1F makes {setups} emitter setups, expected 14.");
        Console.WriteLine($"effect_placement asset=80C19B1F jetpack={jetpack} root={root} world_slots={world} "
            + $"setups={setups} at={expected.X:0.###},{expected.Y:0.###},{expected.Z:0.###} result=pass");
        Hydra(catalog);
        Basis(catalog, 0x80B3A188, 0x80B3A3ED, 0xC20, 11, Vector3.UnitZ, Vector3.UnitY, -Vector3.UnitX);
        Basis(catalog, 0x80B3A440, 0x80B3BDC2, 0x1978, 10, -Vector3.UnitX, -Vector3.UnitY, Vector3.UnitZ);
        Basis(catalog, 0x80F808A0, 0x80F8089F, 0x990, 12, -Vector3.UnitZ, -Vector3.UnitY, -Vector3.UnitX);
        SpawnFrame(new EffectReader(catalog));
        TwoFrames(new EffectReader(catalog));
    }

    private static List<EffectEmitterSetup> Setups(SceneDocument scene)
    {
        var result = new List<EffectEmitterSetup>();
        for (int index = 0; index < scene.Markers.Count; index++)
            if (scene.Markers[index].Effect is { Kind: EffectKind.ParticleSystem } effect)
                for (int system = 0; system < effect.Systems.Count; system++)
                    result.AddRange(EffectSimulation.CreateSystems(scene.Markers[index], index, system));
        return result;
    }

    private static SceneMarker Node(SceneDocument scene, uint config, int offset) =>
        scene.Markers.SingleOrDefault(m => m.Effect is { } e && e.SourceTag == config && e.SourceOffset == offset)
        ?? throw new InvalidOperationException($"Node 0x{config:X8}+0x{offset:X} is missing.");

    private static Vector3 Row(Matrix4x4 m, int row) => Vector3.Normalize(row switch
    {
        0 => new(m.M11, m.M12, m.M13), 1 => new(m.M21, m.M22, m.M23), _ => new(m.M31, m.M32, m.M33)
    });

    // Bind-pose ends of the Hydra's two primary_trigger points, nodes 5 and 6.
    private static readonly Vector3[] HydraGuns = [new(0.0782f, 1.6721f, 3.1836f), new(0.0779f, -1.6718f, 3.1843f)];

    /** Both rows of the repeated primary_trigger hash are spawn frames: both guns fire. */
    private static void Hydra(PackageCatalog catalog)
    {
        const uint hydra = 0x80B82D35, config = 0x80F2FD20;
        var scene = new AssetSceneLoader(catalog).Load(hydra);
        var setups = new List<EffectEmitterSetup>();
        foreach (int offset in new[] { 0xC78, 0xEE8 })
        {
            var marker = Node(scene, config, offset);
            int index = scene.Markers.IndexOf(marker);
            var effect = marker.Effect!;
            var spawn = effect.SlotLocation(0, effect.Systems[0].Emitter!.SpawnSlot)!;
            Require(spawn.Frames.Count == 2, $"Hydra node +0x{offset:X} has {spawn.Frames.Count} spawn frames, expected 2.");
            for (int frame = 0; frame < 2; frame++)
            {
                var at = (spawn.Frames[frame].Local * spawn.Frames[frame].BoneTransform).Translation;
                Require(Vector3.Distance(at, HydraGuns[frame]) < 1e-3f, $"Hydra node +0x{offset:X} frame {frame} is at {at}.");
            }
            var made = EffectSimulation.CreateSystems(marker, index, 0).ToList();
            Require(made.Count == 2 && made.Select(e => e.Instance).SequenceEqual([0, 1]),
                $"Hydra node +0x{offset:X} makes {made.Count} setups, expected 2.");
            var turn = made[0].SpawnFrame;
            if (offset == 0xC78)
                Require(Vector3.Distance(Row(turn, 0), new(0.9997f, -0.0106f, -0.0217f)) < 1e-3f,
                    $"Hydra exhaust X axis is {Row(turn, 0)}.");
            else
                Require(Vector3.Distance(Row(turn, 0), Vector3.UnitZ) < 1e-4f
                    && Vector3.Distance(Row(turn, 1), new(0.9321f, -0.3621f, 0)) < 1e-3f,
                    $"Hydra 187B6B9C up pass gives X {Row(turn, 0)} and Y {Row(turn, 1)}.");
            setups.AddRange(made);
        }
        foreach (var gun in HydraGuns)
            Require(setups.Count(e => Vector3.Distance(e.Origin, gun) < 1e-3f) == 2, $"Hydra setups miss the gun at {gun}.");
        Console.WriteLine($"effect_hydra asset={hydra:X8} setups={setups.Count} guns=2 up_pass=pass result=pass");
    }

    /** A root location of kind 10, 11 or 12 rebuilds its axes from the entity and world up. */
    private static void Basis(PackageCatalog catalog, uint entity, uint config, int offset, int kind, Vector3 x, Vector3 y, Vector3 z)
    {
        var scene = new AssetSceneLoader(catalog).Load(entity);
        var marker = Node(scene, config, offset);
        var effect = marker.Effect!;
        var spawn = effect.SlotLocation(0, effect.Systems[0].Emitter!.SpawnSlot)!;
        Require(spawn.Kind == kind && spawn.Frames.Count == 1, $"Node 0x{config:X8}+0x{offset:X} spawns at kind {spawn.Kind}.");
        var frame = spawn.Frames[0].InEntity(Matrix4x4.Identity, Matrix4x4.Identity);
        Require(Vector3.Distance(Row(frame, 0), x) < 1e-4f && Vector3.Distance(Row(frame, 1), y) < 1e-4f
                && Vector3.Distance(Row(frame, 2), z) < 1e-4f && frame.Translation.Length() < 1e-5f,
            $"Kind {kind} frame of 0x{entity:X8} has axes {Row(frame, 0)} {Row(frame, 1)} {Row(frame, 2)}.");
        var setup = EffectSimulation.CreateSystems(marker, scene.Markers.IndexOf(marker), 0).Single();
        Require(Vector3.Distance(Row(setup.SpawnFrame, 0), x) < 1e-4f, $"Kind {kind} setup does not take the rebuilt frame.");
        Console.WriteLine($"effect_location_kind kind={kind} asset={entity:X8} x={x} y={y} result=pass");
    }

    /** Two spawn frames and one world frame give one instance per spawn frame. */
    private static void TwoFrames(EffectReader reader)
    {
        var emitter = (reader.ReadEmitter(0x80B38183) ?? throw new InvalidOperationException("Ring fixture emitter is missing."))
            with { SpawnSlot = 0, EmitterSlot = 1 };
        var root = EffectFrame.Root;
        var spawn = new EffectLocation(0, 0, 2, 0, [new EffectSource(2, 2, 0, 0x1234, 0xFF)])
        {
            Frames = [root with { Local = Matrix4x4.CreateTranslation(1, 2, 3) }, root with { Local = Matrix4x4.CreateTranslation(-1, -2, 3) }]
        };
        var world = new EffectLocation(1, 0, 1, 0, [new EffectSource(1, 1, 0, 0, 0xFF)]) { Frames = [root with { Mode = EffectFrameMode.World }] };
        var marker = new SceneMarker
        {
            Name = "Two frames",
            Effect = new EffectDescription
            {
                Systems = [new EffectSystem(1, emitter.Tag, 0, 0, [], emitter)], Locations = [[spawn, world]], DurationMax = 5,
                Placement = Matrix4x4.CreateTranslation(10, 0, 0)
            }
        };
        var setups = EffectSimulation.CreateSystems(marker, 0, 0).ToList();
        Require(setups.Count == 2, $"Two spawn frames made {setups.Count} setups.");
        var centres = new[] { new Vector3(11, 2, 3), new Vector3(9, -2, 3) };
        for (int i = 0; i < 2; i++)
        {
            var simulation = setups[i].Simulation!;
            simulation.Advance(0, 10);
            Require(simulation.Count > 0 && simulation.Transform.IsIdentity, $"Instance {i} did not run in the world frame.");
            for (int p = 0; p < simulation.Count; p++)
            {
                var position = Vector3.Transform(simulation.ParticleAt(p).Position, simulation.Transform);
                float ring = MathF.Sqrt(MathF.Pow(position.Y - centres[i].Y, 2) + MathF.Pow(position.Z - centres[i].Z, 2));
                Require(MathF.Abs(position.X - centres[i].X) < 1e-3f && MathF.Abs(ring - 1) < 1e-3f,
                    $"Instance {i} particle at {position} is off the ring around {centres[i]}.");
            }
        }
        Console.WriteLine("effect_two_frames instances=2 result=pass");
    }

    /** A ring spawned in a moved spawn frame lands around it in a world simulation frame. */
    private static void SpawnFrame(EffectReader reader)
    {
        const uint tag = 0x80B38183;
        var emitter = (reader.ReadEmitter(tag) ?? throw new InvalidOperationException("Ring fixture emitter is missing."))
            with { SpawnSlot = 0, EmitterSlot = 1 };
        var owner = Matrix4x4.CreateTranslation(10, 0, 0);
        Matrix4x4.Invert(owner, out var toWorld);
        var simulation = new EmitterSimulation(emitter, owner, [Matrix4x4.CreateTranslation(1, 2, 3), toWorld], 5,
            EffectSimulation.EmitterCap, 1);
        simulation.Advance(0, 10);
        Require(simulation.Count > 0 && simulation.Transform.IsIdentity, "World-space ring emitter did not run.");
        for (int i = 0; i < simulation.Count; i++)
        {
            var position = Vector3.Transform(simulation.ParticleAt(i).Position, simulation.Transform);
            float ring = MathF.Sqrt((position.Y - 2) * (position.Y - 2) + (position.Z - 3) * (position.Z - 3));
            Require(MathF.Abs(position.X - 11) < 1e-3f && MathF.Abs(ring - 1) < 1e-3f,
                $"Ring particle at {position} is off the ring around (11, 2, 3).");
        }
        Console.WriteLine($"effect_spawn_frame tag={tag:X8} particles={simulation.Count} result=pass");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
