using System.Numerics;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/**
 * CB11 for entity shaders that skin through a transform palette at row 8.
 * At rest every palette entry is the placement transform: vertices are stored in object space.
 */
public static class NativeEntityPalettes
{
    // The draw window: rows 0 to 7 as rigid_model, then the palette.
    public const int Rows = 64;
    private const int FirstPaletteRow = 8;

    /** Fills every palette entry; bone and chunk indices beyond the part's palette read the same entry. */
    public static Vector4[] Build(MeshInstance instance, NativeEntityPalette form)
    {
        var rows = new Vector4[Rows];
        AssetSceneLoader.ReadNativeEntityConstants(instance).CopyTo(rows, 0);
        var m = instance.RenderTransform;
        if (form == NativeEntityPalette.Matrix)
        {
            // Three rows per chunk hold the transposed 3x4 world matrix, as the static packing does.
            for (int row = FirstPaletteRow; row + 3 <= Rows; row += 3)
            {
                rows[row] = new(m.M11, m.M21, m.M31, m.M41);
                rows[row + 1] = new(m.M12, m.M22, m.M32, m.M42);
                rows[row + 2] = new(m.M13, m.M23, m.M33, m.M43);
            }
            return rows;
        }
        if (form != NativeEntityPalette.DualQuaternion) throw new ArgumentOutOfRangeException(nameof(form));
        var (rotation, translation, scale) = Decompose(m);
        // A dual quaternion carries no scale; uniform placement scale folds into row 5, the object offset and scale.
        rows[5] = new Vector4(rows[5].X * scale, rows[5].Y * scale, rows[5].Z * scale, rows[5].W * scale);
        var dual = DualPart(rotation, translation);
        for (int row = FirstPaletteRow; row + 2 <= Rows; row += 2)
        {
            rows[row] = new(rotation.X, rotation.Y, rotation.Z, rotation.W);
            rows[row + 1] = dual;
        }
        Check(rows, m);
        return rows;
    }

    /**
     * Palette rows for a skinned part: entry b is skin matrix b times the placement.
     * Vertex bone and chunk indices are skeleton bone indices, so the palette has one entry per bone.
     * @return The fixed rows 0 to 7, then the entries, padded to a multiple of 16 rows.
     */
    public static Vector4[] BuildSkinned(MeshInstance instance, NativeEntityPalette form, IReadOnlyList<Matrix4x4> skin)
    {
        int perEntry = form switch
        {
            NativeEntityPalette.Matrix => 3, NativeEntityPalette.DualQuaternion => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(form))
        };
        int count = FirstPaletteRow + skin.Count * perEntry;
        var rows = new Vector4[Math.Max(Rows, (count + 15) / 16 * 16)];
        if (rows.Length > MaximumRows) throw new NotSupportedException($"A {skin.Count}-bone palette exceeds {MaximumRows} rows.");
        AssetSceneLoader.ReadNativeEntityConstants(instance).CopyTo(rows, 0);
        var placement = instance.RenderTransform;
        if (form == NativeEntityPalette.Matrix)
        {
            for (int bone = 0; bone < skin.Count; bone++)
            {
                var m = skin[bone] * placement;
                int row = FirstPaletteRow + bone * 3;
                rows[row] = new(m.M11, m.M21, m.M31, m.M41);
                rows[row + 1] = new(m.M12, m.M22, m.M32, m.M42);
                rows[row + 2] = new(m.M13, m.M23, m.M33, m.M43);
            }
            return rows;
        }
        // A dual quaternion carries no scale: the placement's uniform scale folds into row 5, bone scale is dropped.
        float scale = Decompose(placement).Scale;
        rows[5] *= scale;
        for (int bone = 0; bone < skin.Count; bone++)
        {
            var m = skin[bone] * placement;
            var rotation = RotationOf(m);
            int row = FirstPaletteRow + bone * 2;
            rows[row] = new(rotation.X, rotation.Y, rotation.Z, rotation.W);
            rows[row + 1] = DualPart(rotation, m.Translation);
        }
        return rows;
    }

    // The CB11 window limit of a D3D11 constant buffer, in vectors.
    public const int MaximumRows = 4096;

    /** Throws when a dual-quaternion placement cannot be decomposed. */
    public static void ValidatePlacement(Matrix4x4 placement, NativeEntityPalette form)
    { if (form == NativeEntityPalette.DualQuaternion && !RequiresDiagnosticPlacement(placement, form)) _ = Decompose(placement); }

    /** Collapsed or reflected dual-quaternion placements use diagnostic geometry without changing scale. */
    public static bool RequiresDiagnosticPlacement(Matrix4x4 placement, NativeEntityPalette form) =>
        form == NativeEntityPalette.DualQuaternion && placement.GetDeterminant() <= 0;

    /** Rotation of a matrix whose axes are orthogonal; their lengths are ignored. */
    private static Quaternion RotationOf(Matrix4x4 m)
    {
        var x = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
        var y = Vector3.Normalize(new Vector3(m.M21, m.M22, m.M23));
        var z = Vector3.Normalize(new Vector3(m.M31, m.M32, m.M33));
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1)));
    }

    /** Rotation, translation and uniform scale of a row-vector placement; shear and uneven scale are refused. */
    private static (Quaternion Rotation, Vector3 Translation, float Scale) Decompose(Matrix4x4 m)
    {
        var x = new Vector3(m.M11, m.M12, m.M13); var y = new Vector3(m.M21, m.M22, m.M23); var z = new Vector3(m.M31, m.M32, m.M33);
        float scale = x.Length();
        if (scale < 1e-6f || MathF.Abs(y.Length() - scale) > 1e-3f * scale || MathF.Abs(z.Length() - scale) > 1e-3f * scale
            || MathF.Abs(Vector3.Dot(x, y)) > 1e-3f * scale * scale || MathF.Abs(Vector3.Dot(x, z)) > 1e-3f * scale * scale
            || MathF.Abs(Vector3.Dot(y, z)) > 1e-3f * scale * scale || Vector3.Dot(Vector3.Cross(x, y), z) <= 0)
            throw new NotSupportedException("Dual-quaternion skinning needs a placement with uniform scale.");
        var rotationMatrix = new Matrix4x4(x.X / scale, x.Y / scale, x.Z / scale, 0, y.X / scale, y.Y / scale, y.Z / scale, 0,
            z.X / scale, z.Y / scale, z.Z / scale, 0, 0, 0, 0, 1);
        return (Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotationMatrix)), m.Translation, scale);
    }

    /** qd = 0.5 * (t, 0) * qr, so the shader's 2 * qd * conj(qr) returns t. */
    private static Vector4 DualPart(Quaternion q, Vector3 t)
    {
        var v = new Vector3(q.X, q.Y, q.Z);
        var d = 0.5f * (q.W * t + Vector3.Cross(t, v));
        return new(d.X, d.Y, d.Z, -0.5f * Vector3.Dot(t, v));
    }

    /** The shader's rotation and translation formulas must reproduce the placement for a test point. */
    private static void Check(Vector4[] rows, Matrix4x4 m)
    {
        var p = new Vector3(0.3f, -0.7f, 1.1f);
        var q = rows[FirstPaletteRow]; var d = rows[FirstPaletteRow + 1];
        var qv = new Vector3(q.X, q.Y, q.Z); var dv = new Vector3(d.X, d.Y, d.Z);
        var local = p * rows[5].W + new Vector3(rows[5].X, rows[5].Y, rows[5].Z);
        var world = local + 2 * Vector3.Cross(qv, q.W * local + Vector3.Cross(qv, local))
            + 2 * (q.W * dv - d.W * qv + Vector3.Cross(qv, dv));
        float scale = new Vector3(m.M11, m.M12, m.M13).Length();
        var expected = Vector3.Transform(local / scale, m);
        if (Vector3.Distance(world, expected) > 1e-3f * MathF.Max(1, expected.Length()))
            throw new InvalidDataException("The dual-quaternion palette does not reproduce the placement.");
    }
}
