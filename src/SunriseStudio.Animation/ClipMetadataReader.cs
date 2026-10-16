/** File: Reads clip timing and payload-section metadata from an animation clip tag. */
using System.Buffers.Binary;
using SunriseStudio.Packages;

namespace SunriseStudio.Animation;

/** One payload section of a clip: its pointer field, data offset and class hash. */
public sealed record AnimationPayloadSection(int PointerField, int DataOffset, uint ClassHash);

/** Timing is decoded; payload classes do not imply supported pose decompression. */
public sealed record AnimationClipInfo(uint TagHash, int FrameCount, float SampleRate,
    IReadOnlyList<AnimationPayloadSection> PayloadSections)
{
    public float DurationSeconds => (FrameCount - 1) / SampleRate;
}

/** Reads an animation clip's frame count and typed payload sections. */
public sealed class ClipMetadataReader(PackageCatalog catalog)
{
    /** The native duration reader uses a signed frame count and a fixed 30 Hz sample rate. */
    public AnimationClipInfo Read(uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != 0x80808F49)
            throw new NotSupportedException("Tag is not a build-86657 animation clip.");
        byte[] data = catalog.Read(tag);
        if (data.Length < 0x190) throw new InvalidDataException("Animation clip header is truncated.");
        int frames = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(0x13C));
        if (frames < 1) throw new InvalidDataException("Animation clip has no positive frame count.");
        var sections = new List<AnimationPayloadSection>();
        for (int field = 0x10; field <= 0x60; field += 8)
        {
            long relative = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(field));
            if (relative == 0) continue;
            long offset = checked(field + relative);
            if (offset < 4 || offset > data.Length - 4)
                throw new InvalidDataException("Animation payload pointer exceeds the clip.");
            uint classHash = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)offset - 4));
            if (classHash < 0x80800000 || classHash > 0x8080FFFF)
                throw new InvalidDataException("Animation payload has no typed resource marker.");
            sections.Add(new AnimationPayloadSection(field, (int)offset, classHash));
        }
        return new AnimationClipInfo(tag, frames, 30, sections.AsReadOnly());
    }
}
