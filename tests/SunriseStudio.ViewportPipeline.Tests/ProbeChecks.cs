using System.Numerics;
using SunriseStudio.Rendering;
using static Checks;

/** Phase 12: cubemap SH projection (ambient diffuse) and box reflection probes. */
internal static class ProbeChecks
{
    public static void Run(List<string> checks)
    {
        Directions();
        checks.Add("cube texel directions follow the D3D face order and orientation");
        Ambient();
        checks.Add("a constant cube lights every normal to its radiance; a hemisphere sky gives 1, 1/2 and 0");
        Boxes();
        checks.Add("box probes weigh 1 inside, fade to 0 at their faces, follow rotation, and nested boxes win");
        Parallax();
        checks.Add("box-projected reflections aim from the capture point at where the ray leaves the box");
    }

    private static Vector3[][] Cube(int size, Func<Vector3, Vector3> radiance)
    {
        var faces = new Vector3[6][];
        for (int face = 0; face < 6; face++)
        {
            faces[face] = new Vector3[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    faces[face][y * size + x] = radiance(ProbeLighting.CubeDirection(face, (x + .5f) * 2 / size - 1, (y + .5f) * 2 / size - 1));
        }
        return faces;
    }

    private static void Directions()
    {
        Vector3[] axes = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        for (int face = 0; face < 6; face++)
        {
            Require(Near(ProbeLighting.CubeDirection(face, 0, 0), axes[face], 1e-6f), $"Face {face} centre.");
            var corner = ProbeLighting.CubeDirection(face, .9f, -.9f);
            Require(Vector3.Dot(corner, axes[face]) > .5f, $"Face {face} corner left its face.");
        }
        // +Z face: s runs toward +X and t downward toward -Y.
        Require(ProbeLighting.CubeDirection(4, 1, 0).X > 0 && ProbeLighting.CubeDirection(4, 0, 1).Y < 0, "+Z face orientation.");
    }

    private static void Ambient()
    {
        var colour = new Vector3(.2f, .4f, .8f);
        var constant = ProbeLighting.ProjectCubemap(Cube(8, _ => colour), 8);
        foreach (var normal in new[] { Vector3.UnitZ, -Vector3.UnitX, Vector3.Normalize(new Vector3(1, 2, -3)) })
            Require(Near(constant.Irradiance(normal), colour, 1e-3f), $"Constant cube at {normal}: {constant.Irradiance(normal)}.");
        var sky = ProbeLighting.ProjectCubemap(Cube(32, d => d.Z > 0 ? Vector3.One : Vector3.Zero), 32);
        Require(Near(sky.Irradiance(Vector3.UnitZ).X, 1, .03f), $"Sky up: {sky.Irradiance(Vector3.UnitZ)}.");
        Require(Near(sky.Irradiance(Vector3.UnitX).X, .5f, .03f), $"Sky horizon: {sky.Irradiance(Vector3.UnitX)}.");
        Require(sky.Irradiance(-Vector3.UnitZ).X < .03f, $"Sky down: {sky.Irradiance(-Vector3.UnitZ)}.");
        var half = SphericalHarmonics9.Lerp(constant, sky, .5f).Irradiance(Vector3.UnitZ);
        Require(Near(half, (colour + Vector3.One) / 2, .03f), $"Probe lerp: {half}.");
    }

    private static void Boxes()
    {
        var box = new ReflectionProbeBox(Vector3.Zero, Vector3.Zero, new(2, 2, 2), Quaternion.Identity, .5f);
        Require(ProbeLighting.BoxWeight(box, Vector3.Zero) == 1, "Box centre weight.");
        Require(Near(ProbeLighting.BoxWeight(box, new(1.75f, 0, 0)), .5f, 1e-5f), "Fade band weight.");
        Require(ProbeLighting.BoxWeight(box, new(2.5f, 0, 0)) == 0, "Outside weight.");
        var turned = new ReflectionProbeBox(Vector3.Zero, Vector3.Zero, new(4, 1, 1),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), .5f);
        Require(ProbeLighting.BoxWeight(turned, new(0, 3, 0)) == 1 && ProbeLighting.BoxWeight(turned, new(3, 0, 0)) == 0,
            "A rotated box should cover its own long axis only.");
        var room = new ReflectionProbeBox(Vector3.Zero, Vector3.Zero, new(10, 10, 10), Quaternion.Identity, 1);
        var alcove = new ReflectionProbeBox(Vector3.Zero, Vector3.Zero, new(1, 1, 1), Quaternion.Identity, .5f);
        var weights = ProbeLighting.BlendWeights([room, alcove], Vector3.Zero);
        Require(weights[1] == 1 && weights[0] == 0, $"Nested probe should win at its centre: {weights[0]}, {weights[1]}.");
        weights = ProbeLighting.BlendWeights([room, alcove], new(.75f, 0, 0));
        Require(Near(weights[1], .5f, 1e-5f) && Near(weights[0], .5f, 1e-5f), $"Nested fade: {weights[0]}, {weights[1]}.");
        weights = ProbeLighting.BlendWeights([room, alcove], new(50, 0, 0));
        Require(weights.All(weight => weight == 0), "No probe should cover a far point.");
    }

    private static void Parallax()
    {
        var box = new ReflectionProbeBox(Vector3.Zero, Vector3.Zero, Vector3.One, Quaternion.Identity, 0);
        Require(Near(ProbeLighting.BoxProjectedDirection(box, Vector3.Zero, Vector3.UnitY), Vector3.UnitY), "Centre lookup.");
        var corrected = ProbeLighting.BoxProjectedDirection(box, new(.5f, 0, 0), Vector3.UnitY);
        Require(Near(corrected, Vector3.Normalize(new Vector3(.5f, 1, 0))), $"Parallax lookup: {corrected}.");
        var outside = ProbeLighting.BoxProjectedDirection(box, new(5, 0, 0), Vector3.UnitY);
        Require(Near(outside, Vector3.UnitY), "Points outside the box keep the plain direction.");
    }
}
