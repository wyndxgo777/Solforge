using System.Buffers.Binary;
using SunriseStudio.Packages;

namespace SunriseStudio.Projects;

/** One 32-bit word whose base and authored values differ; a missing side (size change) reads as null. */
public sealed record TagWordDiff(int Offset, uint? Base, uint? Authored)
{
    public float? BaseFloat => Base is uint value ? BitConverter.UInt32BitsToSingle(value) : null;
    public float? AuthoredFloat => Authored is uint value ? BitConverter.UInt32BitsToSingle(value) : null;
}

/**
 * Base game bytes of a tag next to the project's authored bytes (Solforge Phase 16). The base side
 * always comes from PackageCatalog.ReadOriginal, so the overlay never feeds itself; the authored side
 * is what StudioProject.ReadAsset reconstructs from the saved delta. Words compare at the same offset,
 * which is how tag values change; a size change lists the tail words with one side missing.
 */
public sealed class ProjectTagDiff
{
    /** Word rows kept for the viewer; ChangedWords still counts every difference. */
    public const int MaximumRows = 65536;

    public uint TagHash { get; }
    public byte[] Base { get; }
    public byte[] Authored { get; }
    public IReadOnlyList<TagWordDiff> Words { get; }
    public int ChangedWords { get; }
    public int ChangedBytes { get; }
    public bool Truncated => ChangedWords > Words.Count;
    public bool Identical => ChangedBytes == 0 && Base.Length == Authored.Length;

    public ProjectTagDiff(uint tagHash, byte[] baseBytes, byte[] authoredBytes)
    {
        ArgumentNullException.ThrowIfNull(baseBytes);
        ArgumentNullException.ThrowIfNull(authoredBytes);
        (TagHash, Base, Authored) = (tagHash, baseBytes, authoredBytes);
        int common = Math.Min(baseBytes.Length, authoredBytes.Length), longest = Math.Max(baseBytes.Length, authoredBytes.Length);
        int bytes = longest - common;
        for (int i = 0; i < common; i++) if (baseBytes[i] != authoredBytes[i]) bytes++;
        ChangedBytes = bytes;
        var words = new List<TagWordDiff>();
        int changed = 0;
        for (int offset = 0; offset < longest; offset += 4)
        {
            uint? left = Word(baseBytes, offset), right = Word(authoredBytes, offset);
            if (left == right && offset + 4 <= common) continue;
            if (left == right && Tail(baseBytes, offset).SequenceEqual(Tail(authoredBytes, offset))) continue;
            changed++;
            if (words.Count < MaximumRows) words.Add(new(offset, left, right));
        }
        (Words, ChangedWords) = (words, changed);
    }

    /** Compares the installed original of a changed (or unchanged) tag with the project's version. */
    public static ProjectTagDiff Compare(StudioProject project, uint tagHash, PackageCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(catalog);
        return new(tagHash, catalog.ReadOriginal(tagHash), project.ReadAsset(tagHash, catalog));
    }

    /** Little-endian word at offset; a partial last word is zero-padded; past the end is null. */
    private static uint? Word(byte[] data, int offset)
    {
        if (offset >= data.Length) return null;
        if (offset + 4 <= data.Length) return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        Span<byte> padded = stackalloc byte[4];
        data.AsSpan(offset).CopyTo(padded);
        return BinaryPrimitives.ReadUInt32LittleEndian(padded);
    }

    private static ReadOnlySpan<byte> Tail(byte[] data, int offset) =>
        offset >= data.Length ? ReadOnlySpan<byte>.Empty : data.AsSpan(offset, Math.Min(4, data.Length - offset));
}
