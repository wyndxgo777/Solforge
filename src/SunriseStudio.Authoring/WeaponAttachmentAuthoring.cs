using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/** One attachment point: name and the bone it follows. */
public sealed record WeaponAttachmentInput(string Name, int BoneIndex,
    Quaternion Rotation, Vector3 Translation, float Scale = 1);
/** Compiled attachment asset and gameplay root. */
public sealed record PreparedWeaponAttachments(ProjectAssetAddition Asset, byte[] GameplayRoot,
    IReadOnlyDictionary<string, uint> NameHashes);

/** Adds and moves the named points of a weapon's point provider; effects play on these points. */
public static class WeaponAttachmentAuthoring
{
    public const uint TemplateTag = 0x80B9EAC3;
    private const string TemplateHash =
        "F3CC7A144EAF04DD41451E3588C8C195FC77DAB2A39C1B0D92D51B615EB5C173";
    private const uint ConfigClass = 0x80809C36, PointClass = 0x80808513;
    // Barrel point rows have the same 64-byte layout as provider rows.
    private const uint BarrelPointClass = 0x80809C00;
    // The provider's resource holds its point rows at +536.
    private const int Descriptor = 536;
    // Stock layout: the point array header sits at +0x290 after the eligibility list; the interface table ends the record.
    private const int RowsHeader = 0x290, TableField = 0x30;
    private const uint ArrayMarker = 0x80809FBD, InterfaceTableClass = 0x808091A4;
    // Highest name hash among the checked template's six rows.
    private const uint LastTemplateHash = 0x9697BFD2;

    /** New points keep the checked provider's frame imports and eligibility bit zero. */
    public static PreparedWeaponAttachments Prepare(PackageCatalog catalog, byte[] gameplayRoot,
        ushort packageId, int entryIndex, string name, IReadOnlyList<WeaponAttachmentInput> points)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (points.Count > 64 || points.Select(point => point.Name).Distinct().Count() != points.Count)
            throw new InvalidDataException("Weapon attachment points need unique names and a count of at most 64.");
        if (!PackageWriter.IsNewPackageId(packageId) || catalog.ContainsInstalledPackage(packageId)
            || entryIndex is < 0 or >= 8192)
            throw new InvalidDataException("Weapon points need an unused native package entry.");
        uint tag = checked(0x80800000u + ((uint)packageId << 13) + (uint)entryIndex);
        if (catalog.ContainsTag(tag)) throw new InvalidDataException("Weapon point destination is already used.");
        if (!catalog.TryGetEntry(TemplateTag, out var entry) || entry.Reference != ConfigClass)
            throw new InvalidDataException("The weapon point provider is missing.");
        byte[] config = catalog.Read(TemplateTag);
        if (Convert.ToHexString(SHA256.HashData(config)) != TemplateHash)
            throw new NotSupportedException("Weapon points need the unchanged checked provider.");
        config = config.ToArray();
        Pair(128, 360, 0x80808506, 0x80808507);
        Pair(176, 456, 0x80808569, 0x80808568);
        Pair(208, 472, 0x80809BD6, 0x80809BD7);
        int[] oldRows = NativeRows.Rows(config, Descriptor, PointClass, 64);
        if (oldRows.Length != 6 || oldRows.Any(row => U32(config, row) != 0 || U32(config, row + 4) != 0)
            || oldRows.Max(row => U32(config, row + 48)) != LastTemplateHash)
            throw new InvalidDataException("The checked weapon attachment layout changed.");
        var hashes = oldRows.Select(row => U32(config, row + 48)).ToHashSet();
        var resultNames = new Dictionary<string, uint>();
        foreach (var point in points)
        {
            uint hash = Hash(point);
            if (!hashes.Add(hash)) throw new InvalidDataException("A weapon attachment point name is already used.");
            config = InvestmentItemClone.AppendRow(config, Descriptor, 64, Row(point, hash));
            resultNames.Add(point.Name, hash);
        }
        Sort(config);
        config = Compact(config);
        byte[] root = gameplayRoot.ToArray();
        int construction = 0, links = 0;
        Retag(0x10, 0x80809C04, 12, [0], true);
        Retag(0x20, 0x80809BC9, 72, [8, 40]);
        Retag(0x30, 0x80809BC9, 72, [8, 40]);
        Retag(0x58, 0x80809C22, 40, [16]);
        Retag(0x68, 0x80809C20, 24, [0]);
        if (construction != 1 || links < 2 || Contains(root, TemplateTag) || Contains(config, TemplateTag))
            throw new NotSupportedException("The weapon root has an unclassified point-provider link.");
        return new(new(name + ".points", config,
            new(packageId, entry.Reference, entry.TypeInfo, name + " points", entryIndex), TemplateTag),
            root, resultNames);

        void Pair(int primary, int resource, uint primaryClass, uint resourceClass)
        {
            if (U32(config, primary) != TemplateTag || U32(config, primary + 4) != resourceClass
                || I64(config, primary + 8) != resource || U32(config, resource) != TemplateTag
                || U32(config, resource + 4) != primaryClass || I64(config, resource + 8) != primary)
                throw new InvalidDataException("Weapon point owner references are not reciprocal.");
            Put32(config, primary, tag); Put32(config, resource, tag);
        }
        void Retag(int field, uint type, int stride, int[] fields, bool builds = false)
        {
            foreach (int row in NativeRows.Rows(root, field, type, stride))
                foreach (int offset in fields)
                    if (U32(root, row + offset) == TemplateTag)
                    {
                        Put32(root, row + offset, tag); links++;
                        if (builds) construction++;
                    }
        }
    }

    /**
     * The name itself, or the first numbered form of it, that sorts after every checked template row.
     * Template rows then keep their stock row indices.
     */
    public static string AfterTemplateRows(string name)
    {
        for (int suffix = 0; suffix < 1_000_000; suffix++)
        {
            string candidate = suffix == 0 ? name : $"{name}.{suffix}";
            if (InvestmentItemClone.Fnv1(candidate) > LastTemplateHash) return candidate;
        }
        throw new InvalidDataException($"No form of the point name {name} sorts after the stock points.");
    }

    /**
     * Adds points to a weapon's own provider; a point whose name is already there moves instead.
     * @param config The provider config the weapon owns.
     * @param retire Name hashes of earlier points to remove, unless a new point has the same name.
     * @return The provider bytes and each point's name hash.
     */
    public static (byte[] Config, IReadOnlyDictionary<string, uint> NameHashes) SetPoints(ReadOnlyMemory<byte> config,
        IReadOnlyList<WeaponAttachmentInput> points, IReadOnlySet<uint>? retire = null)
    {
        byte[] output = config.ToArray();
        if (retire is not null)
            RemoveRows(output, retire.Except(points.Select(Hash)).ToHashSet());
        var names = new Dictionary<string, uint>();
        foreach (var point in points)
        {
            uint hash = Hash(point);
            byte[] row = Row(point, hash);
            int existing = Array.Find(NativeRows.Rows(output, Descriptor, PointClass, 64), at => U32(output, at + 48) == hash);
            if (existing > 0) row.CopyTo(output, existing);
            else output = InvestmentItemClone.AppendRow(output, Descriptor, 64, row);
            names[point.Name] = hash;
        }
        Sort(output);
        return (Compact(output), names);
    }

    /** Moves the provider row named hash to the point's bone and transform; the row keeps its name. */
    public static byte[] MovePoint(ReadOnlyMemory<byte> config, uint hash, WeaponAttachmentInput point)
    {
        Validate(point);
        byte[] output = config.ToArray();
        int row = Array.Find(NativeRows.Rows(output, Descriptor, PointClass, 64), at => U32(output, at + 48) == hash);
        if (row <= 0) throw new InvalidDataException($"The weapon point provider has no point {hash:X8} to move.");
        WriteTransform(output, row, point);
        Sort(output);
        return Compact(output);
    }

    /**
     * Moves every barrel point row named hash to the point's bone and transform.
     * Barrel sets are arrays of 64-byte point rows of class 0x80809C00 inside the weapon's firing config.
     * @return The config bytes and the number of rows moved.
     */
    public static (byte[] Config, int Moved) MoveBarrelPoints(ReadOnlyMemory<byte> config, uint hash, WeaponAttachmentInput point)
    {
        Validate(point);
        byte[] output = config.ToArray();
        int[] rows = BarrelRows(output, hash);
        foreach (int row in rows) WriteTransform(output, row, point);
        return (output, rows.Length);
    }

    /** Copies the stock transform of the provider row named hash back; the row keeps its name. */
    public static byte[] RestorePoint(ReadOnlyMemory<byte> config, uint hash, ReadOnlyMemory<byte> stockConfig)
    {
        byte[] output = config.ToArray(), stock = stockConfig.ToArray();
        int to = Array.Find(NativeRows.Rows(output, Descriptor, PointClass, 64), at => U32(output, at + 48) == hash);
        int from = Array.Find(NativeRows.Rows(stock, Descriptor, PointClass, 64), at => U32(stock, at + 48) == hash);
        if (to <= 0 || from <= 0) throw new InvalidDataException($"The weapon point provider has no point {hash:X8} to restore.");
        stock.AsSpan(from + 4, 44).CopyTo(output.AsSpan(to + 4));
        return output;
    }

    /**
     * Copies the stock transforms of the barrel point rows named hash back, pairing rows in order.
     * @return The config bytes and the number of rows that changed.
     */
    public static (byte[] Config, int Restored) RestoreBarrelPoints(ReadOnlyMemory<byte> config, uint hash,
        ReadOnlyMemory<byte> stockConfig)
    {
        byte[] output = config.ToArray(), stock = stockConfig.ToArray();
        int[] rows = BarrelRows(output, hash), stockRows = BarrelRows(stock, hash);
        if (rows.Length != stockRows.Length)
            throw new InvalidDataException($"The barrel points {hash:X8} no longer match the base weapon.");
        int restored = 0;
        for (int index = 0; index < rows.Length; index++)
        {
            var source = stock.AsSpan(stockRows[index] + 4, 44);
            if (source.SequenceEqual(output.AsSpan(rows[index] + 4, 44))) continue;
            source.CopyTo(output.AsSpan(rows[index] + 4));
            restored++;
        }
        return (output, restored);
    }

    /** Offsets of the barrel point rows named hash, in file order. */
    private static int[] BarrelRows(byte[] bytes, uint hash)
    {
        var rows = new List<int>();
        for (int at = 4; at <= bytes.Length - 16; at += 4)
        {
            if (U32(bytes, at - 4) != ArrayMarker || U32(bytes, at + 8) != BarrelPointClass) continue;
            long count = I64(bytes, at);
            if (count is <= 0 or > 64 || at + 16 + count * 64 > bytes.Length) continue;
            for (int index = 0; index < count; index++)
                if (U32(bytes, at + 16 + index * 64 + 48) == hash) rows.Add(at + 16 + index * 64);
        }
        return rows.ToArray();
    }

    /** Writes bone, rotation, translation and scale; the eligibility word and the name stay. */
    private static void WriteTransform(byte[] bytes, int row, WeaponAttachmentInput point)
    {
        byte[] values = Row(point, U32(bytes, row + 48));
        values.AsSpan(4, 44).CopyTo(bytes.AsSpan(row + 4));
    }

    /**
     * Rewrites the provider in the stock layout: rows directly after the eligibility list, then the
     * interface table, and nothing after it.
     */
    private static byte[] Compact(byte[] config)
    {
        int[] rows = NativeRows.Rows(config, Descriptor, PointClass, 64);
        int table = TableField + 8 + (int)I64(config, TableField + 8);
        if (I64(config, TableField) != 1 || table < 4 || table > config.Length - 32 || U32(config, table - 4) != ArrayMarker
            || I64(config, table) != 1 || U32(config, table + 8) != InterfaceTableClass
            || U32(config, RowsHeader - 4) != ArrayMarker || U32(config, RowsHeader + 8) != PointClass)
            throw new InvalidDataException("The weapon point provider is not in the checked layout.");
        int target = table + 16 + (int)I64(config, table + 16);
        int newTable = RowsHeader + 16 + rows.Length * 64 + 16;
        byte[] output = new byte[newTable + 32];
        config.AsSpan(0, RowsHeader + 16).CopyTo(output);
        for (int index = 0; index < rows.Length; index++)
            config.AsSpan(rows[index], 64).CopyTo(output.AsSpan(RowsHeader + 16 + index * 64));
        Put64(output, RowsHeader, rows.Length);
        Put64(output, Descriptor, rows.Length);
        Put64(output, Descriptor + 8, RowsHeader - (Descriptor + 8));
        Put32(output, newTable - 4, ArrayMarker);
        Put64(output, newTable, 1);
        Put32(output, newTable + 8, InterfaceTableClass);
        Put64(output, newTable + 16, target - (newTable + 16));
        config.AsSpan(table + 24, 8).CopyTo(output.AsSpan(newTable + 24));
        Put64(output, TableField + 8, newTable - (TableField + 8));
        Put64(output, 0, output.Length);
        return output;
    }

    /** Removes the named rows; the kept rows stay in order at the front of the same array. */
    private static void RemoveRows(byte[] config, IReadOnlySet<uint> hashes)
    {
        int[] rows = NativeRows.Rows(config, Descriptor, PointClass, 64);
        byte[][] kept = rows.Where(row => !hashes.Contains(U32(config, row + 48))).Select(row => config.AsSpan(row, 64).ToArray()).ToArray();
        if (kept.Length == rows.Length) return;
        if (kept.Length == 0) throw new InvalidDataException("A weapon point provider cannot lose every point.");
        for (int index = 0; index < rows.Length; index++)
            if (index < kept.Length) kept[index].CopyTo(config, rows[index]);
            else config.AsSpan(rows[index], 64).Clear();
        int header = rows[0] - 16;
        BinaryPrimitives.WriteInt64LittleEndian(config.AsSpan(header), kept.Length);
        BinaryPrimitives.WriteInt64LittleEndian(config.AsSpan(Descriptor), kept.Length);
    }

    /** Refuses a point without a weapon bone or with a transform that is not finite and uniform. */
    private static void Validate(WeaponAttachmentInput point)
    {
        if (string.IsNullOrWhiteSpace(point.Name) || point.Name.Length > 200
            || point.BoneIndex is < 0 or >= 5 || !Finite(point.Translation)
            || !float.IsFinite(point.Scale) || point.Scale <= 0
            || !float.IsFinite(point.Rotation.LengthSquared())
            || MathF.Abs(point.Rotation.LengthSquared() - 1) > .001f)
            throw new InvalidDataException("Weapon points need a valid bone and finite uniform transform.");
    }

    private static uint Hash(WeaponAttachmentInput point)
    {
        Validate(point);
        // Authored points sort after the template rows, so those keep their stock indices.
        uint hash = InvestmentItemClone.Fnv1(AfterTemplateRows(point.Name));
        if (hash is 0 or uint.MaxValue or InvestmentItemClone.EmptyStringHash)
            throw new InvalidDataException("A weapon attachment point name is empty.");
        return hash;
    }

    private static byte[] Row(WeaponAttachmentInput point, uint hash)
    {
        byte[] row = new byte[64];
        Put32(row, 4, checked((uint)point.BoneIndex));
        PutFloat(row, 16, point.Rotation.X); PutFloat(row, 20, point.Rotation.Y);
        PutFloat(row, 24, point.Rotation.Z); PutFloat(row, 28, point.Rotation.W);
        PutFloat(row, 32, point.Translation.X); PutFloat(row, 36, point.Translation.Y);
        PutFloat(row, 40, point.Translation.Z); PutFloat(row, 44, point.Scale);
        Put32(row, 48, hash);
        return row;
    }

    /** Point lookups search the rows by name hash, so they stay sorted. */
    private static void Sort(byte[] config)
    {
        int[] rows = NativeRows.Rows(config, Descriptor, PointClass, 64);
        byte[][] sorted = rows.Select(row => config.AsSpan(row, 64).ToArray()).OrderBy(row => U32(row, 48)).ToArray();
        for (int index = 0; index < rows.Length; index++) sorted[index].CopyTo(config, rows[index]);
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Contains(byte[] bytes, uint value)
    {
        for (int offset = 0; offset <= bytes.Length - 4; offset += 4)
            if (U32(bytes, offset) == value) return true;
        return false;
    }
    private static uint U32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static long I64(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
    private static void Put32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Put64(byte[] bytes, int offset, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);
    private static void PutFloat(byte[] bytes, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value);
}
