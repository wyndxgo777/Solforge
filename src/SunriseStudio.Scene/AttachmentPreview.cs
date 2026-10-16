using System.Numerics;

namespace SunriseStudio.Scene;

/** Node poses are supplied explicitly until each reflected pose-provider binding is established. */
public static class AttachmentTransforms
{
    /** World transform of an attachment point, from its node and the owner's transform. */
    public static Matrix4x4 PointWorld(AuthoredAttachmentPoint point,
        IReadOnlyList<Matrix4x4> nodeObjectTransforms, Matrix4x4 ownerWorld)
    {
        if (point.NodeIndex < 0 || point.NodeIndex >= nodeObjectTransforms.Count)
            throw new NotSupportedException("Attachment point needs a valid caller-supplied node pose.");
        var result = point.LocalTransform * nodeObjectTransforms[point.NodeIndex] * ownerWorld;
        CheckUniformTransform(result);
        return result;
    }

    /** Native alignment removes the child point and preserves the child's current uniform scale. */
    public static Matrix4x4 Align(Matrix4x4 childWorld, Matrix4x4 childPointWorld,
        Matrix4x4 parentPointWorld)
    {
        var childScale = CheckUniformTransform(childWorld);
        CheckUniformTransform(childPointWorld);
        CheckUniformTransform(parentPointWorld);
        if (!Matrix4x4.Invert(childPointWorld, out var inverse))
            throw new InvalidDataException("Child attachment point is singular.");
        var aligned = childWorld * inverse * parentPointWorld;
        if (!Matrix4x4.Decompose(aligned, out _, out var rotation, out var translation))
            throw new InvalidDataException("Attachment alignment has no SRT decomposition.");
        return Matrix4x4.CreateScale(childScale)
            * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation))
            * Matrix4x4.CreateTranslation(translation);
    }

    private static float CheckUniformTransform(Matrix4x4 matrix)
    {
        if (matrix.M14 != 0 || matrix.M24 != 0 || matrix.M34 != 0 || matrix.M44 != 1
            || !Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var position)
            || !float.IsFinite(rotation.LengthSquared()) || !float.IsFinite(position.LengthSquared())
            || !float.IsFinite(scale.LengthSquared()) || scale.X <= 0
            || MathF.Abs(scale.X - scale.Y) > 0.001f || MathF.Abs(scale.X - scale.Z) > 0.001f)
            throw new InvalidDataException("Attachment transforms require finite uniform scale.");
        return scale.X;
    }
}

/**
 * Attachment-preview part of AssetSceneLoader: builds a scene of a weapon fitted to its parent
 * entity's attachment point.
 */
public sealed partial class AssetSceneLoader
{
    /** Preview choices name authored records without inferring runtime selection. */
    public SceneDocument LoadAttachmentPreview(AuthoredWeaponReference weapon,
        AuthoredAttachmentPoint parentPoint, AuthoredAttachmentPoint childPoint,
        IReadOnlyList<Matrix4x4> parentNodeObjectTransforms,
        IReadOnlyList<Matrix4x4> childNodeObjectTransforms,
        Matrix4x4 parentWorld, Matrix4x4 childWorld, CancellationToken token = default)
    {
        cancellation = token;
        var metadata = DescribeEntityAttachments(weapon.ParentEntityTag);
        var verifiedWeapon = metadata.Loadouts.SelectMany(g => g.Weapons).FirstOrDefault(w =>
            w.ConfigTag == weapon.ConfigTag && w.GroupIndex == weapon.GroupIndex
            && w.CandidateIndex == weapon.CandidateIndex && w.SourceOffset == weapon.SourceOffset
            && w.EntityTag == weapon.EntityTag)
            ?? throw new ArgumentException("Weapon choice is not an authored loadout reference.");
        var parent = FindPoint(metadata.ParentPoints, parentPoint);
        var child = FindPoint(metadata.WeaponPoints.Where(p => p.EntityTag == verifiedWeapon.EntityTag), childPoint);
        var parentFrame = AttachmentTransforms.PointWorld(parent, parentNodeObjectTransforms, parentWorld);
        var childFrame = AttachmentTransforms.PointWorld(child, childNodeObjectTransforms, childWorld);
        var transform = AttachmentTransforms.Align(childWorld, childFrame, parentFrame);
        var result = Load(verifiedWeapon.EntityTag, token);
        if (result.Markers.Count != 0)
            throw new NotSupportedException("Attachment preview does not yet transform child debug markers.");
        var source = new AttachmentPreviewSource(verifiedWeapon, parent, child);
        var placements = new Dictionary<SceneObjectPlacement, SceneObjectPlacement>();
        for (var index = 0; index < result.Instances.Count; ++index)
        {
            var original = result.Instances[index];
            SceneObjectPlacement? placement = null;
            if (original.ObjectPlacement is { } owner && !placements.TryGetValue(owner, out placement))
                placements[owner] = placement = new(owner.SourceTag, owner.SourceIndex, owner.WorldTransform * transform);
            result.Instances[index] = new MeshInstance
            {
                ObjectPlacement = placement,
                Mesh = original.Mesh,
                Transform = original.Transform * transform,
                Name = original.Name,
                Layer = original.Layer,
                BubbleHash = original.BubbleHash,
                InstanceSourceTag = original.InstanceSourceTag,
                InstanceSourceIndex = original.InstanceSourceIndex,
                NativeInstanceData = original.NativeInstanceData,
                ObjectClassTag = original.ObjectClassTag,
                ModelSourceConfigTag = original.ModelSourceConfigTag,
                ModelSourceResourceClass = original.ModelSourceResourceClass,
                InitiallyVisible = original.InitiallyVisible,
                InitialVisibilityReason = original.InitialVisibilityReason,
                PlacementSourceTag = original.PlacementSourceTag,
                PlacementSourceOffset = original.PlacementSourceOffset,
                PlacementIdentity = original.PlacementIdentity,
                OcclusionOwners = original.OcclusionOwners,
                MapContainers = original.MapContainers,
                AttachmentSource = source,
                Category = original.Category,
                Tags = original.Tags,
                ActivityTag = original.ActivityTag,
                StateOrdinal = original.StateOrdinal,
                StateName = original.StateName
            };
        }
        result.AttachmentPreview = source;
        return result;
    }

    private static AuthoredAttachmentPoint FindPoint(IEnumerable<AuthoredAttachmentPointSet> sets,
        AuthoredAttachmentPoint requested)
    {
        return sets.SelectMany(s => s.Points).FirstOrDefault(p => p.EntityTag == requested.EntityTag
            && p.ConfigTag == requested.ConfigTag && p.PrimaryOffset == requested.PrimaryOffset
            && p.SourceOffset == requested.SourceOffset && p.RowIndex == requested.RowIndex)
            ?? throw new ArgumentException("Attachment point is not an authored point record.");
    }
}
