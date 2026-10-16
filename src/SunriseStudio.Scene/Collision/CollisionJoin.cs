using System.Numerics;

namespace SunriseStudio.Scene.Collision;

/**
 * Finds the static instance rows a collision placement copies. There is no stored link; the join
 * is world transform equality, as the static collision rule uses it.
 */
public static class CollisionJoin
{
    // Per translation axis, as the rule; the product check matches the static model join.
    private const float TranslationTolerance = 2e-3f, MatrixTolerance = 2e-2f;

    /** One part of each placed static instance row at the placement's world transform, in scene order. */
    public static IReadOnlyList<MeshInstance> StaticRows(SceneDocument scene, CollisionScene collision, CollisionPlacement placement)
    {
        if (placement.Instance < 0 || collision.Entities[placement.Entity].Source != CollisionSource.StaticEntity) return [];
        var world = placement.World;
        var result = new List<MeshInstance>();
        var seen = new HashSet<(uint Table, int Row, uint Owner, int Offset)>();
        foreach (var instance in scene.Instances)
        {
            if (instance.InstanceSourceTag == 0 || !Near(instance.Transform.Translation, world.Translation)) continue;
            if (!Matrix4x4.Invert(instance.Transform, out var inverse) || !Identity(world * inverse)) continue;
            if (seen.Add((instance.InstanceSourceTag, instance.InstanceSourceIndex, instance.PlacementSourceTag,
                instance.PlacementSourceOffset))) result.Add(instance);
        }
        return result;
    }

    private static bool Near(Vector3 a, Vector3 b) => MathF.Abs(a.X - b.X) < TranslationTolerance
        && MathF.Abs(a.Y - b.Y) < TranslationTolerance && MathF.Abs(a.Z - b.Z) < TranslationTolerance;

    /** True when the matrix is the identity within tolerance. */
    internal static bool Identity(Matrix4x4 value)
    {
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                if (MathF.Abs(value[row, column] - (row == column ? 1 : 0)) > MatrixTolerance) return false;
        return true;
    }
}
