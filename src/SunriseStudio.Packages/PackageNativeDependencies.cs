using System.Buffers.Binary;

namespace SunriseStudio.Packages;

/** A native link whose target is absent or of the wrong kind; ExpectedCategory is what the target should be.
    InvalidDataException is sealed, so the details ride in Exception.Data and callers keep catching InvalidDataException. */
public sealed record NativeDependencyFailure(uint Tag, uint Target, string Role, string ExpectedCategory)
{
    private const string Key = "Solforge.NativeDependency";

    /** An AssetCategories name (Texture, Audio, Buffer, Shader, Material, ...) or "Unknown" is ExpectedCategory. */
    public InvalidDataException ToException()
    {
        var error = new InvalidDataException($"Asset {Tag:X8} has an invalid {Role} dependency {Target:X8}.");
        error.Data[Key] = this;
        return error;
    }

    /** The failure carried by an exception from ToException, or null for any other exception. */
    public static NativeDependencyFailure? From(Exception error) => error.Data[Key] as NativeDependencyFailure;
}

/** Resident graphics dependencies exclude mip payloads managed by texture streaming. */
public static class PackageNativeDependencies
{
    private const uint MaterialClass = 0x808071E8, ArrayMarker = 0x80809FBD;
    private const uint ParticleSystemClass = 0x80806E28, ParticleEmitterClass = 0x80806E2C;
    private const uint ParticleMeshClass = 0x80806E2E, ModelClass = 0x808073A5;
    private const uint TextureRowClass = 0x80807211, SamplerRowClass = 0x808073F3;
    // Event records name their bank at +0x14; banks are entries of type 26, subtype 5.
    private const uint SoundEventClass = 0x80809802;
    // Configs name their schema at +0x44; 0xFFFFFFFF means none.
    private const uint ConfigClass = 0x80809C36, ConfigSchemaClass = 0x80809BBB;
    // Entity construction rows: 12 bytes at +0x10, the config tag first.
    private const uint EntityClass = 0x80809C0F, ConstructionRowClass = 0x80809C04;
    private static readonly (int Offset, int Subtype)[] Stages =
        [(0x48, 1), (0xE8, -1), (0x188, -1), (0x228, -1), (0x2C8, 0), (0x368, 6)];

    /** The native 0x6000 path requires a streaming owner and cannot load from a resident manifest. */
    public static bool IsResident(uint typeInfo) => (typeInfo & 0xF000) != 0x6000;

    /** Staged references need a resolver that includes their unpublished entry metadata. */
    public static IReadOnlyList<uint> Read(PackageCatalog catalog, uint tag, uint reference,
        uint typeInfo, ReadOnlyMemory<byte> data, Func<uint, PackageEntry?>? resolve = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!IsResident(typeInfo)) return [];
        int type = (int)(typeInfo >> 9 & 127), subtype = (int)(typeInfo >> 6 & 7);
        var result = new HashSet<uint>();
        PackageEntry? Entry(uint target) => resolve is null
            ? catalog.TryGetEntry(target, out var entry) ? entry : null : resolve(target);
        void Link(uint target, Func<PackageEntry, bool> valid, string role)
        {
            if (target is 0 or uint.MaxValue) return;
            if (Entry(target) is not { } entry || !valid(entry))
                throw new NativeDependencyFailure(tag, target, role, ExpectedCategory(role, type, subtype)).ToException();
            if (target != tag && IsResident(entry.TypeInfo)) result.Add(target);
        }

        if (type is 32 or 33 or 34)
            Link(reference, entry => entry.Type == type + 8 && entry.SubType == subtype, "payload");
        else if (type is 40 or 41 or 42)
            Link(reference, entry => entry.Type == type - 8 && entry.SubType == subtype
                && entry.Reference == tag, "paired header");

        if (type == 32 && subtype is >= 1 and <= 3)
        {
            if (data.Length != 40 || U16(data, 12) != 0xCAFE)
                throw new InvalidDataException($"Texture {tag:X8} has an unsupported header.");
            Link(U32(data, 36), entry => entry.Type == 48, "streamed mip");
        }
        if (type is 8 or 16 && reference == 0x80804A53)
        {
            if (data.Length < 128) throw new InvalidDataException("An icon container is truncated.");
            foreach (int offset in new[] { 20, 24, 28, 32, 36, 40 })
                Link(U32(data, offset), entry => entry.Reference == 0x80804A69, "icon bitmap");
        }
        if (type is 8 or 16 && reference == 0x80804A55)
            foreach (int row in Rows(data, 8, 0x80804A59, 112))
                foreach (int offset in new[] { 4, 8, 12, 16, 20, 24 })
                    Link(U32(data, row + offset), entry => entry.Reference == 0x80804A69, "library bitmap");
        if (type is 8 or 16 && reference == 0x80804A69)
        {
            if (data.Length < 24) throw new InvalidDataException("An icon bitmap is truncated.");
            long descriptor = checked(16 + I64(data, 16));
            if (descriptor < 4 || descriptor > data.Length - 16)
                throw new InvalidDataException("An icon bitmap descriptor leaves its asset.");
            (uint Group, uint Frame) classes = U32(data, (int)descriptor - 4) switch
            {
                0x80804A67 => (0x80804A6C, 0x80804A6F),
                0x80804A65 => (0x80804A6A, 0x80804A6E),
                _ => throw new InvalidDataException("An icon bitmap has an unsupported group class.")
            };
            foreach (int group in Rows(data, (int)descriptor, classes.Group, 16))
                foreach (int frame in Rows(data, group, classes.Frame, 4))
                    Link(U32(data, frame), entry => entry.Type == 32 && entry.SubType is >= 1 and <= 3,
                        "bitmap texture");
        }
        // A Wwise event plays only when its bank is loaded with it.
        if (type is 8 or 16 && reference == SoundEventClass)
        {
            if (data.Length < 0x18) throw new InvalidDataException($"Sound event {tag:X8} is truncated.");
            Link(U32(data, 0x14), entry => entry.Type == 26 && entry.SubType == 5, "sound bank");
        }
        // An entity builds from its construction configs, so they load with it.
        if (type is 8 or 16 && reference == EntityClass)
            foreach (int row in Rows(data, 0x10, ConstructionRowClass, 12))
                Link(U32(data, row), entry => entry.Reference == ConfigClass, "entity config");
        // A config's schema is resident wherever the config is.
        if (type is 8 or 16 && reference == ConfigClass)
        {
            if (data.Length < 0x48) throw new InvalidDataException($"Config {tag:X8} is truncated.");
            Link(U32(data, 0x44), entry => entry.Reference == ConfigSchemaClass, "config schema");
        }
        if (type is 8 or 16 && reference == ParticleSystemClass)
        {
            if (data.Length < 0x20) throw new InvalidDataException($"Particle system {tag:X8} is truncated.");
            Link(U32(data, 0), entry => entry.Reference == ParticleEmitterClass, "particle emitter");
            foreach (int offset in new[] { 4, 8, 0x10, 0x14, 0x1C })
                Link(U32(data, offset), entry => entry.Reference == MaterialClass, "particle material");
            Link(U32(data, 0x18), entry => entry.Reference == ParticleMeshClass, "particle mesh");
        }
        if (type is 8 or 16 && reference == ParticleMeshClass)
            foreach (int row in Rows(data, 0x10, 0x808073A4, 4))
                Link(U32(data, row), entry => entry.Reference == ModelClass, "particle model");
        if (type is 8 or 16 && reference == ModelClass)
            foreach (int row in Rows(data, 0x10, 0x80807378, 0x88))
            {
                foreach (int offset in new[] { 0, 4, 8, 16 })
                    Link(U32(data, row + offset), entry => entry.Type == 32
                        && entry.SubType == (offset == 16 ? 6 : 4), "model buffer");
                foreach (int part in Rows(data, row + 0x18, 0x8080737E, 0x20))
                    Link(U32(data, part), entry => entry.Reference == MaterialClass, "model material");
            }
        if (type is not (8 or 16) || reference != MaterialClass) return result.Order().ToArray();
        if (data.Length < 0x400 || I64(data, 0) != data.Length)
            throw new InvalidDataException($"Material {tag:X8} has an unsupported size.");
        foreach (var (offset, stageSubtype) in Stages)
        {
            Link(U32(data, offset), entry => entry.Type == 33
                && (stageSubtype < 0 || entry.SubType == stageSubtype), "shader");
            Link(U32(data, offset + 0x84), entry => entry.Type == 32 && entry.SubType == 7,
                "constant buffer");
            foreach (int row in Rows(data, offset + 8, TextureRowClass, 8))
                Link(U32(data, row + 4), entry => entry.Type == 32 && entry.SubType is >= 1 and <= 3,
                    "texture");
            foreach (int row in Rows(data, offset + 0x40, SamplerRowClass, 16))
                Link(U32(data, row), entry => entry.Type == 34 && entry.SubType == 1, "sampler");
        }
        return result.Order().ToArray();
    }

    /** The kind of asset a link role points at, in AssetCategories names. */
    private static string ExpectedCategory(string role, int type, int subtype) => role switch
    {
        "payload" or "paired header" => type is 32 or 40 && subtype is >= 1 and <= 3 ? "Texture" : "Buffer",
        "streamed mip" or "icon bitmap" or "library bitmap" or "bitmap texture" or "texture" => "Texture",
        "sound bank" => "Audio",
        "model buffer" or "constant buffer" => "Buffer",
        "shader" => "Shader",
        "particle material" or "model material" => "Material",
        "particle emitter" or "particle mesh" => "Effect",
        "particle model" => "Entity model",
        "entity config" or "config schema" => "Entity / Definition",
        _ => "Unknown"
    };

    /** Typed arrays must retain their marker, repeated count and row class. */
    private static IReadOnlyList<int> Rows(ReadOnlyMemory<byte> data, int field, uint rowClass, int stride)
    {
        if (field < 0 || field > data.Length - 16)
            throw new InvalidDataException("A native dependency array is truncated.");
        long count = I64(data, field);
        if (count == 0) return [];
        long header = checked(field + 8 + I64(data, field + 8));
        if (count < 0 || header < 4 || header > data.Length - 16
            || count > (data.Length - header - 16) / stride
            || U32(data, (int)header - 4) != ArrayMarker || I64(data, (int)header) != count
            || U32(data, (int)header + 8) != rowClass)
            throw new InvalidDataException("A native dependency array is malformed.");
        return Enumerable.Range(0, checked((int)count)).Select(index => checked((int)header + 16 + index * stride)).ToArray();
    }

    private static ushort U16(ReadOnlyMemory<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.Span[offset..]);
    private static uint U32(ReadOnlyMemory<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.Span[offset..]);
    private static long I64(ReadOnlyMemory<byte> data, int offset) =>
        BinaryPrimitives.ReadInt64LittleEndian(data.Span[offset..]);
}
