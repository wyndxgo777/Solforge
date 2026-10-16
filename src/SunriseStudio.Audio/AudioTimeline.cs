using System.Buffers.Binary;

namespace SunriseStudio.Audio;

/** @param Pan 2D panner position 0..1 when the panner is on; null leaves the panner off. */
public sealed record AudioTimelineClip(uint Segment, uint Track, uint Media, double StartSeconds,
    double SourceSeconds, double DurationSeconds, float GainDb, AudioEnvelope? FadeIn = null,
    AudioEnvelope? FadeOut = null, double LoopSeconds = 0,
    IReadOnlyList<AudioClipAutomation>? Automation = null, double AutomationOffsetSeconds = 0, float? Pan = null);
/** Where one segment starts, enters, exits and ends on the timeline, in seconds. */
public sealed record AudioTimelineSegment(uint Id, string Name, double StartSeconds,
    double EntrySeconds, double ExitSeconds, double EndSeconds);
/** A planned render: segments, clips, duration and notices. */
public sealed record AudioTimelinePlan(uint Playlist, IReadOnlyList<AudioTimelineSegment> Segments,
    IReadOnlyList<AudioTimelineClip> Clips, double DurationSeconds, IReadOnlyList<string> Notices,
    AudioSyncPoint? Synchronization = null, AudioDestinationPoint? Destination = null, AudioPlaylistPath? PlaylistPath = null);
/** Result of a render: the WAV path, clipped sample count, peak and overlap frames. */
public sealed record AudioTimelineRender(string Path, long ClippedSamples, double Peak, int OverlapFrames);

/** Segment entry and exit cues align clips while their authored lead-ins and tails overlap. */
public static partial class AudioTimeline
{
    /** Plans a playlist route joined by exit-cue transitions; the history advances only on success. */
    public static AudioTimelinePlan Build(AudioAnalysis analysis, uint playlistId, int loopIterations = 2, int variation = 0,
        AudioPlaylistHistory? history = null, uint startItem = 0)
    {
        if (loopIterations is < 1 or > 16 || variation < 0) throw new ArgumentOutOfRangeException(nameof(loopIterations));
        if (!analysis.Playlists.TryGetValue(playlistId, out var playlist)) throw new InvalidDataException("Choose an installed playlist.");
        if (playlist.Limits.Count > 0) throw new NotSupportedException(string.Join(", ", playlist.Limits));
        var notices = new HashSet<string>(StringComparer.Ordinal);
        history ??= new();
        var route = AudioPlaylistRouting.Select(analysis, playlist, loopIterations, variation, history, startItem, notices);
        var sequence = route.Segments;
        var scheduled = new List<Scheduled>();
        scheduled.Add(Place(analysis, sequence[0], 0));
        for (int i = 1; i < sequence.Count; i++)
        {
            var source = scheduled[^1];
            var rule = Match(analysis, playlist.Transitions, source.Segment.Id, sequence[i]);
            if (rule.SourceSync != 7)
                throw new NotSupportedException("Playlist audition needs an exit-cue transition; use Transition audition for a timed change.");
            Schedule(analysis, scheduled, rule, sequence[i], source.Origin + source.Segment.Exit);
        }
        var plan = MakePlan(analysis, playlistId, scheduled, notices) with { PlaylistPath = route.Path };
        history.Commit(route.Next, playlistId);
        return plan;
    }

    /** Renders from PCM16 sources; see the AudioSamples overload. */
    public static AudioTimelineRender Render(AudioTimelinePlan plan, IReadOnlyDictionary<uint, PcmWave> sources,
        string path, CancellationToken token = default) =>
        Render(plan, sources.ToDictionary(pair => pair.Key, pair => AudioSamples.FromPcm(pair.Value)), path, token);

    /** The engine's constant-power centre: a mono source reaches each stereo channel at sqrt(1/2). */
    public const float MonoToStereoGain = 0.70710677f;

    /** Float accumulation preserves overlaps; quantization reports clipping instead of normalizing it away. */
    public static AudioTimelineRender Render(AudioTimelinePlan plan, IReadOnlyDictionary<uint, AudioSamples> sources,
        string path, CancellationToken token = default)
    {
        const int rate = 48000, channels = 2;
        int frames = checked((int)Math.Round(plan.DurationSeconds * rate, MidpointRounding.AwayFromZero));
        if (frames <= 0 || frames > 1200 * rate) throw new InvalidDataException("Timeline duration is invalid.");
        var mixed = new float[checked(frames * channels)]; var active = new byte[frames];
        foreach (var clip in plan.Clips)
        {
            token.ThrowIfCancellationRequested();
            if (!sources.TryGetValue(clip.Media, out var source)) throw new InvalidDataException($"Missing media {clip.Media}.");
            if (source.Channels is < 1 or > 2 || source.SampleRate is < 8000 or > 192000 || source.Samples.Length % source.Channels != 0)
                throw new NotSupportedException("Timeline sources need valid mono or stereo audio.");
            if (!double.IsFinite(clip.SourceSeconds) || !double.IsFinite(clip.StartSeconds) || !double.IsFinite(clip.DurationSeconds)
                || clip.SourceSeconds < 0 || clip.StartSeconds < 0 || clip.DurationSeconds < 0 || !float.IsFinite(clip.GainDb))
                throw new InvalidDataException("Timeline clip bounds are invalid.");
            if (clip.LoopSeconds < 0 || !double.IsFinite(clip.LoopSeconds)
                || (clip.LoopSeconds > 0 && clip.LoopSeconds > source.DurationSeconds + 2.0 / source.SampleRate))
                throw new InvalidDataException("Music source loop bounds are invalid.");
            if (clip.LoopSeconds == 0 && clip.SourceSeconds + clip.DurationSeconds > source.DurationSeconds + 2.0 / source.SampleRate)
                throw new InvalidDataException("The decoded source is shorter than its authored music clip.");
            int start = checked((int)Math.Round(clip.StartSeconds * rate, MidpointRounding.AwayFromZero));
            int count = checked((int)Math.Round(clip.DurationSeconds * rate, MidpointRounding.AwayFromZero));
            if (start > frames || count > frames - start) throw new InvalidDataException("Timeline clip leaves its output.");
            int available = source.Samples.Length / source.Channels;
            int loopFrames = clip.LoopSeconds > 0 ? Math.Min(available, checked((int)Math.Round(clip.LoopSeconds * source.SampleRate))) : available;
            if (available == 0 || loopFrames == 0) throw new InvalidDataException("Music source contains no complete samples.");
            var automation = clip.Automation?.OrderByDescending(value => value.Type).Select(value => new AudioAutomationCurve(value, rate)).ToArray() ?? [];
            if (!double.IsFinite(clip.AutomationOffsetSeconds) || clip.AutomationOffsetSeconds is < 0 or > 1200)
                throw new InvalidDataException("Clip automation offset is invalid.");
            long automationOffset = checked((long)Math.Round(clip.AutomationOffsetSeconds * rate, MidpointRounding.AwayFromZero));
            // Panner on: left sqrt(1 - x), right sqrt(x), for mono and stereo alike. Off: mono centred, stereo as is.
            float left = 1, right = 1;
            if (clip.Pan is { } pan)
            {
                if (!float.IsFinite(pan) || pan is < 0 or > 1) throw new InvalidDataException("Clip pan is invalid.");
                left = MathF.Sqrt(1 - pan); right = MathF.Sqrt(pan);
            }
            else if (source.Channels == 1) left = right = MonoToStereoGain;
            double level = Math.Pow(10, clip.GainDb / 20.0);
            double[] gains = [level * left, level * right];
            if (!double.IsFinite(level)) throw new InvalidDataException("Audio gain exceeds the working range.");
            // The engine rounds the source offset and loop length to whole output samples.
            long sourceStart = checked((long)Math.Round(clip.SourceSeconds * rate, MidpointRounding.AwayFromZero));
            long loopLength = clip.LoopSeconds > 0 ? Math.Max(1, checked((long)Math.Round(clip.LoopSeconds * rate, MidpointRounding.AwayFromZero))) : 0;
            for (int frame = 0; frame < count; frame++)
            {
                if ((frame & 16383) == 0) token.ThrowIfCancellationRequested();
                long step = sourceStart + frame;
                if (loopLength > 0) step %= loopLength;
                double position = step * (double)source.SampleRate / rate;
                double time = (double)(start + frame) / rate;
                double envelope = (clip.FadeIn?.At(time) ?? 1) * (clip.FadeOut?.At(time) ?? 1);
                float clipGain = 1;
                foreach (var lane in automation) clipGain *= lane.GainAt(automationOffset + frame);
                envelope *= Math.Max(0, clipGain);
                int a = (int)Math.Floor(position); double fraction = position - a;
                if (a < 0 || a >= available) continue;
                int b = a + 1 < loopFrames ? a + 1 : clip.LoopSeconds > 0 ? 0 : available - 1;
                active[start + frame] = (byte)Math.Min(255, active[start + frame] + 1);
                for (int channel = 0; channel < channels; channel++)
                {
                    int inputChannel = Math.Min(channel, source.Channels - 1);
                    float sampleA = source.Samples[a * source.Channels + inputChannel];
                    float sampleB = source.Samples[b * source.Channels + inputChannel];
                    mixed[(start + frame) * channels + channel] += (float)((sampleA + (sampleB - sampleA) * fraction) * gains[channel] * envelope);
                }
            }
        }
        var bytes = new byte[checked(mixed.Length * 2)]; long clipped = 0; double peak = 0;
        for (int i = 0; i < mixed.Length; i++)
        {
            if ((i & 32767) == 0) token.ThrowIfCancellationRequested();
            if (!float.IsFinite(mixed[i])) throw new InvalidDataException("Audio mix exceeds the working range.");
            peak = Math.Max(peak, Math.Abs(mixed[i]) / 32768.0);
            if (mixed[i] < short.MinValue || mixed[i] > short.MaxValue) clipped++;
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)Math.Clamp((int)Math.Round(mixed[i]), short.MinValue, short.MaxValue));
        }
        token.ThrowIfCancellationRequested(); PcmWave.Write(path, rate, channels, bytes);
        return new(path, clipped, peak, active.Count(count => count > 1));
    }
}
