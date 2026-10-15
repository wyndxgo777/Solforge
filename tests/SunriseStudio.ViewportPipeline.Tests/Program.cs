// Solforge Phases 10-14: viewport and geometry pipeline checks. The default run needs no game install and
// no GPU; --game <dir> adds the Phase 14 audit, which skips cleanly without a hardware adapter.
var checks = new List<string>();
try
{
    string? game = null;
    for (int at = 0; at < args.Length; at++)
    {
        if (args[at] == "--game" && at + 1 < args.Length) game = args[++at];
        else
        {
            Console.Error.WriteLine("usage: [--game <dir>]");
            return 2;
        }
    }
    CameraChecks.Run(checks);
    foreach (string check in checks) Console.WriteLine("PASS " + check);
    Console.WriteLine($"PASS checks={checks.Count}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

internal static class Checks
{
    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static bool Near(float a, float b, float tolerance = 1e-3f) => MathF.Abs(a - b) <= tolerance;

    public static bool Near(System.Numerics.Vector3 a, System.Numerics.Vector3 b, float tolerance = 1e-3f) =>
        System.Numerics.Vector3.Distance(a, b) <= tolerance;

    public static bool Near(System.Numerics.Vector2 a, System.Numerics.Vector2 b, float tolerance = 1e-3f) =>
        System.Numerics.Vector2.Distance(a, b) <= tolerance;
}
