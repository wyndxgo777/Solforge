using System.Buffers.Binary;
using System.Security.Cryptography;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/** Compiled weapon art entity and its config. */
public sealed record PreparedWeaponArtEntity(uint EntityTag, uint ConfigTag,
    IReadOnlyList<ProjectAssetAddition> Assets);

/** Art roots retain their external frame bindings while their model component is cloned. */
public static class WeaponAssetEntityCompiler
{
    private sealed record Profile(uint Config, uint Model, string RootHash, string ConfigHash);
    private static readonly IReadOnlyDictionary<uint, Profile> Profiles = new Dictionary<uint, Profile>
    {
        [0x81A6B357] = new(0x81A6B355, 0x81A6B350,
            "36D53E070E3D2C451C8109EC41EFF3EDF517E24E7D8E451CADE9A64A5FBFB8AB",
            "64F7F4A72ED5850E77C0434AD3DA95BDEE4D05E0699BDB25BB707F43A63B2A74"),
        [0x81A6B3A5] = new(0x81A6B3A4, 0x81A6B39F,
            "C69DF1565D8045C9748A30812DB3766773935A79727A1E1F470999FABB9D230C",
            "1EB2ADBC0170B134AFCB5116364FA974B933D2E9751315FB32E3754573F2C50C"),
        [0x81A6B3EC] = new(0x81A6B3EB, 0x81A6B3E7,
            "0175E4AF5D8E98B70463A0FEA9157ECA8EE5B0F34E861633D5052E7D088E8224",
            "CAD72E7C0D45A74C65279B01EB504EF947E009CB40D014BB557597711CEDA9E8"),
        [0x81A6B42E] = new(0x81528E8A, 0x81528E89,
            "637C2EA51AED87FBEB05700F08667E5A4A109494C76A5220D910F182DF579C62",
            "D16F832B7DE58082B4423259A74DBDE1A4E100C5ED5CA59464281EEFA22B90EB")
    };

    /** True only for the four art selectors and entities the compiler is built for. */
    public static bool Supports(IReadOnlyList<WeaponArtVariant> variants)
    {
        var expected = new Dictionary<int, uint>
        { [0] = 0x81A6B357, [1] = 0x81A6B3A5, [21] = 0x81A6B3EC, [3] = 0x81A6B42E };
        return variants.Count == expected.Count && variants.Select(value => value.Selector).Distinct().Count() == expected.Count
            && variants.All(value => expected.GetValueOrDefault(value.Selector) == value.EntityTag);
    }

    /** Only typed reciprocal owners and typed root links receive new tags. */
    public static PreparedWeaponArtEntity Prepare(PackageCatalog catalog, uint sourceEntityTag,
        uint modelTag, ushort packageId, int firstEntry, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Profiles.TryGetValue(sourceEntityTag, out var profile))
            throw new NotSupportedException("Weapon art creation needs a checked Tommy art profile.");
        if (!PackageWriter.IsNewPackageId(packageId) || catalog.ContainsInstalledPackage(packageId)
            || firstEntry < 0 || firstEntry > 8190)
            throw new InvalidDataException("Weapon art creation needs two unused package entries.");
        uint Tag(int index) => checked(0x80800000u + ((uint)packageId << 13) + (uint)(firstEntry + index));
        if (catalog.ContainsTag(Tag(0)) || catalog.ContainsTag(Tag(1)))
            throw new InvalidDataException("A weapon art destination tag is already used.");
        byte[] root = Read(sourceEntityTag, 0x80809C0F, profile.RootHash);
        byte[] config = Read(profile.Config, 0x80809C36, profile.ConfigHash);
        int primary = checked(0x10 + (int)I64(config, 0x10));
        int resource = checked(0x18 + (int)I64(config, 0x18));
        if (U32(config, primary - 4) != 0x808072B8 || U32(config, resource - 4) != 0x808072BD
            || U32(config, resource + 0x1DC) != profile.Model)
            throw new InvalidDataException("Weapon model component layout changed.");
        Pair(primary, resource, 0x808072B8, 0x808072BD);
        Pair(primary + 0x30, resource + 0x48, 0x80809BD8, 0x80809BD9);
        Pair(primary + 0x100, resource + 0x260, 0x808072CB, 0x808072C7);
        Pair(primary + 0x140, resource + 0x2C0, 0x808072C0, 0x808072C1);
        Pair(primary + 0x1A0, resource + 0x1E8, 0x80807314, 0x80807315);
        int[] values = NativeRows.Rows(config, primary + 0x120, 0x80809788, 96);
        int[] resources = NativeRows.Rows(config, resource + 0x2A8, 0x80809789, 40);
        if (values.Length != resources.Length)
            throw new InvalidDataException("Weapon model value-bank pairs disagree.");
        for (int index = 0; index < values.Length; index++)
            Pair(values[index], resources[index], 0x80809788, 0x80809789);
        Put32(config, resource + 0x1DC, modelTag);
        root = WeaponConfigEdits.RetagRoot(root, profile.Config, Tag(0));
        if (ContainsAligned(config, profile.Config))
            throw new NotSupportedException("An unclassified old model-owner reference remains in the art component.");
        var configEntry = Require(profile.Config);
        var rootEntry = Require(sourceEntityTag);
        ProjectAssetAddition[] assets =
        [
            new(name + ".component", config,
                new(packageId, configEntry.Reference, configEntry.TypeInfo, null, firstEntry), profile.Config),
            new(name + ".entity", root,
                new(packageId, rootEntry.Reference, rootEntry.TypeInfo, name, firstEntry + 1), sourceEntityTag)
        ];
        return new(Tag(1), Tag(0), assets);

        PackageEntry Require(uint tag) => catalog.TryGetEntry(tag, out var entry) ? entry
            : throw new InvalidDataException("Weapon art source is missing.");
        byte[] Read(uint tag, uint type, string hash)
        {
            byte[] bytes = catalog.Read(tag);
            if (Require(tag).Reference != type || Convert.ToHexString(SHA256.HashData(bytes)) != hash)
                throw new NotSupportedException("Weapon art creation needs the unchanged checked source profile.");
            return bytes.ToArray();
        }
        void Pair(int p, int r, uint pType, uint rType)
        {
            if (U32(config, p) != profile.Config || U32(config, p + 4) != rType || I64(config, p + 8) != r
                || U32(config, r) != profile.Config || U32(config, r + 4) != pType || I64(config, r + 8) != p)
                throw new InvalidDataException("Weapon model owner pair is not reciprocal.");
            Put32(config, p, Tag(0)); Put32(config, r, Tag(0));
        }
    }

    private static bool ContainsAligned(byte[] data, uint tag) =>
        Enumerable.Range(0, data.Length / 4).Any(index => U32(data, index * 4) == tag);

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static long I64(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
