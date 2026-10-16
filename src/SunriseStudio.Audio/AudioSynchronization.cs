/** File: Cues, meters and sync points that decide when a music transition may fire. */
namespace SunriseStudio.Audio;

/** A named cue at a position in milliseconds. */
public sealed record AudioCue(uint Id, double PositionMs, string? Name = null);
/** Beat, bar and grid lengths in frames. */
public sealed record AudioMeterFrames(uint Beat, uint Bar, uint Grid, uint GridOffset);
/** A frame offset from entry reached through a cue or meter, with the kind of sync. */
public sealed record AudioSyncPoint(uint FrameFromEntry, uint Cue, int Kind, uint MeterOwner = 0)
{
    public double SecondsFromEntry => (double)FrameFromEntry / AudioSynchronization.SampleRate;
}

/** Tempo and grid of a segment. */
public sealed record AudioMeter(float Tempo, int BeatsPerBar, int BeatValue, double GridPeriodMs, double GridOffsetMs)
{
    /** A bar multiplies the rounded beat length, rather than rounding its full duration. */
    public AudioMeterFrames Frames()
    {
        if (!float.IsFinite(Tempo) || Tempo <= 0 || BeatsPerBar is < 1 or > 255 || BeatValue is < 1 or > 255
            || !double.IsFinite(GridPeriodMs) || !double.IsFinite(GridOffsetMs) || GridPeriodMs <= 0 || GridOffsetMs < 0)
            throw new InvalidDataException("Music meter is invalid.");
        uint beat = AudioSynchronization.RoundFrames(240.0 / BeatValue / Tempo * AudioSynchronization.SampleRate);
        uint grid = AudioSynchronization.MillisecondsToFrames(GridPeriodMs);
        if (beat == 0 || grid == 0) throw new InvalidDataException("Music meter period is shorter than one sample.");
        return new(beat, checked(beat * (uint)BeatsPerBar), grid, AudioSynchronization.MillisecondsToFrames(GridOffsetMs));
    }
}

/** Music synchronization uses the output sample clock and preserves authored cue order. */
public static class AudioSynchronization
{
    public const int SampleRate = 48000;

    /** The meter in effect for a segment and the ancestor that owns it. */
    public static (uint Owner, AudioMeter Meter) Meter(AudioAnalysis analysis, uint segment)
    {
        var seen = new HashSet<uint>(); uint current = segment;
        while (current != 0)
        {
            if (!seen.Add(current)) throw new InvalidDataException("Music meter inheritance contains a cycle.");
            if (analysis.Meters.TryGetValue(current, out var meter)) return (current, meter);
            if (!analysis.Routes.TryGetValue(current, out var route)) break;
            current = route.Parent;
        }
        throw new NotSupportedException("The source has no loaded authored meter override.");
    }

    /** Strict beat boundaries advance one sample; custom cue filtering has its own rules. */
    public static AudioSyncPoint Resolve(AudioAnalysis analysis, uint segment, int kind, double requestSeconds,
        uint cueFilter = 0, bool strict = true)
    {
        if (!analysis.Segments.TryGetValue(segment, out var source)) throw new InvalidDataException("Music source segment is missing.");
        if (!double.IsFinite(requestSeconds) || requestSeconds < 0 || requestSeconds > 1200)
            throw new ArgumentOutOfRangeException(nameof(requestSeconds));
        uint request = RoundFrames(requestSeconds * SampleRate);
        uint entry = MillisecondsToFrames(source.Entry), exit = MillisecondsToFrames(source.Exit);
        if (exit < entry || request > exit - entry) throw new InvalidDataException("The transition request is outside the source entry and exit cues.");
        var cues = source.Cues ?? [new(0, source.Entry), new(0, source.Exit)];
        if (cues.Count < 2) throw new InvalidDataException("Music source has fewer than two cues.");
        if (kind == 0) return new(request, 0, kind);
        if (kind == 7) return new(exit - entry, cues[^1].Id, kind);
        if (kind == 6 && request == 0 && !strict) return new(0, cues[0].Id, kind);
        if (kind is 1 or 2 or 3)
        {
            var (owner, authored) = Meter(analysis, segment); var meter = authored.Frames();
            uint period = kind == 1 ? meter.Grid : kind == 2 ? meter.Bar : meter.Beat;
            ulong minimum = request + (strict ? 1UL : 0UL), value = kind == 1 ? meter.GridOffset : 0;
            if (value < minimum) value += (minimum - value + period - 1) / period * period;
            if (value <= exit - entry) return new((uint)value, 0, kind, owner);
        }
        else if (kind is 4 or 5)
        {
            int start = kind == 5 || strict ? 1 : 0, end = kind == 5 ? cues.Count - 1 : cues.Count;
            for (int i = start; i < end; i++)
            {
                uint frame = MillisecondsToFrames(cues[i].PositionMs);
                if (frame < entry || frame > exit) throw new InvalidDataException("A music cue is outside entry and exit.");
                uint relative = frame - entry;
                if ((kind == 5 ? relative > request : relative >= request) && (cueFilter == 0 || cues[i].Id == cueFilter))
                    return new(relative, cues[i].Id, kind);
            }
            if (kind == 4) return new(exit - entry, cues[^1].Id, kind);
        }
        else if (kind != 6) throw new NotSupportedException($"Music synchronization type {kind} is not supported.");
        throw new NotSupportedException("No matching synchronization point remains in the selected segment.");
    }

    /** Display label for a sync kind. */
    public static string Label(int kind) => kind switch
    { 0 => "Immediate", 1 => "Next grid", 2 => "Next bar", 3 => "Next beat", 4 => "Next cue", 5 => "Next custom cue", 6 => "Entry cue", 7 => "Exit cue", _ => "Unsupported" };

    /** Converts milliseconds to a whole number of output frames. */
    internal static uint MillisecondsToFrames(double milliseconds) => RoundFrames(SampleRate * milliseconds * .001);
    /** Rounds a frame count, rejecting values that are negative or too large. */
    internal static uint RoundFrames(double value)
    {
        if (!double.IsFinite(value) || value < 0 || value > int.MaxValue - .5)
            throw new InvalidDataException("Music sample time is outside the supported range.");
        return checked((uint)Math.Round(value, MidpointRounding.AwayFromZero));
    }
}
