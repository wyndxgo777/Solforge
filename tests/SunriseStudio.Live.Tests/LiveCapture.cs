using System.Diagnostics;
using System.Text.Json;
using SunriseStudio.Live;

/** Tool that attaches to one running game process and records live world frames to an output folder for later analysis. */
internal static class LiveCapture
{
    /** Captures read-only diagnostics without starting or changing the game. */
    public static int Run(string gameRoot, string output, int seconds)
    {
        var targets = LiveConnection.FindProcesses(gameRoot);
        if (targets.Count != 1) throw new InvalidOperationException($"Expected one local game process; found {targets.Count}.");
        using var connection = LiveConnection.Attach(targets[0]);
        var samples = new List<object>();
        var timer = Stopwatch.StartNew();
        int frames = 0, worlds = 0, placements = 0;
        uint lastMap = 0, lastScenario = 0;
        while (timer.Elapsed.TotalSeconds < Math.Clamp(seconds, 1, 180) && !connection.HasExited)
        {
            var frame = connection.Poll();
            if (frame is not null) frames++;
            if (connection.World is { } world)
            {
                worlds++;
                if (world.MapRootTag != lastMap || world.ScenarioTag != lastScenario)
                {
                    Console.WriteLine($"live map={world.MapRootTag:X8} definition={world.MapDefinitionTag:X8} scenario={world.ScenarioTag:X8} slices={world.Slices.Count}");
                    lastMap = world.MapRootTag; lastScenario = world.ScenarioTag;
                }
            }
            if (connection.Placements is { Available: true } observed) placements = Math.Max(placements, observed.Rows.Count);
            var batch = connection.Placements;
            samples.Add(new { Frame = frame, World = connection.World, Placements = batch is null ? null
                : new { batch.Time, batch.Available, batch.Complete, batch.Truncated, batch.Total, batch.Overflow,
                    batch.Epoch, Valid = batch.Rows.Count(row => row.TransformAvailable), Dynamic = batch.Rows.Count(row => row.Dynamic) } });
            Thread.Sleep(100);
        }
        string path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { Frames = frames, Worlds = worlds,
            MaximumPlacements = placements, Samples = samples, LastPlacements = connection.Placements },
            new JsonSerializerOptions { IncludeFields = true }));
        Console.WriteLine($"live capture={path} frames={frames} worlds={worlds} placements={placements}");
        return frames > 0 && worlds > 0 ? 0 : 1;
    }
}
