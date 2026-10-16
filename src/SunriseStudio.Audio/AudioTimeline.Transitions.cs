/** File: Partial AudioTimeline: planning and rendering of transitions between music segments. */
namespace SunriseStudio.Audio;

/** A fade window: start, duration, curve and direction. */
public sealed record AudioEnvelope(double StartSeconds, double DurationSeconds, int Curve, bool FadeIn)
{
    /** Gain at a time: 0 or 1 outside the window, the curve inside. */
    public double At(double time)
    {
        if (DurationSeconds <= 0 || !double.IsFinite(DurationSeconds) || !double.IsFinite(StartSeconds))
            throw new InvalidDataException("Audio envelope bounds are invalid.");
        if (time < StartSeconds) return FadeIn ? 0 : 1;
        if (time >= StartSeconds + DurationSeconds) return FadeIn ? 1 : 0;
        double x = Math.Clamp((time - StartSeconds) / DurationSeconds, 0, 1);
        return AudioCurves.Interpolate(Curve, x, FadeIn);
    }
}

/** Builds timelines (segment layout, clips, fades) from a playlist or a transition. */
public static partial class AudioTimeline
{
    private sealed class Scheduled(AudioSegment segment, double origin)
    {
        public AudioSegment Segment { get; } = segment;
        public double Origin { get; } = origin;
        public double First { get; set; }
        public double Last { get; set; } = segment.Duration;
        public AudioEnvelope? FadeIn { get; set; }
        public AudioEnvelope? FadeOut { get; set; }
    }

    /** The requested time is measured from the source entry cue, not from the file's leading audio. */
    public static AudioTimelinePlan BuildTransition(AudioAnalysis analysis, uint container, uint sourceSegment,
        uint destinationSegment, double requestSeconds, int destinationVariation = 0)
        => BuildTransitionCore(analysis, container, sourceSegment, destinationSegment, requestSeconds, destinationVariation);

    /** A playlist destination resolves its authored jump before publishing any step-history change. */
    public static AudioTimelinePlan BuildPlaylistTransition(AudioAnalysis analysis, uint container, uint sourceSegment,
        uint destinationPlaylist, double requestSeconds, int loopIterations = 2, int variation = 0,
        AudioPlaylistHistory? history = null, int destinationVariation = 0)
    {
        if (loopIterations is < 1 or > 16 || variation < 0) throw new ArgumentOutOfRangeException(nameof(loopIterations));
        if (!analysis.Playlists.TryGetValue(destinationPlaylist, out var playlist)
            || !analysis.TransitionContainers.TryGetValue(container, out var owner))
            throw new InvalidDataException("Choose a loaded transition container and destination playlist.");
        if (playlist.Limits.Count > 0) throw new NotSupportedException(string.Join(", ", playlist.Limits));
        history ??= new(); uint start = 0;
        for (int attempt = 0; attempt < 64; attempt++)
        {
            var notices = new HashSet<string>(StringComparer.Ordinal);
            var route = AudioPlaylistRouting.Select(analysis, playlist, loopIterations, variation, history, start, notices);
            var rule = Match(analysis, owner.Rules, sourceSegment, route.Segments[0]);
            if (rule.JumpTo != 0 && rule.JumpTo != start) { start = rule.JumpTo; continue; }
            var plan = BuildTransitionCore(analysis, container, sourceSegment, route.Segments[0], requestSeconds,
                destinationVariation, playlist, route.Segments, notices) with { PlaylistPath = route.Path };
            history.Commit(route.Next, playlist.Id); return plan;
        }
        throw new NotSupportedException("Playlist destination rules do not settle on one jump target.");
    }

    /** Plans one transition: resolves the sync point, then places the source, bridge and destination. */
    private static AudioTimelinePlan BuildTransitionCore(AudioAnalysis analysis, uint container, uint sourceSegment,
        uint destinationSegment, double requestSeconds, int destinationVariation,
        AudioPlaylist? playlist = null, IReadOnlyList<uint>? continuation = null, IReadOnlySet<string>? routeNotices = null)
    {
        if (!double.IsFinite(requestSeconds) || requestSeconds < 0 || requestSeconds > 1200)
            throw new ArgumentOutOfRangeException(nameof(requestSeconds));
        ArgumentOutOfRangeException.ThrowIfNegative(destinationVariation);
        if (!analysis.TransitionContainers.TryGetValue(container, out var owner))
            throw new InvalidDataException("Choose a music transition container.");
        if ((sourceSegment != 0 && !Ancestors(analysis, sourceSegment).Contains(container))
            || (destinationSegment != 0 && !Ancestors(analysis, destinationSegment).Contains(container))
            || sourceSegment == 0 && destinationSegment == 0)
            throw new InvalidDataException("Both segments must belong to the selected transition container.");
        var rule = Match(analysis, owner.Rules, sourceSegment, destinationSegment);
        var notices = new HashSet<string> { "Transition audition uses the selected source and destination segments; it does not simulate event-driven playlist selection." };
        if (routeNotices is not null) notices.UnionWith(routeNotices);
        if (sourceSegment == 0)
        {
            if (rule.BridgeSegment != 0 || rule.DestinationEntry != 0)
                throw new NotSupportedException("Choose a source segment to audition a bridge or alternate destination entry.");
            if (playlist is null) ValidateJump(analysis, rule, destinationSegment);
            var initial = Place(analysis, destinationSegment, requestSeconds * 1000);
            var entry = AudioDestination.Resolve(initial.Segment, rule.DestinationEntry, cueFilter: rule.DestinationCue,
                matchSourceCue: rule.MatchCue, variation: destinationVariation);
            Start(initial, initial.Origin + initial.Segment.Entry + entry.SecondsFromEntry * 1000,
                rule.FadeInMs, rule.FadeInCurve, rule.FadeInOffsetMs, rule.PreEntry && entry.AllowsPreEntry);
            DestinationNotice(entry, notices);
            var initialRows = new List<Scheduled> { initial };
            ContinuePlaylist(analysis, initialRows, playlist, continuation, destinationVariation);
            return MakePlan(analysis, container, initialRows, notices) with { Destination = entry };
        }
        if (playlist is null) ValidateJump(analysis, rule, destinationSegment);
        var first = Place(analysis, sourceSegment, 0);
        double earliest = requestSeconds;
        bool strict = rule.DestinationEntry != 1 && !(rule.MatchCue && rule.DestinationEntry is 2 or 3);
        var point = AudioSynchronization.Resolve(analysis, sourceSegment, rule.SourceSync, earliest, rule.SourceCue, strict);
        AudioDestinationPoint? destinationPoint;
        for (int attempt = 0; ; attempt++)
        {
            if (attempt == 64) throw new NotSupportedException("Destination preparation did not reach a stable synchronization point.");
            destinationPoint = destinationSegment == 0 ? null : AudioDestination.Resolve(analysis.Segments[destinationSegment],
                rule.DestinationEntry, point.FrameFromEntry, point.Cue, rule.DestinationCue, rule.MatchCue, destinationVariation);
            double lead = RequiredLead(analysis, rule, destinationSegment, destinationPoint);
            double minimum = requestSeconds + lead / 1000;
            if (point.SecondsFromEntry + 0.5 / AudioSynchronization.SampleRate >= minimum) break;
            if (rule.SourceSync == 7) throw new NotSupportedException("The remaining source is too short for the authored transition lead-in.");
            earliest = Math.Max(earliest, minimum);
            point = AudioSynchronization.Resolve(analysis, sourceSegment, rule.SourceSync, earliest, rule.SourceCue, strict);
        }
        double sync = (AudioSynchronization.MillisecondsToFrames(first.Segment.Entry) + point.FrameFromEntry)
            * (1000.0 / AudioSynchronization.SampleRate);
        var scheduled = new List<Scheduled> { first };
        if (destinationSegment == 0)
        {
            if (rule.BridgeSegment != 0)
                throw new NotSupportedException("This stop transition needs a bridge.");
            Stop(first, sync, rule.FadeOutMs, rule.FadeOutCurve, rule.FadeOutOffsetMs, rule.PostExit);
            return MakePlan(analysis, container, scheduled, notices) with { Synchronization = point };
        }
        Schedule(analysis, scheduled, rule, destinationSegment, sync, destinationPoint, playlist is not null);
        ContinuePlaylist(analysis, scheduled, playlist, continuation, destinationVariation);
        DestinationNotice(destinationPoint!, notices);
        return MakePlan(analysis, container, scheduled, notices) with { Synchronization = point, Destination = destinationPoint };
    }

    /** Segments that sit under a transition container. */
    public static IReadOnlyList<AudioSegment> TransitionSegments(AudioAnalysis analysis, uint container) =>
        analysis.Segments.Values.Where(segment => Ancestors(analysis, segment.Id).Contains(container)).ToArray();

    /** Playlists that sit under a transition container. */
    public static IReadOnlyList<AudioPlaylist> TransitionPlaylists(AudioAnalysis analysis, uint container) =>
        analysis.Playlists.Values.Where(playlist => Ancestors(analysis, playlist.Id).Contains(container)).ToArray();

    private static HashSet<uint> Ancestors(AudioAnalysis analysis, uint id)
    {
        var result = new HashSet<uint>();
        if (id == 0) { result.Add(0); return result; }
        while (id != 0)
        {
            if (!result.Add(id)) throw new InvalidDataException("Music parent routing contains a cycle.");
            if (!analysis.Routes.TryGetValue(id, out var route)) break;
            id = route.Parent;
        }
        return result;
    }

    /** The bottom-most matching rule wins, including rules that name an ancestor container. */
    private static AudioTransition Match(AudioAnalysis analysis, IReadOnlyList<AudioTransition> rules, uint source, uint destination)
    {
        var sources = Ancestors(analysis, source); var destinations = Ancestors(analysis, destination);
        return rules.LastOrDefault(rule => (rule.Source == uint.MaxValue || sources.Contains(rule.Source))
            && (rule.Destination == uint.MaxValue || destinations.Contains(rule.Destination)))
            ?? throw new NotSupportedException("The container has no matching authored transition.");
    }

    private static Scheduled Place(AudioAnalysis analysis, uint id, double origin)
    {
        if (!analysis.Segments.TryGetValue(id, out var segment))
            throw new InvalidDataException("Music segment is missing from the loaded banks.");
        if (segment.Limits.Count > 0) throw new NotSupportedException(string.Join(", ", segment.Limits));
        return new(segment, origin);
    }

    /** Stops the source at sync, then starts the bridge, if any, and the destination at its entry. */
    private static void Schedule(AudioAnalysis analysis, List<Scheduled> rows, AudioTransition rule, uint destination, double sync,
        AudioDestinationPoint? destinationPoint = null, bool resolvedJump = false)
    {
        if (!resolvedJump) ValidateJump(analysis, rule, destination);
        if (destinationPoint is null && rule.DestinationEntry != 0)
            throw new NotSupportedException("Use Transition audition to select this destination entry.");
        var source = rows[^1];
        Stop(source, sync, rule.FadeOutMs, rule.FadeOutCurve, rule.FadeOutOffsetMs, rule.PostExit);
        if (rule.BridgeSegment != 0)
        {
            var bridge = rule.Bridge ?? throw new InvalidDataException("The transition segment has no authored fade settings.");
            var middle = Place(analysis, bridge.Segment, 0);
            middle = Place(analysis, bridge.Segment, sync - middle.Segment.Entry);
            Start(middle, sync, bridge.FadeInMs, bridge.FadeInCurve, bridge.FadeInOffsetMs, bridge.PreEntry);
            double bridgeExit = middle.Origin + middle.Segment.Exit;
            Stop(middle, bridgeExit, bridge.FadeOutMs, bridge.FadeOutCurve, bridge.FadeOutOffsetMs, bridge.PostExit);
            rows.Add(middle); sync = bridgeExit;
        }
        var next = Place(analysis, destination, 0);
        destinationPoint ??= AudioDestination.Resolve(next.Segment, rule.DestinationEntry);
        next = Place(analysis, destination, sync - next.Segment.Entry - destinationPoint.SecondsFromEntry * 1000);
        Start(next, sync, rule.FadeInMs, rule.FadeInCurve, rule.FadeInOffsetMs, rule.PreEntry && destinationPoint.AllowsPreEntry);
        rows.Add(next);
    }

    /** Appends the rest of the destination playlist, joined by exit-cue transitions. */
    private static void ContinuePlaylist(AudioAnalysis analysis, List<Scheduled> rows, AudioPlaylist? playlist,
        IReadOnlyList<uint>? continuation, int destinationVariation)
    {
        if (playlist is null || continuation is null) return;
        for (int index = 1; index < continuation.Count; index++)
        {
            var previous = rows[^1]; uint destination = continuation[index];
            var rule = Match(analysis, playlist.Transitions, previous.Segment.Id, destination);
            if (rule.SourceSync != 7)
                throw new NotSupportedException("The destination playlist needs an external transition before its next segment.");
            var sourcePoint = AudioSynchronization.Resolve(analysis, previous.Segment.Id, 7, 0);
            var entry = AudioDestination.Resolve(analysis.Segments[destination], rule.DestinationEntry,
                sourcePoint.FrameFromEntry, sourcePoint.Cue, rule.DestinationCue, rule.MatchCue, destinationVariation);
            Schedule(analysis, rows, rule, destination, previous.Origin + previous.Segment.Exit, entry);
        }
    }

    private static void Start(Scheduled row, double entry, double duration, int curve, double offset, bool preEntry)
    {
        ValidateFade(duration, offset, curve);
        double selected = entry - row.Origin;
        row.First = duration > 0 ? Math.Max(preEntry ? 0 : row.Segment.Entry, selected + offset)
            : preEntry ? 0 : selected;
        if (duration > 0)
        {
            row.FadeIn = new((entry + offset) / 1000, duration / 1000, curve, true);
        }
    }

    /** Only explicit leaf jumps are admitted; container jumps need playlist state. */
    private static void ValidateJump(AudioAnalysis analysis, AudioTransition rule, uint destination)
    {
        if (rule.JumpTo == 0) return;
        var matches = new List<AudioPlaylistItem>();
        void Visit(AudioPlaylistItem item)
        {
            if (item.Id == rule.JumpTo) matches.Add(item);
            foreach (var child in item.Children) Visit(child);
        }
        foreach (uint owner in Ancestors(analysis, destination))
            if (analysis.Playlists.TryGetValue(owner, out var playlist)) Visit(playlist.Root);
        if (matches.Count != 1 || matches[0].Segment == 0 || matches[0].Children.Count != 0)
            throw new NotSupportedException("This playlist jump is missing, ambiguous or names a container.");
        if (matches[0].Segment != destination)
            throw new NotSupportedException($"The authored jump requires destination segment {matches[0].Segment}.");
    }

    /** Authored lead-ins and fades must fit after the request, without assuming streaming latency. */
    private static double RequiredLead(AudioAnalysis analysis, AudioTransition rule, uint destination, AudioDestinationPoint? point)
    {
        double lead = Math.Max(0, rule.FadeOutMs - rule.FadeOutOffsetMs);
        if (destination == 0) return lead;
        double interval = 0;
        if (rule.Bridge is { } bridge)
        {
            var segment = analysis.Segments[bridge.Segment]; interval = segment.Exit - segment.Entry;
            lead = Math.Max(lead, EntryLead(segment, 0, true, bridge.FadeInMs, bridge.FadeInOffsetMs, bridge.PreEntry));
            lead = Math.Max(lead, bridge.FadeOutMs - bridge.FadeOutOffsetMs - interval);
        }
        var target = analysis.Segments[destination];
        return Math.Max(lead, EntryLead(target, point!.SecondsFromEntry * 1000, point.AllowsPreEntry,
            rule.FadeInMs, rule.FadeInOffsetMs, rule.PreEntry) - interval);
    }

    private static double EntryLead(AudioSegment segment, double cursor, bool allowsPreEntry, double fade, double offset, bool preEntry)
    {
        bool allow = preEntry && allowsPreEntry;
        double first = fade > 0 ? cursor + offset : allow ? -segment.Entry : cursor;
        if (!allow) first = Math.Max(0, first);
        return Math.Max(0, cursor - first);
    }

    private static void DestinationNotice(AudioDestinationPoint point, HashSet<string> notices)
    {
        if (point.Kind is 2 or 3)
            notices.Add("Destination cue: repeatable pick, not the game seed");
    }

    private static void Stop(Scheduled row, double sync, double duration, int curve, double offset, bool postExit)
    {
        ValidateFade(duration, offset, curve);
        double localSync = sync - row.Origin;
        double end = duration > 0 ? localSync + offset
            : postExit && Math.Abs(localSync - row.Segment.Exit) < .01 ? row.Segment.Duration : localSync;
        row.Last = Math.Min(postExit ? row.Segment.Duration : row.Segment.Exit, end);
        if (duration > 0) row.FadeOut = new((sync + offset - duration) / 1000, duration / 1000, curve, false);
    }

    private static void ValidateFade(double duration, double offset, int curve)
    {
        if (!double.IsFinite(duration) || !double.IsFinite(offset) || duration < 0 || duration > 1200000 || Math.Abs(offset) > 1200000)
            throw new InvalidDataException("Music fade bounds are invalid.");
        if (duration == 0 && offset != 0)
            throw new NotSupportedException("An offset without an authored fade is not supported.");
        if (duration > 0) _ = AudioCurves.Interpolate(curve, .5, true);
    }

    /** Turns scheduled segments into clips trimmed to each segment's playing range. */
    private static AudioTimelinePlan MakePlan(AudioAnalysis analysis, uint owner, IReadOnlyList<Scheduled> rows, HashSet<string> notices)
    {
        var placements = new List<AudioTimelineSegment>(); var clips = new List<AudioTimelineClip>();
        foreach (var row in rows)
        {
            var segment = row.Segment;
            if (row.Origin + row.First < 0 || row.FadeOut?.StartSeconds < 0 || row.FadeIn?.StartSeconds < 0)
                throw new NotSupportedException("The requested transition needs more lead-in time for its pre-entry or fade.");
            if (row.Last < row.First) throw new InvalidDataException("Transition timing leaves no playable segment range.");
            placements.Add(new(segment.Id, segment.Name, (row.Origin + row.First) / 1000,
                (row.Origin + segment.Entry) / 1000, (row.Origin + segment.Exit) / 1000, (row.Origin + row.Last) / 1000));
            foreach (uint trackId in segment.Tracks)
            {
                if (!analysis.Tracks.TryGetValue(trackId, out var track)) throw new InvalidDataException("Segment track is missing.");
                if (track.Limits.Count > 0) throw new NotSupportedException(string.Join(", ", track.Limits));
                float gain = Gain(analysis, track.Id, notices);
                if (track.Spatial) notices.Add("3D positioning is not applied; the sound plays centred.");
                for (int clipIndex = 0; clipIndex < track.Clips.Count; clipIndex++)
                {
                    var clip = track.Clips[clipIndex];
                    if (clip.SourceDuration <= 0 || clip.BeginTrim < 0 || clip.BeginTrim >= clip.SourceDuration)
                        throw new NotSupportedException("This music clip has an unsupported source trim.");
                    double clipStart = clip.PlayAt + clip.BeginTrim, clipEnd = clip.PlayAt + clip.SourceDuration + clip.EndTrim;
                    if (clipEnd < clipStart) throw new InvalidDataException("Music clip trim is invalid.");
                    double start = Math.Max(row.First, clipStart), end = Math.Min(row.Last, clipEnd);
                    if (end <= start) continue;
                    clips.Add(new(segment.Id, trackId, clip.SourceId, (row.Origin + start) / 1000,
                        (start - clip.PlayAt) / 1000, (end - start) / 1000, gain, row.FadeIn, row.FadeOut,
                        clip.EndTrim > 0 ? clip.SourceDuration / 1000 : 0,
                        track.Automation?.Where(value => value.ClipIndex == clipIndex).ToArray(), (start - clipStart) / 1000, track.Pan));
                }
            }
        }
        double duration = placements.Max(value => value.EndSeconds);
        if (!double.IsFinite(duration) || duration is <= 0 or > 1200)
            throw new InvalidDataException("Timeline exceeds the twenty-minute audition limit.");
        return new(owner, placements, clips, duration, notices.Order().ToArray());
    }

    /** Sums route volumes up the parent chain, then down the first output bus. */
    private static float Gain(AudioAnalysis analysis, uint id, HashSet<string> notices)
    {
        float value = 0; uint bus = 0; var visited = new HashSet<uint>();
        while (id != 0)
        {
            if (!visited.Add(id)) throw new InvalidDataException("Audio parent routing contains a cycle.");
            if (!analysis.Routes.TryGetValue(id, out var route)) { notices.Add("A parent audio node is outside the loaded banks."); break; }
            value += route.VolumeDb;
            foreach (string limit in route.Limits) notices.Add("Dry audition omits: " + limit.ToLowerInvariant());
            if (bus == 0) bus = route.OutputBus;
            id = route.Parent;
        }
        while (bus != 0)
        {
            if (!visited.Add(bus)) throw new InvalidDataException("Audio bus routing contains a cycle.");
            if (!analysis.Routes.TryGetValue(bus, out var route)) { notices.Add("An output bus is outside the loaded banks."); break; }
            value += route.VolumeDb;
            foreach (string limit in route.Limits) notices.Add("Dry audition omits: bus " + limit.ToLowerInvariant());
            bus = route.OutputBus;
        }
        return value;
    }
}
