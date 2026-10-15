using System.Numerics;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;
using static Checks;

/** Phase 13: grid, angle, scale and surface-normal snapping of the transform handles. */
internal static class SnapChecks
{
    public static void Run(List<string> checks)
    {
        Grid();
        checks.Add("world-axis moves land the pivot on the grid; local-axis moves snap their length and stay on the axis");
        AnglesAndScale();
        checks.Add("turns snap to whole angle steps and scale factors to their step, never to zero");
        Surface();
        checks.Add("surface snapping moves the pivot onto the hit and turns the up axis onto the normal, facing the eye");
        Group();
        checks.Add("one snapped change about the group pivot keeps every member's offset (group transforms)");
    }

    private static void Grid()
    {
        var pivot = new Vector3(.3f, 2, 5);
        var move = TransformSnapping.SnapMove(pivot, new Vector3(1.4f, 0, 0), 1);
        Require(Near(pivot + move, new Vector3(2, 2, 5), 1e-5f), $"Grid move landed at {pivot + move}.");
        Require(TransformSnapping.SnapMove(pivot, Vector3.Zero, 1) == Vector3.Zero, "No move stays no move.");
        var diagonal = Vector3.Normalize(new Vector3(1, 1, 0));
        var local = TransformSnapping.SnapMove(pivot, diagonal * 2.6f, .5f);
        Require(Near(local, diagonal * 2.5f, 1e-5f), $"Local-axis move: {local}.");
        Require(TransformSnapping.SnapMove(pivot, new Vector3(1.4f, 0, 0), 0) == new Vector3(1.4f, 0, 0), "Step 0 leaves moves alone.");
        var settings = SnapSettings.Default with { Enabled = true, MoveStep = .25f };
        var change = TransformSnapping.Apply(TransformChange.Identity with { Move = new(0, .6f, 0) }, Vector3.Zero, Vector3.UnitY, 0, settings);
        Require(Near(change.Move, new Vector3(0, .5f, 0), 1e-6f), $"Apply move: {change.Move}.");
    }

    private static void AnglesAndScale()
    {
        var turn = TransformSnapping.SnapTurn(Vector3.UnitZ, 0.4f, 15);
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6);
        Require(MathF.Abs(Quaternion.Dot(turn, expected)) > .99999f, "0.4 rad should snap to 30 degrees.");
        var settings = SnapSettings.Default;
        var change = TransformSnapping.Apply(TransformChange.Identity with { Turn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .1f) },
            Vector3.Zero, Vector3.UnitX, .1f, settings);
        Require(change.Turn == Quaternion.Identity || MathF.Abs(Quaternion.Dot(change.Turn, Quaternion.Identity)) > .99999f,
            "A turn under half a step should snap back to none.");
        Require(Near(TransformSnapping.SnapScale(new Vector3(1.26f), .1f), new Vector3(1.3f), 1e-5f), "Uniform scale snap.");
        Require(Near(TransformSnapping.SnapScale(new Vector3(.02f, 1, 1), .1f), new Vector3(.1f, 1, 1), 1e-6f),
            "Scale never snaps to zero, and untouched axes stay 1.");
    }

    private static void Surface()
    {
        var up = TransformSnapping.FromTo(Vector3.UnitZ, Vector3.UnitX);
        Require(Near(Vector3.Transform(Vector3.UnitZ, up), Vector3.UnitX, 1e-5f), "FromTo +Z to +X.");
        var flip = TransformSnapping.FromTo(Vector3.UnitZ, -Vector3.UnitZ);
        Require(Near(Vector3.Transform(Vector3.UnitZ, flip), -Vector3.UnitZ, 1e-5f), "FromTo opposite directions.");
        Require(TransformSnapping.FromTo(Vector3.UnitY, Vector3.UnitY) == Quaternion.Identity, "FromTo same direction.");
        // A wall facing +X at x = 4, hit from an eye at the origin.
        var hit = new Vector3(4, 1, 2);
        var normal = TransformSnapping.SurfaceNormal(hit, hit + new Vector3(0, -.01f, 0), hit + new Vector3(0, 0, -.01f), Vector3.Zero);
        Require(normal is { } n && Near(n, -Vector3.UnitX, 1e-5f), $"Surface normal should face the eye: {normal}.");
        Require(TransformSnapping.SurfaceNormal(hit, hit, hit, Vector3.Zero) is null, "Degenerate hits give no normal.");
        var pivot = new Vector3(0, 0, 1);
        var change = TransformSnapping.AlignToSurface(pivot, Vector3.UnitZ, hit, -Vector3.UnitX, true);
        Require(Near(change.Apply(pivot, pivot), hit, 1e-5f), "The pivot should land on the hit.");
        var top = change.Apply(pivot + Vector3.UnitZ, pivot);
        Require(Near(top - hit, -Vector3.UnitX, 1e-5f), $"The up axis should follow the normal: {top - hit}.");
        var placeOnly = TransformSnapping.AlignToSurface(pivot, Vector3.UnitZ, hit, -Vector3.UnitX, false);
        Require(placeOnly.Turn == Quaternion.Identity, "Without align the selection keeps its rotation.");
    }

    private static void Group()
    {
        Vector3[] members = [new(1, 0, 0), new(-1, 2, 0), new(0, -3, 1)];
        var pivot = members.Aggregate(Vector3.Zero, (sum, member) => sum + member) / members.Length;
        var settings = SnapSettings.Default with { Enabled = true };
        var change = TransformSnapping.Apply(TransformChange.Identity with { Move = new(.8f, 0, 0) }, pivot, Vector3.UnitX, 0, settings);
        var moved = members.Select(member => change.Apply(member, pivot)).ToArray();
        for (int i = 1; i < members.Length; i++)
            Require(Near(moved[i] - moved[0], members[i] - members[0], 1e-5f), "Group members kept their offsets.");
        Require(Near(change.Apply(pivot, pivot).X, MathF.Round(pivot.X + .8f), 1e-5f), "The group pivot lands on the grid.");
    }
}
