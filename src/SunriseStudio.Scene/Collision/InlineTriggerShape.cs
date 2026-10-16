using System.Numerics;

namespace SunriseStudio.Scene.Collision;

/** The inline shape's axis can keep its rotation fixed in world space. */
public sealed record InlineTriggerShape(CollisionShape Shape, bool WorldAligned)
{
    /** World transform of the shape: the owner's, or only its translation when the shape is world-aligned. */
    public Matrix4x4 WorldTransform(Matrix4x4 owner) => WorldAligned
        ? Matrix4x4.CreateTranslation(owner.Translation) : owner;

    /** Native kinds 0 and cylinder axis 0 ignore the owner's rotation. */
    public static InlineTriggerShape Decode(byte[] bytes, int offset)
    {
        var data = new TagData(bytes);
        byte kind = data.U8(offset);
        switch (kind)
        {
            case 0 or 1:
                return new(CollisionShapes.BoxShape(data.V3(offset + 0x10) * 0.5f), kind == 0);
            case 2:
                return new(CollisionShapes.SphereShape(Vector3.Zero, data.F32(offset + 4)), false);
            case 3:
            {
                float radius = data.F32(offset + 4), length = data.F32(offset + 8), split = data.F32(offset + 12);
                byte axis = data.U8(offset + 16);
                if (!float.IsFinite(radius) || !float.IsFinite(length) || !float.IsFinite(split) || radius <= 0 || length <= 0)
                    throw new InvalidDataException("The inline cylinder has invalid dimensions.");
                var direction = axis == 1 ? Vector3.UnitX : Vector3.UnitZ;
                return new(CollisionShapes.CylinderShape(-direction * (length * split), direction * (length * (1 - split)), radius), axis == 0);
            }
            default:
                throw new NotSupportedException("This inline shape refers to a packfile.");
        }
    }
}
