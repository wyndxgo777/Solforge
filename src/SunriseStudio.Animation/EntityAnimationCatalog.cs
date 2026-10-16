/** File: Lists the animation clips an entity config references through its animation definitions. */
using System.Buffers.Binary;
using SunriseStudio.Packages;

namespace SunriseStudio.Animation;

/** One clip reference from an entity config: definition tag, index, clip tag and recovered name. */
public sealed record EntityAnimationReference(uint ConfigTag, uint DefinitionTag, int Index, uint ClipTag)
{
    public string? Name { get; init; }
    public IReadOnlyList<AnimationClipNameEvidence> NameEvidence { get; init; } = [];
    public string? NameDiagnostic { get; init; }
}

/** Clip indices belong to each animation definition and are not global identifiers. */
public sealed class EntityAnimationCatalog(PackageCatalog catalog)
{
    /** Grouped clip views must not choose the first of conflicting owner-specific names. */
    public static string? UniqueName(IEnumerable<EntityAnimationReference> references) =>
        AnimationClipNames.Unique(references.SelectMany(reference => reference.NameEvidence)
            .Select(evidence => evidence.Name));

    /** Follow the typed animation component to its installed clip list. */
    public IReadOnlyList<EntityAnimationReference> Read(uint entityTag, bool resolveNames = false,
        CancellationToken cancellationToken = default)
    {
        if (Class(entityTag) != 0x80809C0F)
            throw new NotSupportedException("Animation catalog input must be an entity class tag.");
        byte[] entity = catalog.Read(entityTag);
        var result = new List<EntityAnimationReference>();
        var definitions = new HashSet<uint>();
        foreach (int row in Rows(entity, 0x10, 0x80809C04, 12))
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint configTag = U32(entity, row);
            if (Class(configTag) != 0x80809C36) continue;
            byte[] config = catalog.Read(configTag);
            int primary = Pointer(config, 0x10);
            if (U32(config, primary - 4) != 0x808036E2) continue;
            uint definitionTag = U32(config, checked(primary + 0xD0));
            if (definitionTag is 0 or uint.MaxValue) continue;
            if (Class(definitionTag) != 0x808036F6)
                throw new InvalidDataException("Animation component references an unexpected definition class.");
            if (!definitions.Add(definitionTag)) continue;
            result.AddRange(ReadDefinition(definitionTag, resolveNames, cancellationToken)
                .Select(reference => reference with { ConfigTag = configTag }));
        }
        return result.AsReadOnly();
    }

    /** Names remain tied to the definition row and its signed clip-list index. */
    public IReadOnlyList<EntityAnimationReference> ReadDefinition(uint definitionTag, bool resolveNames = false,
        CancellationToken cancellationToken = default)
    {
        if (Class(definitionTag) != 0x808036F6)
            throw new NotSupportedException("Animation definition has an unsupported class.");
        byte[] definition = catalog.Read(definitionTag);
        uint[] clips = Rows(definition, 8, 0x80808F48, 4).Select(row => U32(definition, row)).ToArray();
        IReadOnlyDictionary<int, AnimationClipNames.Label> labels = new Dictionary<int, AnimationClipNames.Label>();
        string? diagnostic = null;
        if (resolveNames)
        {
            var paths = new Dictionary<uint, IReadOnlyList<AnimationClipNames.EventPath>>();
            try
            {
                labels = AnimationClipNames.Resolve(definition, clips, tag =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Class(tag) != 0x80808F49) return [];
                    if (!paths.TryGetValue(tag, out var value))
                        paths[tag] = value = AnimationClipNames.ReadPaths(catalog.Read(tag), Class);
                    return value;
                });
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException or KeyNotFoundException
                or IOException or UnauthorizedAccessException)
            { diagnostic = error.Message; }
        }
        var result = new List<EntityAnimationReference>();
        for (int index = 0; index < clips.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint clip = clips[index];
            if (clip is 0 or uint.MaxValue) continue;
            if (Class(clip) != 0x80808F49)
                throw new InvalidDataException($"Animation slot {index} references an unsupported clip class.");
            labels.TryGetValue(index, out var label);
            result.Add(new(0, definitionTag, index, clip)
            { Name = label?.Name, NameEvidence = label?.Evidence ?? [], NameDiagnostic = diagnostic });
        }
        return result.AsReadOnly();
    }

    /** Layer-name hashes in run order; a blend entry's layer index points into this list. */
    public IReadOnlyList<uint> ReadLayerNames(uint definitionTag)
    {
        if (Class(definitionTag) != 0x808036F6)
            throw new NotSupportedException("Animation definition has an unsupported class.");
        byte[] definition = catalog.Read(definitionTag);
        return Rows(definition, 0x58, 0x80808F1C, 4).Select(row => U32(definition, row)).ToArray();
    }

    private uint Class(uint tag) => catalog.TryGetEntry(tag, out var entry)
        && AssetCategories.IsStructured(entry) ? entry.Reference : 0;
    private static ReadOnlySpan<byte> Slice(byte[] data, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > data.Length - count)
            throw new InvalidDataException("Animation reference range exceeds its tag.");
        return data.AsSpan(offset, count);
    }
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(data, offset, 4));
    private static int Pointer(byte[] data, int field) => checked((int)(field
        + BinaryPrimitives.ReadInt64LittleEndian(Slice(data, field, 8))));

    /** Array classes and byte extents are checked before exposing any clip link. */
    private static int[] Rows(byte[] data, int field, uint expected, int stride)
    {
        long count = BinaryPrimitives.ReadInt64LittleEndian(Slice(data, field, 8));
        if (count < 0 || count > 1_000_000) throw new InvalidDataException("Invalid animation reference count.");
        if (count == 0) return [];
        int header = Pointer(data, field + 8);
        if (U32(data, header - 4) != 0x80809FBD || U32(data, header + 8) != expected)
            throw new InvalidDataException("Animation reference array has an unexpected element class.");
        int start = checked(header + 16);
        _ = Slice(data, start, checked((int)count * stride));
        return Enumerable.Range(0, (int)count).Select(i => start + i * stride).ToArray();
    }
}
