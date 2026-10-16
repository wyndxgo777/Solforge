/** File: Reads the rig's control definitions (frames, chains, default values) from an animation rig tag. */
using System.Numerics;
using SunriseStudio.Packages;

namespace SunriseStudio.Animation;

/** How a control frame is built from bones: bone indices, kind, and optional aim target. */
public sealed record AnimationFrameReference(short BoneIndex, short SecondBoneIndex, byte Kind,
    AnimationFrameReference? TranslationFrame, float AimWeight, short AimTarget, byte AimSource);

/** A clip whose keys meet these keys has its control translations multiplied by the scale. */
public sealed record AnimationRetarget(IReadOnlyList<uint> Keys, float TranslationScale);

/** One rig control: its bones, composition and chain modes, frames and default value. */
public sealed record AnimationRigControl(int Index, uint NameHash, short BoneIndex, short ChainBoneIndex,
    byte CompositionMode, byte ChainMode, AnimationFrameReference RotationFrame,
    AnimationFrameReference TranslationFrame, BoneTransform DefaultValue)
{
    public byte PoleAxis { get; init; }
}

/** The authored control-to-bone table is separate from the render skeleton. */
public sealed class AnimationRigReader(PackageCatalog catalog)
{
    /** The rig and skeleton must come from the same entity construction list. */
    public AnimationRigBinding Read(SkeletonRig skeleton)
    {
        if (skeleton.EntityTag == 0)
            throw new NotSupportedException("Animation binding needs the entity that owns the skeleton.");
        var entity = new AnimationControlReader.Data(catalog.Read(skeleton.EntityTag));
        var candidates = new List<uint>();
        foreach (int row in entity.Rows(0x10, 0x80809C04, 12))
        {
            uint tag = entity.U32(row);
            if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != 0x80809C36) continue;
            var data = new AnimationControlReader.Data(catalog.Read(tag));
            int primary = data.Pointer(0x10), secondary = data.Pointer(0x18);
            if (data.U32(primary - 4) == 0x80808F96 && data.U32(secondary - 4) == 0x80808F8F)
                candidates.Add(tag);
        }
        if (candidates.Distinct().Count() != 1)
            throw new NotSupportedException($"Entity has {candidates.Distinct().Count()} supported animation rigs.");
        uint resourceTag = candidates[0];
        var rig = new AnimationControlReader.Data(catalog.Read(resourceTag));
        int resource = rig.Pointer(0x18);
        int[] rows = rig.Rows(resource + 0xA0, 0x80809076, 52);
        int[] defaults = rig.Rows(resource + 0xB0, 0x80809F75, 32);
        if (rows.Length == 0 || rows.Length != defaults.Length)
            throw new InvalidDataException("Animation control count differs from its authored default poses.");
        var controls = new AnimationRigControl[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            int row = rows[i], value = defaults[i];
            short bone = rig.I16(row + 44), chain = rig.I16(row + 46);
            byte composition = rig.U8(row + 48), chainMode = rig.U8(row + 49);
            if (bone < 0 || bone >= skeleton.Bones.Count || chain < -1 || chain >= skeleton.Bones.Count
                || composition > 1 || chainMode is not (0 or 1 or 255))
                throw new NotSupportedException("Animation rig control uses an unsupported bone or composition mode.");
            var transform = new BoneTransform(new Quaternion(rig.F32(value), rig.F32(value + 4),
                rig.F32(value + 8), rig.F32(value + 12)), new Vector3(rig.F32(value + 16),
                rig.F32(value + 20), rig.F32(value + 24)), rig.F32(value + 28));
            SkeletonReader.CheckTransform(transform);
            controls[i] = new AnimationRigControl(i, rig.U32(row), bone, chain, composition, chainMode,
                Frame(rig, row + 4, skeleton.Bones.Count), Frame(rig, row + 24, skeleton.Bones.Count), transform)
            { PoleAxis = rig.U8(row + 50) };
        }
        var links = new EntityAnimationCatalog(catalog).Read(skeleton.EntityTag);
        var clips = links.Select(x => x.ClipTag).ToHashSet();
        var groups = rig.Rows(resource + 0x90, 0x8080907F, 8).Select(row =>
            new AnimationControlGroup(rig.U32(row), rig.I16(row + 4))).ToArray();
        if (groups.Any(group => group.ControlCount < 0)
            || groups.Sum(group => (long)group.ControlCount) != controls.Length)
            throw new InvalidDataException("Rig control groups disagree with its control count.");
        var ikControls = rig.Rows(resource + 0xC0, 0x80800006, 2).Select(rig.I16).ToArray();
        if (ikControls.Any(index => index < 0 || index >= controls.Length)
            || ikControls.Distinct().Count() != ikControls.Length)
            throw new InvalidDataException("Animation IK list contains invalid control indices.");
        return new AnimationRigBinding(skeleton, resourceTag, controls, clips, groups,
            ikControls.Select(index => (int)index).ToArray(), rig.U8(resource + 0x128) != 0,
            AnimationBoneCorrection.Read(catalog, links.Select(x => x.ConfigTag).Distinct(), skeleton.Bones.Count))
        { Retargets = ReadRetargets(rig.U32(resource + 0xD0), controls.Length) };
    }

    /**
     * The rig's retarget table: rows of keys and a translation scale for clips authored on another body.
     * Rows with a second factor, control offsets or control rotations are refused; no installed table has them.
     */
    private AnimationRetarget[] ReadRetargets(uint tag, int controlCount)
    {
        if (tag is 0 or uint.MaxValue || !catalog.TryGetEntry(tag, out var entry)) return [];
        if (entry.Reference != RetargetTableClass)
            throw new InvalidDataException($"Animation retarget link 0x{tag:X8} is not a class 0x{RetargetTableClass:X8} table.");
        var table = new AnimationControlReader.Data(catalog.Read(tag));
        return table.Rows(0x08, RetargetRowClass, 56).Select(row =>
        {
            float scale = table.F32(row + 16), second = table.F32(row + 20);
            int[] offsets = table.Rows(row + 24, 0x80800091, 16), rotations = table.Rows(row + 40, 0x80800096, 16);
            if (!float.IsFinite(scale) || scale <= 0 || second != 1 || offsets.Length is not 0 && offsets.Length != controlCount
                || offsets.Any(at => table.F32(at) != 0 || table.F32(at + 4) != 0 || table.F32(at + 8) != 0 || table.F32(at + 12) != 0)
                || rotations.Any(at => table.F32(at) != 0 || table.F32(at + 4) != 0 || table.F32(at + 8) != 0 || table.F32(at + 12) != 1))
                throw new NotSupportedException("Animation retarget row uses fields that are not decoded.");
            return new AnimationRetarget(Array.AsReadOnly(table.Rows(row, AnimationControlReader.RetargetKeyClass,
                AnimationControlReader.RetargetKeyStride)
                .Select(table.U32).ToArray()), scale);
        }).ToArray();
    }

    // The rig secondary's +0xD0 links this table; its rows are 56 bytes.
    private const uint RetargetTableClass = 0x80809071, RetargetRowClass = 0x80809073;

    /** An aim frame holds a nested frame 6 bytes in; the nested frame supplies its translation. */
    private static AnimationFrameReference Frame(AnimationControlReader.Data data, int offset, int bones,
        int depth = 0)
    {
        short first = data.I16(offset), second = data.I16(offset + 2);
        byte kind = data.U8(offset + 4);
        if (first < -1 || first >= bones || second < -1 || second >= bones || kind > 6)
            throw new NotSupportedException("Animation reference frame uses an unsupported bone or kind.");
        if (kind != 6) return new(first, second, kind, null, 0, -1, 0);
        short target = data.I16(offset + 16);
        byte source = data.U8(offset + 18);
        if (depth != 0 || source > 2 || target < -1 || target >= bones)
            throw new NotSupportedException("Animation aim frame uses an unsupported source or depth.");
        return new(first, second, kind, Frame(data, offset + 6, bones, depth + 1),
            data.F32(offset + 12), target, source);
    }
}
