using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Animation;

/** CPU skinning of a cloth display stream: positions, unit normals and tangents, kept w lanes. */
static class ClothDisplayChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run()
    {
        Vector4[] bind =
        [
            new(1, 2, 3, 1), new(0, 0, 1, 0), new(1, 0, 0, -1),
            new(-2, .5f, 4, 1), new(0, 1, 0, 0), new(0, 0, 1, 1)
        ];
        var bytes = MemoryMarshal.AsBytes(bind.AsSpan()).ToArray();
        // Lane 1 has zero weight and a bone outside the palette, so it must never be read.
        VertexSkinWeights[] weights = [new(0, 99, 0, 0, new(1, 0, 0, 0)), new(1, 99, 0, 0, new(1, 0, 0, 0))];
        var output = new byte[bytes.Length];
        ClothDisplaySkin.Skin(bytes, weights, [Matrix4x4.Identity, Matrix4x4.Identity], output);
        Require(output.AsSpan().SequenceEqual(bytes), "An identity palette changed the cloth stream.");
        var yaw = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(0, 0, 5);
        ClothDisplaySkin.Skin(bytes, weights, [Matrix4x4.Identity, yaw], output);
        var posed = MemoryMarshal.Cast<byte, Vector4>(output);
        Require(Near(posed[3], new(-.5f, -2, 9, 1)), $"The yawed position is {posed[3]}.");
        Require(Near(posed[4], new(-1, 0, 0, 0)) && Near(posed[5], new(0, 0, 1, 1)),
            $"The yawed normal or tangent is {posed[4]}, {posed[5]}.");
        Require(Near(posed[0], bind[0]) && Near(posed[1], bind[1]) && Near(posed[2], bind[2]),
            "A vertex on the identity bone moved.");
        var half = new VertexSkinWeights[] { new(0, 1, 0, 0, new(.5f, .5f, 0, 0)), weights[1] };
        ClothDisplaySkin.Skin(bytes, half, [Matrix4x4.Identity, Matrix4x4.CreateRotationZ(MathF.PI / 2)], output);
        posed = MemoryMarshal.Cast<byte, Vector4>(output);
        Require(MathF.Abs(new Vector3(posed[1].X, posed[1].Y, posed[1].Z).Length() - 1) < 1e-5f
                && MathF.Abs(new Vector3(posed[2].X, posed[2].Y, posed[2].Z).Length() - 1) < 1e-5f && posed[2].W == -1,
            "A blended normal or tangent is not unit length or lost its w lane.");
        Console.WriteLine("cloth_display_skin identity=pass yaw=pass unit=pass w_lanes=pass zero_weight=pass");
    }

    private static bool Near(Vector4 a, Vector4 b) => Vector4.Distance(a, b) < 1e-5f;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
