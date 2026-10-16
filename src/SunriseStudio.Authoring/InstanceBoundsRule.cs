using System.Numerics;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/**
 * The client culls a static instance by its bounds row, in table space, and never recomputes it.
 * Each edited box is the installed box moved from the installed row to the edited row.
 * The table aggregate at +0x80 and +0x90 grows to hold every edited box.
 */
internal sealed class InstanceBoundsRule : IObjectEditRule
{
    public ObjectEditLayout Layout => ObjectEditLayout.StaticInstance;

    /** Moves each edited instance's installed box to its edited row and grows the table aggregate. */
    public void Apply(ObjectEditContext context, IReadOnlyList<ObjectEdit> edits)
    {
        foreach (var group in edits.GroupBy(edit => edit.Target.SourceTag).OrderBy(group => group.Key))
        {
            uint tableTag = group.Key;
            var originalTable = new NativeRows(context.Original(tableTag));
            byte[] tableBytes = context.Current(tableTag);
            var table = new NativeRows(tableBytes);
            // The paired bounds table tag sits at instance table +0x18.
            uint boundsTag = originalTable.U32(0x18);
            if (table.U32(0x18) != boundsTag || context.ClassOf(boundsTag) != 0x80809671)
                throw new InvalidDataException("The instance table lost its bounds table.");
            var originalBounds = new NativeRows(context.Original(boundsTag));
            byte[] boundsBytes = context.Current(boundsTag);
            int[] originalRows = originalTable.Rows(0x40, 0x808071A3, 48);
            int[] rows = table.Rows(0x40, 0x808071A3, 48);
            int[] originalBoxes = originalBounds.Rows(8, 0x80809673, 48);
            int[] boxes = new NativeRows(boundsBytes).Rows(8, 0x80809673, 48);
            if (originalBoxes.Length != originalRows.Length || boxes.Length != originalBoxes.Length
                || rows.Length != originalRows.Length)
                throw new InvalidDataException("The instance bounds table does not match its instance table.");
            Vector3 aggregateMin = table.V3(0x80), aggregateMax = table.V3(0x90);
            Vector3 min = aggregateMin, max = aggregateMax;
            // An empty aggregate stays empty; the client treats it as never visible.
            bool aggregate = min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z;
            foreach (var edit in group)
            {
                int index = edit.Target.Index;
                if (!Matrix4x4.Invert(RowMatrix(originalTable, originalRows[index]), out var inverse))
                    throw new InvalidDataException("The installed instance transform cannot be inverted.");
                int installed = originalBoxes[index];
                var box = TransformBox(originalBounds.V3(installed), originalBounds.V3(installed + 16),
                    inverse * RowMatrix(table, rows[index]));
                // Both W lanes are 1.0; the client scales the translation by them.
                NativeRows.Write(boundsBytes, boxes[index], box.Min.X, box.Min.Y, box.Min.Z, 1);
                NativeRows.Write(boundsBytes, boxes[index] + 16, box.Max.X, box.Max.Y, box.Max.Z, 1);
                if (aggregate) { min = Vector3.Min(min, box.Min); max = Vector3.Max(max, box.Max); }
                context.Produced(edit, boundsTag);
            }
            if (min != aggregateMin) NativeRows.Write(tableBytes, 0x80, min.X, min.Y, min.Z, 1);
            if (max != aggregateMax) NativeRows.Write(tableBytes, 0x90, max.X, max.Y, max.Z, 1);
        }
    }

    /** Scale, then rotate by the unit quaternion, then translate, as the client builds an instance matrix. */
    private static Matrix4x4 RowMatrix(NativeRows data, int row) =>
        Matrix4x4.CreateScale(data.F32(row + 28)) * Matrix4x4.CreateFromQuaternion(data.Rotation(row))
            * Matrix4x4.CreateTranslation(data.V3(row + 16));

    /** The eight corners of a box, moved, reduced to a new box. */
    internal static (Vector3 Min, Vector3 Max) TransformBox(Vector3 min, Vector3 max, Matrix4x4 transform)
    {
        Vector3 low = new(float.PositiveInfinity), high = new(float.NegativeInfinity);
        for (int corner = 0; corner < 8; corner++)
        {
            var point = Vector3.Transform(new Vector3((corner & 1) == 0 ? min.X : max.X,
                (corner & 2) == 0 ? min.Y : max.Y, (corner & 4) == 0 ? min.Z : max.Z), transform);
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
                throw new InvalidDataException("The edited instance bounds exceed finite range.");
            low = Vector3.Min(low, point); high = Vector3.Max(high, point);
        }
        return (low, high);
    }
}
