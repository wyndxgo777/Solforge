/** Console entry for the mission UI tests; needs an owned installation path argument. */
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--pending-lua"]) return PendingLuaChecks.BehaviorOnly();
        if (args.Length < 1) { Console.Error.WriteLine("Pass an owned installation path."); return 2; }
        int typed = Array.IndexOf(args, "--api-nodes");
        if (typed >= 0) return ApiNodeUiChecks.Run(args[0], args[typed + 1], typed + 2 < args.Length ? args[typed + 2] : null);
        int controls = Array.IndexOf(args, "--control-flow");
        if (controls >= 0) return ControlFlowUiChecks.Run(args[0], controls + 1 < args.Length ? args[controls + 1] : null);
        int problems = Array.IndexOf(args, "--sdk-problems");
        if (problems >= 0) return SdkProblemUiChecks.Run(args[0], problems + 1 < args.Length ? args[problems + 1] : null);
        int scenes = Array.IndexOf(args, "--scenes");
        if (scenes >= 0) return SceneUiChecks.Run(args[0],
            scenes + 1 < args.Length && !args[scenes + 1].StartsWith("--", StringComparison.Ordinal) ? args[scenes + 1] : null);
        return TypedAuthoringUiChecks.Run(args[0], args.Length > 1 ? args[1] : null);
    }
}
