using System.Buffers.Binary;
using System.Security.Cryptography;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

namespace SunriseStudio.Authoring;

/** One burst of a sequence: its system, its node's duration and its attachment point. */
public sealed record WeaponParticleBurst(uint SystemTag, float DurationSeconds, uint AttachmentHash);

/** Builds particle sequences and their entities for the firing row. */
public static class WeaponParticleBinding
{
    public const uint EntityTemplate = 0x80C5CE35, ConfigTemplate = 0x80C5CE34, SequenceClass = 0x808084E9;
    // Two timed particle nodes under one parallel root, each on its own location.
    public const uint PairEntityTemplate = 0x80B3B903, PairConfigTemplate = 0x80B3B902;
    // Sequence lifecycle byte at resource +0x2E0: bit 0 starts on spawn, bit 1 retires the node when it ends.
    public const int LifecycleOffset = 0x2E0;
    public const byte StartOnSpawn = 1, RetireOnEnd = 2;
    private const byte LifecycleStartAndRetire = StartOnSpawn | RetireOnEnd;
    // The template locations use the object frame (kind 0, flags 0x3F). Kind 3 with flags 0x22 is the
    // first-person pose; kind 2 would place the burst on the world weapon.
    private const byte PointLocation = 3, ObjectFrameMask = 0x3F, PointMask = 0x22;
    // The template's per-location flag array is anchored before the sequence resource.
    private const int LocationFlagsField = 0x220;

    /** A checked profile: entity, sequence, the sequence's reciprocal owner pairs and its stock systems. */
    private sealed record Profile(uint Entity, string EntityHash, uint Config, string ConfigHash,
        (int Primary, int Resource)[] Owners, uint[] Systems);

    private static readonly Profile Single = new(EntityTemplate,
        "419913E5CE81D541F528E4D0C9D5718EC472A3698E0A0B5001E48184757767F2", ConfigTemplate,
        "B9661427A12D8F0509B0AEEC4C26654C20B99B96E1128AF70AA05E76420A6B56",
        [(0x90, 0x5A8), (0x1E0, 0x748), (0x260, 0x798), (0x290, 0x818), (0x300, 0x8B0), (0x340, 0x8D0),
         (0x3C0, 0x960), (0x400, 0x980)], [0x80C5CE33]);

    private static readonly Profile Pair = new(PairEntityTemplate,
        "5C14481F84C267EDA08C50B0DB7F765D6DF16D09444E9257324DA97DF311B3A5", PairConfigTemplate,
        "E1D0B0F94F153178E59F813B7DB6DC904A104E9E02525826995F931541A13333",
        [(0x90, 0x638), (0x1E0, 0x7D8), (0x260, 0x828), (0x290, 0x8A8), (0x300, 0x940), (0x340, 0x960),
         (0x3C0, 0x9F0), (0x3F0, 0xA08), (0x430, 0xA28), (0x490, 0xC98)], [0x80B3B901, 0x80C67FD4]);

    private static Profile For(int bursts) => bursts switch
    {
        1 => Single,
        2 => Pair,
        _ => throw new NotSupportedException("A weapon particle sequence holds one or two bursts.")
    };

    /** The checked entity and sequence templates for a sequence of one or two bursts. */
    public static uint EntityTemplateFor(int bursts) => For(bursts).Entity;
    /** Config template tag for a burst count. */
    public static uint ConfigTemplateFor(int bursts) => For(bursts).Config;

    /**
     * The timed sequence alone, for a firing row slot entity the weapon spawns once and restarts per shot.
     * @param bursts One or two bursts; each plays on its own location.
     * @param entry Package entry of the sequence record.
     * @return The sequence record; its lifecycle neither starts on spawn nor retires the node.
     */
    public static ProjectAssetAddition PrepareSequence(PackageCatalog catalog, IReadOnlyList<WeaponParticleBurst> bursts,
        ushort packageId, int entry, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!PackageWriter.IsNewPackageId(packageId) || catalog.ContainsInstalledPackage(packageId) || entry is < 0 or > 8191)
            throw new InvalidDataException("A per-shot particle sequence needs an unused package entry.");
        uint tag = checked(0x80800000u + ((uint)packageId << 13) + (uint)entry);
        if (catalog.ContainsTag(tag) || bursts.Any(burst => burst.SystemTag is 0 or uint.MaxValue
                || burst.SystemTag < 0x80800000 || burst.SystemTag == tag))
            throw new InvalidDataException("The per-shot particle sequence overlaps another asset.");
        var profile = For(bursts.Count);
        byte[] config = BuildConfig(catalog, bursts, tag);
        catalog.TryGetEntry(profile.Config, out var configEntry);
        return new(name + ".sequence", config, new(packageId, configEntry.Reference, configEntry.TypeInfo,
            name + ".sequence", entry), profile.Config);
    }

    /** The checked profile entity for one or two bursts, with its sequence link moved to the given sequence. */
    public static byte[] ProfileEntity(PackageCatalog catalog, uint sequenceTag, int bursts = 1)
    {
        var profile = For(bursts);
        return WeaponConfigEdits.RetagRoot(Read(catalog, profile.Entity, 0x80809C0F, profile.EntityHash), profile.Config, sequenceTag);
    }

    /** The resource offset of a sequence config, or -1 when the config is not a sequence. */
    public static int SequenceResource(byte[] config)
    {
        if (config.Length < 32) return -1;
        long resource = 24 + I64(config, 24);
        return resource >= 4 && resource <= config.Length - LifecycleOffset - 4 && U32(config, (int)resource - 4) == SequenceClass
            ? (int)resource : -1;
    }

    /** Refuses a system that is not a CPU emitter with one shared frame slot. */
    public static void ValidateSystem(PackageCatalog catalog, uint systemTag)
    {
        var system = new EffectReader(catalog).ReadSystem(systemTag);
        if (system is null || system.GpuSimulated || system.Emitter is not { SpawnSlot: 0, EmitterSlot: 0, SlotCount: 1 })
            throw new NotSupportedException("Weapon particle binding supports a CPU emitter with one shared frame slot.");
    }

    /**
     * The checked profile with each burst's system, duration and point written in, retagged to its destination.
     * Burst i plays on work node i, which spawns and simulates in location i.
     */
    private static byte[] BuildConfig(PackageCatalog catalog, IReadOnlyList<WeaponParticleBurst> bursts, uint destination)
    {
        var profile = For(bursts.Count);
        foreach (var burst in bursts)
        {
            if (!float.IsFinite(burst.DurationSeconds) || burst.DurationSeconds <= 0 || burst.DurationSeconds > 60)
                throw new InvalidDataException("Particle duration must be greater than zero and at most 60 seconds.");
            if (burst.AttachmentHash is 0 or uint.MaxValue)
                throw new InvalidDataException("The particle attachment name cannot be invalid.");
            if (catalog.ContainsTag(burst.SystemTag)) ValidateSystem(catalog, burst.SystemTag);
        }
        byte[] config = Read(catalog, profile.Config, 0x80809C36, profile.ConfigHash);
        int resource = Pointer(config, 24);
        if (U32(config, resource - 4) != SequenceClass
            || (config[resource + LifecycleOffset] & LifecycleStartAndRetire) != LifecycleStartAndRetire)
            throw new InvalidDataException("The particle sequence lost its finite spawn lifecycle.");
        // A firing slot node is spawned once at equip; a sequence that retires deletes it after the first shot.
        config[resource + LifecycleOffset] &= unchecked((byte)~LifecycleStartAndRetire);
        int[] works = NativeRows.Rows(config, resource + 0x168, 0x808093E6, 24);
        int[] sources = NativeRows.Rows(config, resource + 0x1C0, 0x80808615, 16);
        int[] masks = NativeRows.Rows(config, resource + 0x1B0, 0x80808617, 6), flags = NativeRows.Rows(config, LocationFlagsField, 0x80808618, 2);
        int[] names = NativeRows.Rows(config, resource + 0x1D0, 0x80808614, 4);
        if (works.Length != bursts.Count || sources.Length != 2 || masks.Length != 2 || flags.Length != 2 || names.Length != 2)
            throw new InvalidDataException("The particle sequence layout changed.");
        for (int index = 0; index < bursts.Count; index++)
        {
            var burst = bursts[index];
            int wrapper = Pointer(config, works[index] + 16), body = wrapper + 16;
            if (U32(config, wrapper - 4) != 0x80806CC6 || config[body + 0x20] != 4 || (config[body + 8] & 0x18) != 0)
                throw new InvalidDataException("A particle sequence node is no longer a timed particle node.");
            int[] systems = NativeRows.Rows(config, body + 0x30, 0x80806CC8, 24);
            if (systems.Length != 1 || U32(config, systems[0] + 16) != profile.Systems[index])
                throw new InvalidDataException("The particle system binding changed.");
            int[] slots = NativeRows.Rows(config, systems[0], 0x80800009, 1);
            if (slots.Length == 0 || config[slots[0]] != index)
                throw new InvalidDataException("The particle spawn frame mapping changed.");
            int source = sources[index];
            if (config[source] != 0 || config[source + 2] != 1 || config[source + 12] != byte.MaxValue
                || config[masks[index] + 4] != ObjectFrameMask || U32(config, source + 4) != ObjectFrameMask
                || config[flags[index]] != ObjectFrameMask)
                throw new InvalidDataException("The particle sequence needs its own object frame.");
            Put32(config, systems[0] + 16, burst.SystemTag);
            // The location's mask is written in its source row and in both side arrays.
            config[source] = PointLocation;
            Put32(config, source + 4, PointMask); config[masks[index] + 4] = PointMask; config[flags[index]] = PointMask;
            Put32(config, source + 8, burst.AttachmentHash); Put32(config, names[index], burst.AttachmentHash);
            // The one-slot emitter spawns and simulates in its own location.
            Put64(config, systems[0], 1); Put64(config, Pointer(config, systems[0] + 8), 1);
            PutFloat(config, body + 0x14, burst.DurationSeconds); PutFloat(config, body + 0x18, burst.DurationSeconds);
        }
        RetagConfig(config, profile, destination);
        return config;
    }

    private static byte[] Read(PackageCatalog catalog, uint tag, uint type, string hash)
    {
        byte[] bytes = catalog.Read(tag);
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != type
            || Convert.ToHexString(SHA256.HashData(bytes)) != hash)
            throw new NotSupportedException("Weapon particles need the unchanged checked effect profile.");
        return (byte[])bytes.Clone();
    }

    /** Every owner pair of the profile retains its types and reciprocal offsets. */
    private static void RetagConfig(byte[] bytes, Profile profile, uint destination)
    {
        var owners = new HashSet<int>();
        foreach (var (primary, resource) in profile.Owners)
        {
            if (U32(bytes, primary) != profile.Config || U32(bytes, resource) != profile.Config
                || I64(bytes, primary + 8) != resource || I64(bytes, resource + 8) != primary)
                throw new InvalidDataException("A particle sequence owner pair is not reciprocal.");
            owners.Add(primary); owners.Add(resource);
        }
        for (int at = 0; at + 4 <= bytes.Length; at += 4)
            if (U32(bytes, at) == profile.Config && !owners.Contains(at))
                throw new NotSupportedException("An unclassified particle owner cannot be cloned.");
        foreach (int at in owners) Put32(bytes, at, destination);
    }

    private static int Pointer(byte[] bytes, int at) => checked(at + (int)I64(bytes, at));
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static long I64(byte[] bytes, int at) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
    private static void Put32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void Put64(byte[] bytes, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
    private static void PutFloat(byte[] bytes, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
}
