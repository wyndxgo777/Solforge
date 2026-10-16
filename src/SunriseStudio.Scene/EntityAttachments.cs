using System.Numerics;

namespace SunriseStudio.Scene;

/**
 * A weapon an entity can carry, with the model tags it resolves to. This file holds the
 * attachment-catalog records and reader.
 */
public sealed record AuthoredWeaponReference(uint ParentEntityTag, uint ConfigTag,
    int GroupIndex, int CandidateIndex, int SourceOffset, uint EntityTag,
    IReadOnlyList<uint> ModelTags);

/** One weapon loadout row: group, eligibility bit, weight and whether it is the fallback. */
public sealed record AuthoredWeaponLoadout(uint ConfigTag, int GroupIndex, int SourceOffset,
    int EligibilityBitIndex, float Weight, bool IsFallback,
    IReadOnlyList<AuthoredWeaponReference> Weapons);

/** An authored attachment point of an entity config. */
public sealed record AuthoredAttachmentPoint(uint EntityTag, uint ConfigTag,
    int PrimaryOffset, int SecondaryOffset, int RowIndex, int SourceOffset,
    int NodeIndex, uint EligibilityBitIndex, uint NameHash, string Name,
    SceneNameEvidence NameEvidence, Quaternion Rotation, Vector3 Translation, float Scale)
{
    public Matrix4x4 LocalTransform => Matrix4x4.CreateScale(Scale)
        * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Translation);
}

/** The attachment points of one role (parent or child) read from a config. */
public sealed record AuthoredAttachmentPointSet(uint EntityTag, uint ConfigTag,
    string Role, int PrimaryOffset, uint PrimaryClass, uint SecondaryClass,
    IReadOnlyList<AuthoredAttachmentPoint> Points);

/** An entity's loadouts, weapon references and attachment points. */
public sealed record EntityAttachmentCatalog(uint EntityTag,
    IReadOnlyList<AuthoredWeaponLoadout> Loadouts,
    IReadOnlyList<AuthoredAttachmentPointSet> ParentPoints,
    IReadOnlyList<AuthoredAttachmentPointSet> WeaponPoints,
    IReadOnlyList<string> RuntimeInputs, IReadOnlyList<string> Diagnostics);

/** A weapon with the parent and child attachment points that join it to its owner. */
public sealed record AttachmentPreviewSource(AuthoredWeaponReference Weapon,
    AuthoredAttachmentPoint ParentPoint, AuthoredAttachmentPoint ChildPoint);

/** Attachment part of AssetSceneLoader: describes an entity's weapon loadouts and attachment points. */
public sealed partial class AssetSceneLoader
{
    /** Loadout alternatives remain explicit because native selection uses runtime bits and RNG. */
    public EntityAttachmentCatalog DescribeEntityAttachments(uint entityTag)
    {
        if (Class(entityTag) != 0x80809C0F)
            throw new ArgumentException("Attachment metadata requires an entity class.");
        var entity = Read(entityTag);
        var loadouts = new List<AuthoredWeaponLoadout>();
        var parentPoints = new List<AuthoredAttachmentPointSet>();
        var diagnostics = new List<string>();
        foreach (var build in entity.Rows(0x10, 0x80809C04, 12))
        {
            var configTag = entity.U32(build);
            if (Class(configTag) != 0x80809C36) continue;
            var data = Read(configTag);
            var primary = data.Relative(0x10);
            var secondary = data.Relative(0x18);
            if (!data.Has(primary - 4, 20) || !data.Has(secondary - 4, 20)) continue;
            var primaryClass = data.U32(primary - 4);
            if (primaryClass == 0x80803E63)
                diagnostics.Add($"Config 0x{configTag:X8} uses an unsupported player equipment family.");
            if (primaryClass != 0x80803EBB || data.U32(secondary - 4) != 0x80803EBC) continue;
            CheckComponentPair(configTag, data, primary, secondary, 0x80803EBB, 0x80803EBC);
            var groups = data.Rows(secondary + 0x120, 0x80803EC4, 0x40);
            for (var index = 0; index < groups.Length; ++index)
            {
                var row = groups[index];
                var weight = data.F32(row + 0x28);
                if (!float.IsFinite(weight) || weight < 0)
                    throw new InvalidDataException("Authored weapon loadout weight is invalid.");
                loadouts.Add(new AuthoredWeaponLoadout(configTag, index, row,
                    data.I32(row + 8), weight, false,
                    ReadWeaponReferences(entityTag, configTag, data, row + 0x30, index)));
            }
            var fallback = ReadWeaponReferences(entityTag, configTag, data, secondary + 0x100, -1);
            if (fallback.Count != 0)
                loadouts.Add(new AuthoredWeaponLoadout(configTag, -1, secondary + 0x100,
                    -1, 0, true, fallback));
            parentPoints.Add(ReadAttachmentPoints(entityTag, configTag, data, primary + 0x30,
                0x80809663, 0x80809664, "Native mode 1 parent default"));
            parentPoints.Add(ReadAttachmentPoints(entityTag, configTag, data, primary + 0xE0,
                0x80808569, 0x80808568, "Native mode 3 declared parent points"));
            if (data.I64(primary + 0xB0) != 0 || data.I64(primary + 0x190) != 0)
                diagnostics.Add($"Config 0x{configTag:X8} has parent point overrides not selected by this reader.");
        }
        var weaponTags = loadouts.SelectMany(g => g.Weapons).Select(w => w.EntityTag);
        if (entity.U8(0x96) == 14) weaponTags = weaponTags.Append(entityTag);
        var weaponPoints = weaponTags.Distinct()
            .SelectMany(DescribeWeaponAttachmentPoints).ToArray();
        string[] inputs = loadouts.Count != 0
            ? ["Runtime eligibility bitset", "Weighted loadout random state",
                "Equipped/stored slot disposition", "Point override and linked-point state",
                "Pose-provider binding and current node poses", "Child scale"]
            : weaponPoints.Length != 0 ? ["Point selection and overrides",
                "Pose-provider binding and current node poses", "Child scale"] : [];
        return new EntityAttachmentCatalog(entityTag, loadouts.AsReadOnly(), parentPoints.AsReadOnly(),
            weaponPoints, inputs, diagnostics.AsReadOnly());
    }

    /** The weapon component retains two point sources; reflected role selection stays explicit. */
    public IReadOnlyList<AuthoredAttachmentPointSet> DescribeWeaponAttachmentPoints(uint entityTag)
    {
        if (Class(entityTag) != 0x80809C0F)
            throw new ArgumentException("Weapon point metadata requires an entity class.");
        var entity = Read(entityTag);
        if (entity.U8(0x96) != 14)
            throw new ArgumentException("Entity is not the native weapon object type.");
        var result = new List<AuthoredAttachmentPointSet>();
        foreach (var build in entity.Rows(0x10, 0x80809C04, 12))
        {
            var configTag = entity.U32(build);
            if (Class(configTag) != 0x80809C36) continue;
            var data = Read(configTag);
            var primary = data.Relative(0x10);
            var secondary = data.Relative(0x18);
            if (!data.Has(primary - 4, 20) || !data.Has(secondary - 4, 20)
                || data.U32(primary - 4) != 0x80803E70
                || data.U32(secondary - 4) != 0x80804221) continue;
            CheckComponentPair(configTag, data, primary, secondary, 0x80803E70, 0x80804221);
            result.Add(ReadAttachmentPoints(entityTag, configTag, data, primary + 0x350,
                0x80809663, 0x80809664, "Weapon point source +0x350"));
            result.Add(ReadAttachmentPoints(entityTag, configTag, data, primary + 0x3F0,
                0x80809663, 0x80809664, "Weapon point source +0x3F0"));
        }
        return result.AsReadOnly();
    }

    /** Child tags follow the exact 16-byte list consumed by native weapon instantiation. */
    private IReadOnlyList<AuthoredWeaponReference> ReadWeaponReferences(uint parentTag,
        uint configTag, TagData data, int descriptor, int group)
    {
        var result = new List<AuthoredWeaponReference>();
        var rows = data.Rows(descriptor, 0x80803ECA, 16);
        for (var index = 0; index < rows.Length; ++index)
        {
            var child = data.U32(rows[index] + 8);
            if (child == uint.MaxValue) continue;
            if (Class(child) != 0x80809C0F || Read(child).U8(0x96) != 14)
                throw new InvalidDataException("Authored weapon reference has the wrong entity type.");
            var models = new HashSet<uint>();
            var entity = Read(child);
            foreach (var build in entity.Rows(0x10, 0x80809C04, 12))
            {
                var config = entity.U32(build);
                if (Class(config) != 0x80809C36) continue;
                var resourceData = Read(config);
                var resource = resourceData.Relative(0x18);
                if (!resourceData.Has(resource - 4, 0x1E4)
                    || resourceData.U32(resource - 4) != 0x808072BD) continue;
                var model = resourceData.U32(resource + 0x1DC);
                if (Class(model) == 0x808073A5) models.Add(model);
            }
            result.Add(new AuthoredWeaponReference(parentTag, configTag, group, index,
                rows[index], child, models.ToArray()));
        }
        return result.AsReadOnly();
    }

    // Point rows are 64 bytes under either array class; one point source pair class uses both.
    private const uint PointRowClass = 0x80809C00, PointSetRowClass = 0x80808513;

    /** Embedded point pairs repeat their owner, peer classes and absolute peer offsets. */
    private AuthoredAttachmentPointSet ReadAttachmentPoints(uint entityTag, uint configTag,
        TagData data, int primary, uint primaryClass, uint secondaryClass, string role)
    {
        var secondary = checked((int)data.U64(primary + 8));
        CheckComponentPair(configTag, data, primary, secondary, primaryClass, secondaryClass);
        var rows = data.Rows(secondary + 0x50,
            data.ArrayClass(secondary + 0x50) == PointSetRowClass ? PointSetRowClass : PointRowClass, 64);
        var points = rows.Select((row, index) =>
        {
            var rotation = data.Q4(row + 0x10);
            var translation = data.V3(row + 0x20);
            var scale = data.F32(row + 0x2C);
            if (!Finite(translation) || !float.IsFinite(rotation.LengthSquared())
                || rotation.LengthSquared() < 1e-8f || !float.IsFinite(scale) || scale <= 0)
                throw new InvalidDataException("Attachment point transform is invalid.");
            var hash = data.U32(row + 0x30);
            var name = ResolveHashName(hash);
            return new AuthoredAttachmentPoint(entityTag, configTag, primary, secondary,
                index, row, data.I32(row + 4), data.U32(row), hash, name.Name, name.Evidence,
                rotation, translation, scale);
        }).ToArray();
        return new AuthoredAttachmentPointSet(entityTag, configTag, role, primary,
            primaryClass, secondaryClass, points);
    }

    private static void CheckComponentPair(uint tag, TagData data, int primary, int secondary,
        uint primaryClass, uint secondaryClass)
    {
        if (!data.Has(primary, 16) || !data.Has(secondary, 16)
            || data.U32(primary) != tag || data.U32(primary + 4) != secondaryClass
            || data.U64(primary + 8) != (ulong)secondary
            || data.U32(secondary) != tag || data.U32(secondary + 4) != primaryClass
            || data.U64(secondary + 8) != (ulong)primary)
            throw new InvalidDataException("Attachment component pair does not agree with its owner.");
    }
}
