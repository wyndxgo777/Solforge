/** File: Destination points of a transition (where the next segment starts) and their labels. */
namespace SunriseStudio.Audio;

/** Where in the destination segment a transition lands, with its random-choice position. */
public sealed record AudioDestinationPoint(uint FrameFromEntry, uint Cue, int Kind, int ChoiceCount, int ChoiceIndex)
{
    public double SecondsFromEntry => (double)FrameFromEntry / AudioSynchronization.SampleRate;
    public bool AllowsPreEntry => FrameFromEntry == 0 && Kind != 1;
}

/** Destination randomness is an explicit audition choice, independent of the game's random seed. */
public static class AudioDestination
{
    /** Random-cue mode keeps entry eligible even when its ID differs from the name filter. */
    public static AudioDestinationPoint Resolve(AudioSegment segment, int kind, uint sourceFrame = 0,
        uint sourceCue = 0, uint cueFilter = 0, bool matchSourceCue = false, int variation = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(variation);
        uint entry = AudioSynchronization.MillisecondsToFrames(segment.Entry);
        uint exit = AudioSynchronization.MillisecondsToFrames(segment.Exit);
        if (exit < entry) throw new InvalidDataException("Destination exit precedes its entry cue.");
        var cues = segment.Cues ?? [new(0, segment.Entry), new(0, segment.Exit)];
        if (cues.Count < 2) throw new InvalidDataException("Destination has fewer than two cues.");
        if (kind == 0) return new(0, cues[0].Id, kind, 1, 0);
        if (kind == 1) return new(exit == entry ? 0 : sourceFrame % (exit - entry), 0, kind, 1, 0);
        if (kind is not (2 or 3)) throw new NotSupportedException($"Destination entry type {kind} is not supported.");
        uint filter = matchSourceCue ? sourceCue : cueFilter;
        var candidates = new List<AudioCue>();
        if (kind == 2) candidates.Add(cues[0]);
        for (int i = 1; i < cues.Count - 1; i++)
        {
            uint position = AudioSynchronization.MillisecondsToFrames(cues[i].PositionMs);
            if (position < entry || position > exit) throw new InvalidDataException("Destination custom cue is outside entry and exit.");
            if (filter == 0 || cues[i].Id == filter) candidates.Add(cues[i]);
        }
        if (candidates.Count == 0) return new(0, cues[0].Id, kind, 1, 0);
        int index = variation % candidates.Count;
        var selected = candidates[index];
        return new(AudioSynchronization.MillisecondsToFrames(selected.PositionMs) - entry,
            selected.Id, kind, candidates.Count, index);
    }

    /** Display label for a destination kind. */
    public static string Label(int kind) => kind switch
    { 0 => "Entry cue", 1 => "Same time", 2 => "Random cue", 3 => "Random custom cue", _ => "Unsupported" };
}
