/** File: Clip automation curves (volume or gain over time) evaluated by frame. */
namespace SunriseStudio.Audio;

/** One automation key: time, value and curve shape. */
public sealed record AudioAutomationPoint(float Seconds, float Value, int Curve);
/** The automation points of one clip for one parameter type. */
public sealed record AudioClipAutomation(uint ClipIndex, int Type, IReadOnlyList<AudioAutomationPoint> Points);

/** Clip automation uses sample-rounded times relative to the trimmed clip start. */
public sealed class AudioAutomationCurve
{
    private readonly float[] frames;
    private readonly AudioAutomationPoint[] points;
    public int Type { get; }

    /** Rounds point times to sample frames; points must be finite and in time order. */
    public AudioAutomationCurve(AudioClipAutomation automation, int sampleRate = 48000)
    {
        if (automation.Type is not (0 or 3 or 4)) throw new NotSupportedException($"Clip automation type {automation.Type} is not supported.");
        if (sampleRate is < 8000 or > 192000 || automation.Points.Count is < 1 or > 65536)
            throw new InvalidDataException("Clip automation point count or sample rate is invalid.");
        Type = automation.Type; points = automation.Points.ToArray(); frames = new float[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            var point = points[i];
            if (!float.IsFinite(point.Seconds) || !float.IsFinite(point.Value) || Math.Abs(point.Seconds) > int.MaxValue / (double)sampleRate
                || point.Curve is < 0 or > 9 || i > 0 && point.Seconds < points[i - 1].Seconds)
                throw new InvalidDataException("Clip automation points are invalid or unordered.");
            frames[i] = (float)Math.Round((double)point.Seconds * sampleRate, MidpointRounding.AwayFromZero);
        }
    }

    /** Graph endpoints hold their authored value; constant segments do not interpolate. */
    public float ValueAt(float frame)
    {
        if (!float.IsFinite(frame)) throw new InvalidDataException("Clip automation time is invalid.");
        int lower = 0, upper = frames.Length;
        while (lower < upper)
        {
            int middle = lower + (upper - lower) / 2;
            if (frames[middle] < frame) lower = middle + 1; else upper = middle;
        }
        if (lower == 0) return points[0].Value;
        if (lower == frames.Length) return points[^1].Value;
        if (frames[lower] == frame) return points[lower].Value;
        int previous = lower - 1;
        float progress = (frame - frames[previous]) / (frames[lower] - frames[previous]);
        return AudioCurves.Evaluate(points[previous].Curve, progress, points[previous].Value, points[lower].Value);
    }

    /** Gain multiplier at a frame; type 0 is stored as an offset from unity. */
    public float GainAt(float frame) => Type == 0 ? 1f + ValueAt(frame) : ValueAt(frame);
}
