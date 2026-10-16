using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Interchange;

/** Native reference collection of AssetBundle. */
public static partial class AssetBundle
{
    /** Reference evidence comes from native readers, never editable manifest claims. */
    private static IReadOnlyList<BundleReference> NativeReferences(PackageCatalog catalog, PackageEntry entry,
        byte[] bytes, CancellationToken token, List<BundleNotice>? notices = null)
    {
        var result = new List<BundleReference>();
        uint U32(int p) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p, 4));
        int Relative(int p) => checked((int)(p + BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(p, 8))));
        void Link(uint target, int offset, string role)
        {
            if (!catalog.ContainsTag(target) || target is 0 or uint.MaxValue) return;
            if (offset == -1 ? entry.Type is not (32 or 33 or 34 or 40 or 41 or 42 or 48) || entry.Reference != target
                : offset < 0 || offset > bytes.Length - 4 || U32(offset) != target)
                throw new InvalidDataException("Physical native reference does not match its source.");
            result.Add(new(target, offset, true, role));
        }
        void Typed(int p, uint expected, string role)
        {
            if (catalog.TryGetEntry(U32(p), out var target) && target.Reference == expected
                && AssetCategories.IsStructured(target)) Link(target.TagHash, p, role);
        }
        IEnumerable<int> Rows(int field, uint expected, int stride)
        {
            long count = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(field, 8));
            if (count == 0) return [];
            int header = Relative(field + 8);
            if (count < 0 || count > 1_000_000 || header < 4 || header > bytes.Length - 16
                || U32(header - 4) != 0x80809FBD || U32(header + 8) != expected
                || BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(header)) != count
                || count > (bytes.Length - header - 16) / stride)
                throw new InvalidDataException("Native reference array is invalid.");
            return Enumerable.Range(0, (int)count).Select(index => header + 16 + index * stride);
        }
        try
        {
            foreach (var link in new AssetConnectionReader(catalog).ReadNativeReferences(entry.TagHash, token))
                Link(link.TagHash, link.SourceOffset, link.Role);
            if (catalog.TryGetEntry(entry.Reference, out var pairedHeader)
                && pairedHeader.SubType == entry.SubType)
            {
                if (entry.Type is 40 or 41 or 42 && pairedHeader.Type == entry.Type - 8
                    && pairedHeader.Reference == entry.TagHash)
                    Link(pairedHeader.TagHash, -1, "Paired header metadata");
                if (entry.Type == 48 && pairedHeader.Type == 32 && pairedHeader.SubType is >= 1 and <= 3)
                {
                    byte[] header = catalog.Read(pairedHeader.TagHash);
                    if (header.Length == 40 && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12)) == 0xCAFE
                        && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(36)) == entry.TagHash)
                        Link(pairedHeader.TagHash, -1, "Streamed header metadata");
                }
            }
            if (entry.Type == 32 && entry.SubType is >= 1 and <= 3 && bytes.Length == 40)
                Link(U32(0x24), 0x24, "Streamed mips");
            if (!AssetCategories.IsStructured(entry)) return result;
            if (entry.Reference == 0x80809C36 && bytes.Length >= 0x20)
            {
                foreach (int field in new[] { 0x10, 0x18 })
                {
                    int target = Relative(field);
                    if (target >= 4 && target <= bytes.Length - 4 && U32(target) == entry.TagHash)
                        Link(entry.TagHash, target, "Config identity");
                }
                int resource = Relative(0x18);
                if (resource >= 4 && resource <= bytes.Length - 0x320 && U32(resource - 4) == 0x808072BD)
                    foreach (int row in Rows(resource + 0x310, 0x80800014, 4)) Typed(row, EffectReader.MaterialClass, "External material");
                for (int marker = 8; marker + 0x50 <= bytes.Length; marker += 4)
                    if (U32(marker) == 0x80806CC5 && U32(marker - 4) == entry.TagHash && U32(marker - 8) == 0x80806CC6)
                    {
                        Link(entry.TagHash, marker - 4, "Particle node owner");
                        foreach (int row in Rows(marker + 0x3C, 0x80806CC8, 0x18)) Typed(row + 0x10, EffectReader.SystemClass, "Particle system");
                    }
                for (int header = 4; header + 16 <= bytes.Length; header += 4)
                    if (U32(header - 4) == 0x80809FBD && U32(header + 8) == 0x80800014)
                    {
                        long count = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(header));
                        if (count < 0 || count > (bytes.Length - header - 16) / 4) continue;
                        for (int i = 0; i < count; i++) Typed(header + 16 + i * 4, EffectReader.SystemClass, "Particle variant");
                    }
            }
            if (entry.Reference == EffectReader.MaterialClass && bytes.Length >= 0x230)
            {
                foreach (int stage in new[] { 0x48, 0xE8, 0x188, 0x228, 0x2C8, 0x368 })
                {
                    if (stage + 0x88 > bytes.Length) continue;
                    if (catalog.TryGetEntry(U32(stage + 0x84), out var constant) && constant.Type == 32 && constant.SubType == 7)
                        Link(constant.TagHash, stage + 0x84, "Material constants");
                    foreach (int row in Rows(stage + 0x40, 0x808073F3, 16))
                        if (catalog.TryGetEntry(U32(row), out var sampler) && sampler.Type == 34 && sampler.SubType == 1)
                            Link(sampler.TagHash, row, "Material sampler");
                }
                foreach (int field in new[] { 0xE8, 0x188, 0x228 })
                    if (catalog.TryGetEntry(U32(field), out var shader) && shader.Type == 33)
                    {
                        Link(shader.TagHash, field, "Additional shader stage");
                        foreach (int row in Rows(field + 8, 0x80807211, 8))
                            if (catalog.TryGetEntry(U32(row + 4), out var texture) && texture.Type == 32 && texture.SubType is >= 1 and <= 3)
                                Link(texture.TagHash, row + 4, "Additional stage texture");
                    }
            }
            if (entry.Reference == EffectReader.SystemClass && bytes.Length >= 0x20)
            {
                Typed(0, EffectReader.EmitterClass, "Emitter");
                foreach (int field in new[] { 4, 8, 0x10, 0x14 }) Typed(field, EffectReader.MaterialClass, "Particle material");
                Link(U32(0x18), 0x18, "Particle mesh");
            }
            if (entry.Reference == EffectReader.FlareClass)
                foreach (int row in Rows(0x20, 0x80806F6D, 12))
                { Typed(row, EffectReader.MaterialClass, "Flare material"); Link(U32(row + 4), row + 4, "Flare parameters"); }
        }
        catch (Exception error) when (Recoverable(error))
        { notices?.Add(new("Dependency", "Partial", error.Message, entry.TagHash)); }
        if (AssetCategories.IsStructured(entry))
        {
            var inspection = new AssetInspectionService().Inspect(new(entry.TagHash, entry.Reference, bytes,
                catalog.GetName(entry.TagHash) ?? "Asset", catalog.GetName, catalog.ContainsTag, catalog.Read));
            foreach (var candidate in inspection.References)
                if (!result.Any(value => value.Offset == candidate.Offset && value.TargetTag == candidate.TagHash))
                    result.Add(new(candidate.TagHash, candidate.Offset, false, "Unverified reference"));
        }
        return result.DistinctBy(value => (value.Offset, value.TargetTag, value.Verified)).ToArray();
    }
}
