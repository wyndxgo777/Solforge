using System.Diagnostics;
using System.Text.Json;

namespace SunriseStudio.Editor;

/** StudioForm part: per-frame prepare, draw and submit timings for the profiling capture. */
public sealed partial class StudioForm
{
    private sealed record FrameTiming(double PrepareMilliseconds, double DrawMilliseconds,
        double SubmitMilliseconds, long AllocatedBytes);
    private readonly List<FrameTiming> frameTimings = [];

    /** Frame samples omit loading and presentation waits. */
    private void RecordFrameProfile(long begin, long draw, long submit, long allocated)
    {
        long end = Stopwatch.GetTimestamp();
        frameTimings.Add(new(Stopwatch.GetElapsedTime(begin, draw).TotalMilliseconds,
            Stopwatch.GetElapsedTime(draw, submit).TotalMilliseconds,
            Stopwatch.GetElapsedTime(submit, end).TotalMilliseconds,
            GC.GetAllocatedBytesForCurrentThread() - allocated));
    }

    /** Profiling writes only when requested and drops the first five warmup samples. */
    private void WriteFrameProfile()
    {
        if (options.ProfileFrames is not { } path || frameTimings.Count == 0) return;
        var samples = frameTimings.Skip(Math.Min(5, frameTimings.Count - 1)).ToArray();
        double Quantile(IEnumerable<double> values, double fraction)
        {
            var ordered = values.Order().ToArray();
            return ordered[(int)Math.Clamp(Math.Ceiling(ordered.Length * fraction) - 1, 0, ordered.Length - 1)];
        }
        var totals = samples.Select(frame => frame.PrepareMilliseconds + frame.DrawMilliseconds + frame.SubmitMilliseconds).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Adapter = renderer?.AdapterName, ClientSize, Frames = samples.Length,
            MedianMilliseconds = Quantile(totals, .5), P95Milliseconds = Quantile(totals, .95),
            MedianAllocatedBytes = Quantile(samples.Select(frame => (double)frame.AllocatedBytes), .5), Samples = samples
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
