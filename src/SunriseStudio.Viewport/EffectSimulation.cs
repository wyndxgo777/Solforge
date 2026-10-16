using System.Numerics;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** One drawable emitter: one instance of a particle system, or a lens flare sprite. */
internal sealed class EffectEmitterSetup
{
    public required SceneMarker Marker { get; init; }
    public uint Material { get; init; }
    public bool Flare { get; init; }
    private Matrix4x4 owner = Matrix4x4.Identity;
    /** The entity's world transform; the simulation follows it. */
    public Matrix4x4 Owner
    {
        get => owner;
        set { owner = value; if (Simulation is { } simulation) simulation.Owner = value; }
    }
    /** Index of the system in the effect. */
    public int System { get; init; }
    /** Instance k spawns at frame k of the spawn location. */
    public int Instance { get; init; }
    /** The frame each transform slot follows; null leaves the slot to the program. */
    public IReadOnlyList<EffectFrame?> Frames { get; init; } = [];
    public int SpawnSlot { get; init; }
    /** The spawn slot frame inside Owner. */
    public Matrix4x4 SpawnFrame { get; set; } = Matrix4x4.Identity;
    public Vector3 Origin => (SpawnFrame * owner).Translation;
    public float Size { get; init; }
    public uint Seed { get; init; }
    public EmitterSimulation? Simulation { get; init; }
    /** The play count of the node's sequence when this emitter last started. */
    public int Generation { get; set; }
    /** World radius around Origin that has held a drawn billboard, edge included; 0 before the first. */
    public float Reach { get; set; }
}

/**
 * Particle systems run their emitter programs; the raster stage is approximated.
 * Billboard size and fade are viewer choices because the raster vertex shader is not decoded.
 */
internal static class EffectSimulation
{
    // Emit never writes more than this for one emitter.
    public const int EmitterCap = 256;

    /**
     * One setup per frame of the spawn location; none when the spawn point is missing.
     * Instance k takes spawn frame k and simulation frame k, or frame 0 past the simulation frames.
     * @param index Marker index in the scene; it seeds the emitter's random stream.
     * @param position Index of the system in the effect.
     */
    public static IEnumerable<EffectEmitterSetup> CreateSystems(SceneMarker marker, int index, int position)
    {
        var effect = marker.Effect!;
        var system = effect.Systems[position];
        uint seed = Hash((uint)index, system.Tag);
        float radius = system.Emitter?.Radius ?? 0.2f;
        float size = Math.Clamp(0.3f + 0.25f * radius, 0.1f, 3);
        if (system.Emitter is not { } emitter)
        {
            yield return new EffectEmitterSetup { Marker = marker, Material = system.RasterMaterial, System = position,
                Size = size, Seed = seed, Owner = effect.Placement };
            yield break;
        }
        if (effect.SpawnMissing(position)) yield break;
        float duration = effect.Activation is { Timed: false } ? float.PositiveInfinity
            : MathF.Max(effect.DurationMax, effect.DurationMin);
        if (float.IsNaN(duration) || float.IsNegativeInfinity(duration)) duration = 0;
        var locations = Enumerable.Range(0, EmitterSimulation.TransformSlots)
            .Select(slot => effect.SlotLocation(position, slot)).ToArray();
        int Frames(int slot) => slot < locations.Length ? locations[slot]?.Frames.Count ?? 0 : 0;
        int instances = Math.Max(1, Frames(emitter.SpawnSlot)), simulated = Math.Max(1, Frames(emitter.EmitterSlot));
        for (int instance = 0; instance < instances; instance++)
        {
            var frames = new EffectFrame?[locations.Length];
            for (int slot = 0; slot < frames.Length; slot++)
            {
                if (locations[slot] is not { } location) continue;
                int pick = slot == emitter.SpawnSlot ? instance
                    : slot == emitter.EmitterSlot && instance < simulated ? instance : 0;
                // A location with no frame here still has a slot; the game writes a fallback frame.
                frames[slot] = pick < location.Frames.Count ? location.Frames[pick] : location.Frames.FirstOrDefault() ?? EffectFrame.Root;
            }
            var matrices = frames.Select(frame => frame?.InEntity(frame.BoneTransform, effect.Placement) ?? Matrix4x4.Identity).ToArray();
            uint instanceSeed = instance == 0 ? seed : Hash(seed, (uint)instance);
            yield return new EffectEmitterSetup
            {
                Marker = marker,
                Material = system.RasterMaterial,
                System = position,
                Instance = instance,
                Frames = frames,
                SpawnSlot = emitter.SpawnSlot,
                SpawnFrame = emitter.SpawnSlot < matrices.Length ? matrices[emitter.SpawnSlot] : Matrix4x4.Identity,
                Size = size,
                Seed = instanceSeed,
                Simulation = new EmitterSimulation(emitter, effect.Placement, matrices, duration, EmitterCap, instanceSeed),
                Owner = effect.Placement
            };
        }
    }

    /**
     * Moves an emitter with its entity; its moving slots take their frame for this pose.
     * @param pose Entity-space bone poses; null keeps every bone at its bind pose.
     */
    public static void Follow(EffectEmitterSetup e, IReadOnlyList<Matrix4x4>? pose)
    {
        var placement = e.Marker.Effect!.Placement;
        e.Owner = placement;
        for (int slot = 0; slot < e.Frames.Count; slot++)
        {
            if (e.Frames[slot] is not { Moves: true } frame) continue;
            var bone = pose is not null && frame.Bone >= 0 && frame.Bone < pose.Count ? pose[frame.Bone] : frame.BoneTransform;
            var matrix = frame.InEntity(bone, placement);
            e.Simulation?.Follow(slot, matrix);
            if (slot == e.SpawnSlot) e.SpawnFrame = matrix;
        }
    }

    /** Flare emitter setup for a marker's effect; the seed comes from the index and effect source. */
    public static EffectEmitterSetup CreateFlare(SceneMarker marker, int index)
    {
        var effect = marker.Effect!;
        return new()
        {
            Marker = marker,
            Material = effect.FlareElements.Count != 0 ? effect.FlareElements[0].Material : 0,
            Flare = true,
            Owner = effect.Placement,
            Size = 0.9f,
            Seed = Hash((uint)index, effect.SourceTag)
        };
    }

    /**
     * Writes the live particles of one emitter at the given clock.
     * @return Particles written; never more than output.Length or EmitterCap.
     */
    public static int Emit(EffectEmitterSetup e, double seconds, float cameraDistance, Span<EffectBillboard> output)
    {
        if (output.IsEmpty) return 0;
        if (e.Flare)
        {
            output[0] = new EffectBillboard(e.Origin, e.Size, new Vector4(1, 1, 1, 0.6f), 0);
            e.Reach = e.Size;
            return 1;
        }
        if (e.Simulation is not { } simulation) return 0;
        simulation.Advance(seconds, cameraDistance);
        var world = simulation.Transform;
        var origin = e.Origin;
        float reach = e.Reach;
        int count = Math.Min(Math.Min(output.Length, EmitterCap), simulation.Count);
        for (int i = 0; i < count; i++)
        {
            var billboard = output[i] = Billboard(e, simulation, world, i);
            reach = MathF.Max(reach, Vector3.Distance(billboard.Position, origin) + billboard.Size);
        }
        e.Reach = reach;
        return count;
    }

    /** Advances an emitter without drawing it and grows its reach from its live particles. */
    public static void Measure(EffectEmitterSetup e, double seconds, float cameraDistance)
    {
        if (e.Flare || e.Simulation is not { } simulation) return;
        simulation.Advance(seconds, cameraDistance);
        var world = simulation.Transform;
        var origin = e.Origin;
        float reach = e.Reach;
        for (int i = 0; i < simulation.Count; i++)
        {
            var billboard = Billboard(e, simulation, world, i);
            reach = MathF.Max(reach, Vector3.Distance(billboard.Position, origin) + billboard.Size);
        }
        e.Reach = reach;
    }

    /** Live particle i as a billboard; size and fade are viewer choices. */
    internal static EffectBillboard Billboard(EffectEmitterSetup e, EmitterSimulation simulation, Matrix4x4 world, int i)
    {
        var (position, age, spin, random) = simulation.ParticleAt(i);
        float fade = Smooth(0, 0.15f, age) * (1 - Smooth(0.6f, 1, age));
        float size = e.Size * (0.8f + 0.4f * random) * (1 + 0.5f * age);
        return new EffectBillboard(Vector3.Transform(position, world), size, new Vector4(1, 1, 1, fade), spin);
    }

    private static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /** Mixes two values into a well-spread 32-bit hash. */
    internal static uint Hash(uint a, uint b)
    {
        uint h = a * 0x85EBCA6Bu ^ b * 0xC2B2AE35u;
        h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
        return h;
    }
}

/** One baked sprite: world position, half-width, fade and screen spin in radians. */
public readonly record struct EffectBakeSample(int Frame, Vector3 Position, float Size, float Alpha, float Spin);

/**
 * One emitter instance baked as a loop, one frame per simulation step.
 * @param MarkerIndex Index of the marker in the scene.
 * @param Default True when the viewer runs it without being told to.
 * @param Mode "cycle" is one start-to-start run, "wrap" a steady stream, "held" the live particles as they are.
 */
public sealed record EffectBakeEmitter(SceneMarker Marker, int MarkerIndex, int System, int Instance, uint Material,
    bool Default, string Trigger, string Mode, int Frames, Vector3 Origin, IReadOnlyList<EffectBakeSample> Samples)
{
    /** Why the emitter has no samples; null when it has some. */
    public string? Failure { get; init; }
}

/** Loop lengths of a bake, in seconds. */
public sealed record EffectBakeOptions
{
    /** Window of a steady stream. */
    public float LoopSeconds { get; init; } = 4;
    /** A node that restarts within this time loops over one start-to-start run. */
    public float MaxCycleSeconds { get; init; } = 12;
    /** A particle of the window still alive this long after it is cut. */
    public float TailSeconds { get; init; } = 10;
    /** The viewer passes the camera distance to emitter programs; a bake has no camera. */
    public float CameraDistance { get; init; } = 10;
}

/** Runs the viewer's particle simulation off screen and records each particle as the viewer draws it. */
public static class EffectBake
{
    /** Frames per second of a bake. */
    public static float Rate => 1 / EmitterSimulation.StepLength;

    /**
     * Bakes every particle system instance of the scene's effect markers; lens flares are left out.
     * @return One entry per emitter instance, and one with a Failure for each system the viewer cannot draw.
     */
    public static IReadOnlyList<EffectBakeEmitter> Run(SceneDocument scene, EffectBakeOptions? options = null,
        CancellationToken token = default)
    {
        options ??= new();
        var jobs = new List<(EffectEmitterSetup? Setup, int Index, int System, string? Failure)>();
        for (int index = 0; index < scene.Markers.Count; index++)
        {
            if (scene.Markers[index].Effect is not { Kind: EffectKind.ParticleSystem } effect) continue;
            for (int system = 0; system < effect.Systems.Count; system++)
            {
                var setups = EffectSimulation.CreateSystems(scene.Markers[index], index, system).ToArray();
                if (setups.Length == 0) jobs.Add((null, index, system, "The spawn point is missing; the game draws nothing."));
                foreach (var setup in setups)
                    jobs.Add((setup, index, system, setup.Simulation is null ? "The emitter did not decode." : null));
            }
        }
        var results = new EffectBakeEmitter[jobs.Count];
        Parallel.For(0, jobs.Count, new ParallelOptions { CancellationToken = token }, job =>
        {
            var (setup, index, system, failure) = jobs[job];
            var marker = scene.Markers[index];
            var effect = marker.Effect!;
            var empty = new EffectBakeEmitter(marker, index, system, setup?.Instance ?? 0, effect.Systems[system].RasterMaterial,
                effect.PlaysByDefault(), effect.Activation?.Trigger() ?? "sequence not read", "", 0,
                setup?.Origin ?? effect.Transform.Translation, []);
            results[job] = failure is not null ? empty with { Failure = failure } : Bake(setup!, empty, options);
        });
        return results;
    }

    /** Runs one emitter from the warm-up the viewer shows and records one loop. */
    private static EffectBakeEmitter Bake(EffectEmitterSetup e, EffectBakeEmitter result, EffectBakeOptions options)
    {
        var simulation = e.Simulation!;
        var effect = e.Marker.Effect!;
        EffectSimulation.Follow(e, null);
        // As the viewer runs it: on by default, or forced on as Show all does.
        simulation.Active = true;
        simulation.Loop = true;
        simulation.Delay = result.Default ? effect.Activation!.Delay + (effect.Activation.SerialStart ?? 0) : 0;
        float distance = options.CameraDistance;
        int Steps(float seconds) => Math.Max(1, (int)MathF.Round(seconds * Rate));
        simulation.Advance(0, distance);
        var samples = new List<EffectBakeSample>();
        void Record(int frame, long first, long end)
        {
            var world = simulation.Transform;
            int count = Math.Min(simulation.Count, EffectSimulation.EmitterCap);
            for (int i = 0; i < count; i++)
            {
                long serial = simulation.SerialAt(i);
                if (serial < first || serial >= end) continue;
                var billboard = EffectSimulation.Billboard(e, simulation, world, i);
                // A sprite with no alpha draws nothing; a nonfinite one has no place to draw.
                if (billboard.Color.W > 0 && float.IsFinite(billboard.Size) && float.IsFinite(billboard.Spin.X)
                    && float.IsFinite(billboard.Position.X + billboard.Position.Y + billboard.Position.Z))
                    samples.Add(new(frame, billboard.Position, billboard.Size, billboard.Color.W, billboard.Spin.X));
            }
        }
        string mode = "wrap";
        int frames = Steps(options.LoopSeconds), limit = Steps(options.MaxCycleSeconds);
        bool timed = effect.Activation is not { Timed: false } && float.IsFinite(MathF.Max(effect.DurationMax, effect.DurationMin));
        if (timed && StepToStart(simulation, distance, limit))
        {
            // A run starts and ends empty, so one whole run loops without a seam.
            for (int step = 0; step < limit; step++)
            {
                Record(step, 0, long.MaxValue);
                int starts = simulation.Starts;
                simulation.StepOnce(distance);
                if (simulation.Starts == starts) continue;
                mode = "cycle";
                frames = step + 1;
                break;
            }
            if (mode != "cycle") samples.Clear();
            // Loop frame 0 shows the run as the viewer first shows it, after its warm-up.
            int shift = Steps(EmitterSimulation.WarmUpLength) % frames;
            for (int i = 0; i < samples.Count; i++)
                samples[i] = samples[i] with { Frame = (samples[i].Frame - shift + frames) % frames };
        }
        if (mode != "cycle")
        {
            // Particles spawned in one window, each shown again one window later, make a steady stream.
            long first = simulation.NextSerial;
            for (int step = 0; step < frames; step++)
            {
                simulation.StepOnce(distance);
                Record(step, first, long.MaxValue);
            }
            long end = simulation.NextSerial;
            if (end == first)
            {
                // Nothing spawned in the window: keep what lives, as the viewer shows it.
                mode = "held";
                samples.Clear();
                for (int step = 0; step < frames; step++)
                {
                    simulation.StepOnce(distance);
                    Record(step, 0, long.MaxValue);
                }
            }
            else
                for (int step = frames, last = frames + Steps(options.TailSeconds); step < last; step++)
                {
                    simulation.StepOnce(distance);
                    int before = samples.Count;
                    Record(step % frames, first, end);
                    if (samples.Count == before && !Alive(simulation, first, end)) break;
                }
        }
        if (simulation.Failure is { } failure) return result with { Failure = failure };
        if (samples.Count == 0) return result with { Mode = mode, Frames = frames, Failure = "No particle shows in the loop." };
        samples.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        return result with { Mode = mode, Frames = frames, Samples = samples };
    }

    /** Steps until the node starts again; false when it does not within limit steps. */
    private static bool StepToStart(EmitterSimulation simulation, float distance, int limit)
    {
        int starts = simulation.Starts;
        for (int step = 0; step < limit; step++)
        {
            simulation.StepOnce(distance);
            if (simulation.Starts != starts) return true;
        }
        return false;
    }

    private static bool Alive(EmitterSimulation simulation, long first, long end)
    {
        for (int i = 0; i < simulation.Count; i++)
            if (simulation.SerialAt(i) >= first && simulation.SerialAt(i) < end) return true;
        return false;
    }
}
