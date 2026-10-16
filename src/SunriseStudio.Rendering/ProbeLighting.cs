using System.Numerics;

namespace SunriseStudio.Rendering;

/** Nine RGB spherical-harmonic coefficients (bands 0 to 2) of a radiance environment. */
public sealed class SphericalHarmonics9
{
    public Vector3[] Coefficients { get; } = new Vector3[9];

    /** Real SH basis in the usual order: Y00, Y1-1, Y10, Y11, Y2-2, Y2-1, Y20, Y21, Y22. */
    public static void Basis(Vector3 d, Span<float> output)
    {
        output[0] = 0.282095f;
        output[1] = 0.488603f * d.Y;
        output[2] = 0.488603f * d.Z;
        output[3] = 0.488603f * d.X;
        output[4] = 1.092548f * d.X * d.Y;
        output[5] = 1.092548f * d.Y * d.Z;
        output[6] = 0.315392f * (3 * d.Z * d.Z - 1);
        output[7] = 1.092548f * d.X * d.Z;
        output[8] = 0.546274f * (d.X * d.X - d.Y * d.Y);
    }

    /**
     * Diffuse irradiance E(n) / pi for a surface facing n (Ramamoorthi and Hanrahan), so a constant
     * radiance L lights a white Lambertian surface to exactly L.
     */
    public Vector3 Irradiance(Vector3 normal)
    {
        var n = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitZ;
        Span<float> basis = stackalloc float[9];
        Basis(n, basis);
        // Band weights A0 = pi, A1 = 2pi/3, A2 = pi/4, divided by pi.
        var sum = Vector3.Zero;
        for (int i = 0; i < 9; i++) sum += Coefficients[i] * basis[i] * (i == 0 ? 1f : i < 4 ? 2f / 3 : .25f);
        return Vector3.Max(sum, Vector3.Zero);
    }

    /** Per-coefficient linear interpolation of two probes. */
    public static SphericalHarmonics9 Lerp(SphericalHarmonics9 a, SphericalHarmonics9 b, float t)
    {
        var result = new SphericalHarmonics9();
        for (int i = 0; i < 9; i++) result.Coefficients[i] = Vector3.Lerp(a.Coefficients[i], b.Coefficients[i], t);
        return result;
    }
}

/** A box-shaped reflection probe: the cubemap's capture point, its influence box and the fade at its faces. */
public readonly record struct ReflectionProbeBox(Vector3 Capture, Vector3 Center, Vector3 HalfExtents, Quaternion Rotation, float Fade);

/**
 * Solforge Phase 12: CPU side of cubemap reflection probes and ambient diffuse. The game-shader path draws the
 * authored probes itself (NativeCubemapLighting, NativeAmbientLighting); this projects decoded cubemaps to SH
 * irradiance for the geometry preview and export, and weighs and parallax-corrects box probes.
 */
public static class ProbeLighting
{
    /** D3D cube face order +X, -X, +Y, -Y, +Z, -Z; s and t run -1 to 1 across the face, t downward. */
    public static Vector3 CubeDirection(int face, float s, float t) => Vector3.Normalize(face switch
    {
        0 => new Vector3(1, -t, -s),
        1 => new Vector3(-1, -t, s),
        2 => new Vector3(s, 1, t),
        3 => new Vector3(s, -1, -t),
        4 => new Vector3(s, -t, 1),
        5 => new Vector3(-s, -t, -1),
        _ => throw new ArgumentOutOfRangeException(nameof(face))
    });

    /**
     * Projects a cubemap of linear RGB radiance onto SH. Texels are weighed by their solid angle, and the
     * total is renormalised to 4 pi so a constant cube projects exactly.
     * @param faces Six faces of size * size texels, row major.
     */
    public static SphericalHarmonics9 ProjectCubemap(IReadOnlyList<Vector3[]> faces, int size)
    {
        if (faces.Count != 6) throw new ArgumentException("A cubemap has six faces.", nameof(faces));
        if (size <= 0 || faces.Any(face => face.Length != size * size)) throw new ArgumentException("Every face needs size * size texels.", nameof(faces));
        var result = new SphericalHarmonics9();
        Span<float> basis = stackalloc float[9];
        double total = 0;
        float texel = 2f / size;
        for (int face = 0; face < 6; face++)
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float s = (x + .5f) * texel - 1, t = (y + .5f) * texel - 1;
                    float weight = 4f / (MathF.Pow(1 + s * s + t * t, 1.5f) * size * size);
                    total += weight;
                    SphericalHarmonics9.Basis(CubeDirection(face, s, t), basis);
                    var radiance = faces[face][y * size + x];
                    for (int i = 0; i < 9; i++) result.Coefficients[i] += radiance * (basis[i] * weight);
                }
        float normalise = (float)(4 * Math.PI / total);
        for (int i = 0; i < 9; i++) result.Coefficients[i] *= normalise;
        return result;
    }

    /** 1 inside the box beyond the fade band, falling linearly to 0 at the faces; 0 outside. */
    public static float BoxWeight(ReflectionProbeBox probe, Vector3 point)
    {
        var local = Vector3.Transform(point - probe.Center, Quaternion.Inverse(probe.Rotation));
        var inside = probe.HalfExtents - Vector3.Abs(local);
        float nearest = MathF.Min(inside.X, MathF.Min(inside.Y, inside.Z));
        if (nearest < 0) return 0;
        return probe.Fade <= 0 ? 1 : MathF.Min(1, nearest / probe.Fade);
    }

    /**
     * Normalised weights of the probes covering a point; the smaller box wins ties, as a nested probe should.
     * @return One weight per probe that sums to 1, or all zero when no probe covers the point.
     */
    public static float[] BlendWeights(IReadOnlyList<ReflectionProbeBox> probes, Vector3 point)
    {
        var weights = new float[probes.Count];
        var order = Enumerable.Range(0, probes.Count)
            .OrderBy(i => probes[i].HalfExtents.X * probes[i].HalfExtents.Y * probes[i].HalfExtents.Z).ToArray();
        float remaining = 1;
        foreach (int i in order)
        {
            if (remaining <= 0) break;
            float weight = BoxWeight(probes[i], point) * remaining;
            weights[i] = weight;
            remaining -= weight;
        }
        float sum = weights.Sum();
        if (sum > 0) for (int i = 0; i < weights.Length; i++) weights[i] /= sum;
        return weights;
    }

    /**
     * Box-projected (parallax-corrected) lookup: the direction from the capture point to where the reflected ray
     * from the shaded point leaves the influence box. Points outside the box keep the plain direction.
     */
    public static Vector3 BoxProjectedDirection(ReflectionProbeBox probe, Vector3 point, Vector3 direction)
    {
        var inverse = Quaternion.Inverse(probe.Rotation);
        var local = Vector3.Transform(point - probe.Center, inverse);
        var ray = Vector3.Transform(Vector3.Normalize(direction), inverse);
        if (Vector3.Abs(local) is var a && (a.X > probe.HalfExtents.X || a.Y > probe.HalfExtents.Y || a.Z > probe.HalfExtents.Z))
            return Vector3.Normalize(direction);
        float distance = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            if (MathF.Abs(ray[axis]) < 1e-8f) continue;
            float plane = ray[axis] > 0 ? probe.HalfExtents[axis] : -probe.HalfExtents[axis];
            distance = MathF.Min(distance, (plane - local[axis]) / ray[axis]);
        }
        var hit = probe.Center + Vector3.Transform(local + ray * distance, probe.Rotation);
        var corrected = hit - probe.Capture;
        return corrected.LengthSquared() > 1e-12f ? Vector3.Normalize(corrected) : Vector3.Normalize(direction);
    }
}
