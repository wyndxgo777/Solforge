using System.Numerics;
using System.Reflection;
using System.Text.Json;
using SunriseStudio.Editor;
using SunriseStudio.Live;
using SunriseStudio.Viewport;

/** Checks the live report: detached reports claim no CUI link and extreme finite coordinates serialize their distances. */
internal static class LiveReportChecks
{
    /** A valid finite observation must remain valid JSON even when its distance exceeds float range. */
    internal static int Run(AssetViewport viewport)
    {
        string root = Path.Combine(Path.GetTempPath(), "sunrise-live-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var workspace = new StudioWorkspace();
            using var page = new LivePage();
            var extreme = new Vector3(float.MaxValue);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(LivePage).GetField("snapshot", flags)!.SetValue(page,
                new LiveSnapshot((ulong)Environment.TickCount64, 1, true, extreme,
                    new LiveCamera(-extreme, Vector3.UnitX, Vector3.UnitZ, 1, 1), false, null));
            typeof(LivePage).GetMethod("SaveReport", flags)!.Invoke(page, [workspace, viewport, root]);
            using var report = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(root).Single()));
            if (report.RootElement.GetProperty("cui").GetProperty("alive").GetBoolean())
                throw new InvalidDataException("A detached report claimed a live CUI link.");
            var follow = report.RootElement.GetProperty("follow");
            double expected = Math.Sqrt(3) * float.MaxValue;
            foreach (string distance in new[] { "camera_to_game_camera", "pivot_to_player" })
            {
                double actual = follow.GetProperty(distance).GetDouble();
                if (!double.IsFinite(actual) || actual <= float.MaxValue || Math.Abs(actual / expected - 1) >= 1e-14)
                    throw new InvalidDataException("Finite extreme coordinates lost their serializable distance.");
            }
            Console.WriteLine("live_report finite_extremes=serialized distances=double");
            return 1;
        }
        finally
        {
            if (Directory.Exists(root) && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, true);
        }
    }
}
