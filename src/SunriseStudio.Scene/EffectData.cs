using System.Numerics;

namespace SunriseStudio.Scene;

/** Kind of effect node. This file holds the decoded effect (particle system and lens flare) data. */
public enum EffectKind { ParticleSystem, LensFlare }

/** One named emitter channel: its storage kind and slot from the emitter's 55-entry map. */
public readonly record struct EffectChannel(int Index, string Name, byte Kind, byte Slot, Vector4? Constant);

/** A {first, count} run of the emitter's slot list. */
public readonly record struct EffectSlotGroup(byte First, byte Count);

/**
 * Emitter values that the emitter stores as constants (channel kind 6).
 * A null value is computed by the emitter program, which the viewer does not run.
 */
public sealed record EffectEmitter(uint Tag, IReadOnlyList<EffectChannel> Channels)
{
    /** The emitter's byte code; StageLengths splits it into eight stages in order. */
    public byte[] Program { get; init; } = [];
    public ushort[] StageLengths { get; init; } = new ushort[8];
    /** Vec4 constants the program reads by index. */
    public Vector4[] ProgramConstants { get; init; } = [];
    /** The +0x08 array that kind-6 channels index by float. */
    public Vector4[] ChannelConstants { get; init; } = [];
    /** Native spawn shape: 0 point, 1 ring, 2 sphere, 3 line, 4 box, 5 grid. */
    public byte Shape { get; init; }
    /** Native spawn direction: 0 cone, 1 ring cone, 2 sphere, 3 and 4 outward from the spawn point. */
    public byte Direction { get; init; }
    /** Transform slots of the spawn frame and the simulation frame. */
    public byte SpawnSlot { get; init; }
    public byte EmitterSlot { get; init; }
    /** The +0x18 slot list: a row-byte index per entry. */
    public byte[] SlotList { get; init; } = [];
    /** The +0x28 groups over the slot list. */
    public EffectSlotGroup[] SlotGroups { get; init; } = [];
    /** The +0x38 spawn group and the +0x3A simulation group. */
    public EffectSlotGroup SpawnGroup { get; init; }
    public EffectSlotGroup SimulationGroup { get; init; }
    /** Total transform slots, +0x47; slots past the row's byte count are program slots. */
    public byte SlotCount { get; init; }
    /** True when acceleration 0 or 1 is authored in the emitter frame rather than the world. */
    public bool LocalAcceleration0 { get; init; }
    public bool LocalAcceleration1 { get; init; }

    public float? Lifetime => Scalar(5);
    public float? EmissionRate => Scalar(10);
    public Vector3? ShapeScale => Vector(11);
    public float? Angle => Scalar(12);
    public float? Radius => Scalar(13);
    public float? Length => Scalar(14);
    public float? InitialSpeed => Scalar(15);
    public float? InitialCount => Scalar(16);
    public float? MaxCount => Scalar(17);
    public Vector3? Acceleration0Direction => Vector(18);
    public float? Acceleration0Strength => Scalar(19);
    public Vector3? Acceleration1Direction => Vector(20);
    public float? Acceleration1Strength => Scalar(21);
    public float? AirFriction => Scalar(22);
    public float? RotationRate => Scalar(24);
    public float? GravityStrength => Scalar(31);

    /**
     * The row byte that feeds a transform slot, or -1 when none does.
     * A program slot takes its group's first slot-list entry; that stand-in is assumed.
     */
    public int RowFor(int slot, int rows)
    {
        if (slot < rows) return slot;
        int first = slot == SpawnSlot ? SpawnGroup.First : slot == EmitterSlot ? SimulationGroup.First : -1;
        int row = first >= 0 && first < SlotList.Length ? SlotList[first] : -1;
        return row < rows ? row : -1;
    }

    private EffectChannel? Find(int index) => Channels.FirstOrDefault(c => c.Index == index && c.Constant is not null);
    private float? Scalar(int index) => Find(index)?.Constant?.X;
    private Vector3? Vector(int index) => Find(index)?.Constant is { } v ? new Vector3(v.X, v.Y, v.Z) : null;
}

/** Class-0x80806E28 particle system: emitter, raster material, and compute techniques when simulated on the GPU. */
public sealed record EffectSystem(uint Tag, uint EmitterTag, uint RasterMaterial, uint MeshTag,
    IReadOnlyList<uint> ComputeMaterials, EffectEmitter? Emitter)
{
    public bool GpuSimulated => ComputeMaterials.Count != 0;
}

/** One location source: kind, frame cap, flags, point hash and sub-object. */
public sealed record EffectSource(byte Kind, byte Count, byte Flags, uint Point, byte SubObject);

/** How a frame is built: in the entity, the world, or a basis rebuilt around world up. */
public enum EffectFrameMode : byte { Entity, World, Back, Up, Down }

/** One frame a location gives. Local and BoneTransform apply in Entity mode only. */
public sealed record EffectFrame(EffectFrameMode Mode, int Bone, Matrix4x4 Local, Matrix4x4 BoneTransform, bool UpPass)
{
    public static EffectFrame Root { get; } = new(EffectFrameMode.Entity, -1, Matrix4x4.Identity, Matrix4x4.Identity, false);
    public bool World => Mode == EffectFrameMode.World;
    /** False when the frame never changes inside the entity. */
    public bool Moves => Mode is EffectFrameMode.World or EffectFrameMode.Up or EffectFrameMode.Down || Bone >= 0 || UpPass;

    /**
     * The frame inside the entity for a bone pose and an entity placement.
     * @param bone The bone's entity-space pose; ignored without a bone.
     * @param placement The entity's world transform.
     */
    public Matrix4x4 InEntity(Matrix4x4 bone, Matrix4x4 placement)
    {
        var toEntity = Matrix4x4.Invert(placement, out var inverse) ? inverse : Matrix4x4.Identity;
        switch (Mode)
        {
            case EffectFrameMode.World: return toEntity;
            case EffectFrameMode.Back: return Matrix4x4.CreateRotationZ(MathF.PI);
            case EffectFrameMode.Up:
                return Basis(Vector3.UnitZ, Level(placement), placement.Translation) * toEntity;
            case EffectFrameMode.Down:
                return Basis(-Vector3.UnitZ, -Level(placement), placement.Translation) * toEntity;
        }
        var frame = Local * (Bone >= 0 ? bone : BoneTransform);
        if (!UpPass) return frame;
        var world = frame * placement;
        return Basis(Vector3.UnitZ, Level(world), world.Translation) * toEntity;
    }

    /** The frame's Y axis, or its X axis when Y is vertical. */
    private static Vector3 Level(Matrix4x4 frame)
    {
        var y = Vector3.Normalize(new Vector3(frame.M21, frame.M22, frame.M23));
        return MathF.Abs(1 - MathF.Abs(y.Z)) <= 1e-4f ? Vector3.Normalize(new Vector3(frame.M11, frame.M12, frame.M13)) : y;
    }

    /** X is a, Z is a x b normalized, Y is Z x X. */
    private static Matrix4x4 Basis(Vector3 a, Vector3 b, Vector3 position)
    {
        var z = Vector3.Normalize(Vector3.Cross(a, b));
        var y = Vector3.Cross(z, a);
        return new Matrix4x4(a.X, a.Y, a.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, position.X, position.Y, position.Z, 1);
    }
}

/**
 * One row of the sequence's location table with all its sources.
 * Frames holds every frame the sources give, up to Count; empty when a point is missing.
 */
public sealed record EffectLocation(int Index, uint Name, byte Count, byte Flags, IReadOnlyList<EffectSource> Sources)
{
    public IReadOnlyList<EffectFrame> Frames { get; init; } = [];
    /** The first source's kind and point hash. */
    public byte Kind => Sources.Count == 0 ? (byte)0 : Sources[0].Kind;
    public uint Point => Sources.Count == 0 ? 0 : Sources[0].Point;
    public bool World => Kind == 1;
}

/** A channel range a node needs: open while Min <= value < Max. */
public sealed record EffectGate(uint Channel, float Min, float Max)
{
    /** True while Min <= value < Max. */
    public bool Open(float value) => value >= Min && value < Max;
}

/** A random flow on the node's path: the node runs only when the flow picks Branch of Branches. */
public sealed record EffectRandomPick(int Flow, int Branch, int Branches);

/** What starts a particle node: its sequence, the flow nodes above it and its own timers. */
public sealed record EffectActivation
{
    public uint SequenceTag { get; init; }
    public int WorkIndex { get; init; } = -1;
    /** The sequence starts when the object spawns. */
    public bool AutoStart { get; init; }
    public bool Reachable { get; init; }
    /** A start condition kind other than a channel range is on the path. */
    public bool UnknownCondition { get; init; }
    /** The node has no timer, or a flow above it loops. */
    public bool Continuous { get; init; }
    public bool Timed { get; init; } = true;
    /** Start delays along the path, shortest of each range. */
    public float Delay { get; init; }
    /** The node's longest duration; zero when it has no timer. */
    public float Duration { get; init; }
    /** Time spent by earlier siblings of serial flows; null after a sibling with no timer. */
    public float? SerialStart { get; init; } = 0;
    public IReadOnlyList<EffectGate> Gates { get; init; } = [];
    public IReadOnlyList<EffectRandomPick> RandomPicks { get; init; } = [];

    /** The node can run once its sequence plays, apart from gates and random picks. */
    public bool Startable() => Reachable && !UnknownCondition && SerialStart is not null;

    /** The gates are open for these channel values; a missing channel reads 0. */
    public bool GatesOpen(Func<uint, float> channel) => GatesOpen(channel, static (read, hash) => read(hash));

    /** GatesOpen with caller state, so a per-frame caller passes a static delegate. */
    public bool GatesOpen<TState>(TState state, Func<TState, uint, float> channel)
    {
        for (int index = 0; index < Gates.Count; index++)
            if (!Gates[index].Open(channel(state, Gates[index].Channel))) return false;
        return true;
    }

    /** On by default: autostart, no random pick, every gate open, and continuous. */
    public bool PlaysByDefault(IReadOnlyDictionary<uint, float>? channels = null) =>
        AutoStart && Startable() && RandomPicks.Count == 0 && Continuous
        && GatesOpen(hash => channels?.GetValueOrDefault(hash) ?? 0);

    /** A short label for what starts the node. */
    public string Trigger()
    {
        if (!AutoStart) return "started by gameplay";
        if (!Reachable) return "not reached from the root";
        if (UnknownCondition) return "unknown start condition";
        if (SerialStart is null) return "after a sibling with no timer";
        if (RandomPicks.Count != 0) return "random branch";
        string timing = Continuous ? "continuous" : "plays once";
        return Gates.Count == 0 ? $"on at spawn, {timing}" : $"gated, {timing}";
    }
}

/** One lens-flare element: a screen-space material and its parameter block. */
public sealed record EffectFlareElement(uint Material, uint Parameters);

/** Decoded effect attached to a scene marker in the Effects layer. */
public sealed class EffectDescription
{
    public EffectKind Kind { get; init; }
    public uint EntityTag { get; init; }
    public uint SourceTag { get; init; }
    public int SourceOffset { get; init; }
    public uint NodeName { get; init; }
    public float StartMin { get; init; }
    public float StartMax { get; init; }
    public float DurationMin { get; init; }
    public float DurationMax { get; init; }
    public IReadOnlyList<EffectSystem> Systems { get; init; } = [];
    /** Per system, the location of each row byte; an empty list is the entity root. */
    public IReadOnlyList<IReadOnlyList<EffectLocation>> Locations { get; init; } = [];
    public IReadOnlyList<EffectFlareElement> FlareElements { get; init; } = [];
    /** Null when the node's sequence cannot be read. */
    public EffectActivation? Activation { get; init; }
    /** False when a node row names no system and the entity's first listed variant stands in. */
    public bool VariantResolved { get; init; } = true;
    /** Frame 0 of the first system's spawn slot: its bone, -1 at the root, and its offsets. */
    public int Bone { get; init; } = -1;
    public Matrix4x4 Local { get; init; } = Matrix4x4.Identity;
    public Matrix4x4 BoneTransform { get; init; } = Matrix4x4.Identity;
    private Matrix4x4 placement = Matrix4x4.Identity;
    /** The entity's world transform, scale and parent included; it applies last. */
    public Matrix4x4 Placement { get => placement; init => placement = value; }
    /** The world frame in the bind pose: Local * BoneTransform * Placement. */
    public Matrix4x4 Transform => Local * BoneTransform * placement;

    /** Sets the effect's world placement. */
    internal void Place(Matrix4x4 value) => placement = value;

    /** The location that feeds a transform slot of a system, or null when none does. */
    public EffectLocation? SlotLocation(int system, int slot) => SlotLocation(Systems, Locations, system, slot);

    /** Location feeding a system's transform slot, or null. */
    internal static EffectLocation? SlotLocation(IReadOnlyList<EffectSystem> systems,
        IReadOnlyList<IReadOnlyList<EffectLocation>> locations, int system, int slot)
    {
        var slots = system < locations.Count ? locations[system] : [];
        int row = systems[system].Emitter?.RowFor(slot, slots.Count) ?? (slot < slots.Count ? slot : -1);
        return row >= 0 ? slots[row] : null;
    }

    /** True when the spawn slot names a point the entity lacks; the game emits nothing. */
    public bool SpawnMissing(int system) => SpawnMissing(Systems, Locations, system);

    /** True when the system's spawn slot names a point the entity lacks. */
    internal static bool SpawnMissing(IReadOnlyList<EffectSystem> systems,
        IReadOnlyList<IReadOnlyList<EffectLocation>> locations, int system) =>
        systems[system].Emitter is { } emitter
        && SlotLocation(systems, locations, system, emitter.SpawnSlot) is { Frames.Count: 0 };

    /** Lens flares always run; a node without a readable sequence never runs by default. */
    public bool PlaysByDefault(IReadOnlyDictionary<uint, float>? channels = null) =>
        Kind == EffectKind.LensFlare || Activation?.PlaysByDefault(channels) == true;
}
