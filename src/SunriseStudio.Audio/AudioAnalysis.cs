/**
 * File: Records for the analysed Wwise music graph (segments, playlists, transitions, routes, choices) that
 * the timeline and composer consume.
 */
namespace SunriseStudio.Audio;

/** One option of a switch or state group and the node it selects. */
public sealed record AudioChoice(uint Value, string Name, uint Target);
/** A switch or state group with its named choices. */
public sealed record AudioChoiceGroup(uint Id, bool Switch, string Name, IReadOnlyList<AudioChoice> Choices);
/** A game parameter (RTPC) with its range. */
public sealed record AudioParameter(uint Id, string Name, float Minimum, float Maximum);
/** Bus routing of one node: parent, output bus and volume, plus limits that stopped analysis. */
public sealed record AudioRoute(uint Id, uint Parent, uint OutputBus, float VolumeDb, bool Bus,
    IReadOnlyList<string> Limits);
/** One clip on a music track: source, lane, placement and trims in seconds. */
public sealed record AudioClip(uint SourceId, uint Lane, double PlayAt, double BeginTrim, double EndTrim, double SourceDuration);
/**
 * A music track.
 * @param Pan 2D panner position 0..1 from the track's positioning owner when the panner is on; null when off.
 * @param Spatial The positioning owner is 3D, which a preview plays centred.
 */
public sealed record AudioTrack(uint Id, string Name, uint Parent, float VolumeDb, int Type,
    IReadOnlyList<AudioClip> Clips, IReadOnlyList<string> Limits, IReadOnlyList<AudioClipAutomation>? Automation = null,
    float? Pan = null, bool Spatial = false);
/** A music segment: timing, entry and exit points, its tracks and analysis limits. */
public sealed record AudioSegment(uint Id, string Name, uint Parent, float VolumeDb, double Duration,
    double Entry, double Exit, IReadOnlyList<uint> Tracks, IReadOnlyList<string> Limits,
    IReadOnlyList<AudioCue>? Cues = null);
/** One node of a playlist tree (segment or container) with loop, weight and shuffle settings. */
public sealed record AudioPlaylistItem(uint Id, uint Segment, int Kind, int Loops,
    uint Weight, int AvoidRepeat, bool Shuffle, IReadOnlyList<AudioPlaylistItem> Children,
    int LoopMinimum = 0, int LoopMaximum = 0, bool UseWeights = false);
/** One transition rule: source and destination sync, entry point and fade curves. */
public sealed record AudioTransition(uint Source, uint Destination, int SourceSync, int DestinationEntry,
    double FadeOutMs, double FadeInMs, int FadeOutCurve, int FadeInCurve,
    double FadeOutOffsetMs, double FadeInOffsetMs, bool PostExit, bool PreEntry, uint BridgeSegment, uint JumpTo,
    AudioBridge? Bridge = null, uint SourceCue = 0, uint DestinationCue = 0, bool MatchCue = false);
/** A bridge segment played between two segments, with its fades. */
public sealed record AudioBridge(uint Segment, double FadeInMs, int FadeInCurve, double FadeInOffsetMs,
    double FadeOutMs, int FadeOutCurve, double FadeOutOffsetMs, bool PreEntry, bool PostExit);
/** A music switch or playlist container and the transition rules it owns. */
public sealed record AudioTransitionContainer(uint Id, string Name, IReadOnlyList<AudioTransition> Rules);
/** A music playlist: its item tree and transitions. */
public sealed record AudioPlaylist(uint Id, string Name, uint Parent, float VolumeDb,
    AudioPlaylistItem Root, IReadOnlyList<AudioTransition> Transitions, IReadOnlyList<string> Limits);

/** Music routing read from version-113 bank objects; a music object with invalid timing is skipped with a diagnostic. */
public sealed record AudioAnalysis
{
    public IReadOnlyDictionary<uint, AudioTrack> Tracks { get; init; } = new Dictionary<uint, AudioTrack>();
    public IReadOnlyDictionary<uint, AudioSegment> Segments { get; init; } = new Dictionary<uint, AudioSegment>();
    public IReadOnlyDictionary<uint, AudioPlaylist> Playlists { get; init; } = new Dictionary<uint, AudioPlaylist>();
    public IReadOnlyDictionary<uint, AudioTransitionContainer> TransitionContainers { get; init; } = new Dictionary<uint, AudioTransitionContainer>();
    public IReadOnlyList<AudioChoiceGroup> GameSyncs { get; init; } = [];
    public IReadOnlyList<AudioParameter> Parameters { get; init; } = [];
    public IReadOnlyDictionary<uint, AudioRoute> Routes { get; init; } = new Dictionary<uint, AudioRoute>();
    public IReadOnlyDictionary<uint, AudioMeter> Meters { get; init; } = new Dictionary<uint, AudioMeter>();
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    /** Banks are read in order and the first object with an ID wins. */
    public static AudioAnalysis Read(IReadOnlyList<AudioBank> banks) => Read(WwiseHierarchy.Read(banks));

    /** Builds tracks, segments, playlists, routes, meters and game syncs from the parsed objects. */
    public static AudioAnalysis Read(WwiseHierarchy hierarchy)
    {
        var tracks = new Dictionary<uint, AudioTrack>(); var segments = new Dictionary<uint, AudioSegment>();
        var playlists = new Dictionary<uint, AudioPlaylist>(); var groups = new Dictionary<(uint, bool), List<AudioChoice>>();
        var parameters = new Dictionary<uint, (float Min, float Max)>(); var diagnostics = hierarchy.Diagnostics.ToList();
        var routes = new Dictionary<uint, AudioRoute>();
        var transitions = new Dictionary<uint, AudioTransitionContainer>();
        var meters = new Dictionary<uint, AudioMeter>();
        foreach (var node in hierarchy.Banks.SelectMany(bank => bank.Objects))
        {
            try { ReadObject(node); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException)
            { diagnostics.Add($"Music object {node.Id} skipped: {error.Message}"); }
        }
        return new() { Tracks = tracks, Segments = segments, Playlists = playlists,
            GameSyncs = groups.OrderBy(value => value.Key.Item1).Select(value => new AudioChoiceGroup(value.Key.Item1,
                value.Key.Item2, (value.Key.Item2 ? "Switch " : "State ") + value.Key.Item1,
                value.Value.DistinctBy(choice => choice.Value).OrderBy(choice => choice.Value).ToArray())).ToArray(),
            Parameters = parameters.Select(pair => new AudioParameter(pair.Key, $"Parameter {pair.Key}", pair.Value.Min, pair.Value.Max)).ToArray(), Routes = routes,
            Diagnostics = diagnostics, TransitionContainers = transitions, Meters = meters };

        /** Every check runs before the first insert, so a skipped object leaves no partial rows. */
        void ReadObject(WwiseObject node)
        {
            uint id = node.Id; string label = Label(node);
            var basis = WwiseHierarchy.NodeOf(node); var music = Music(node);
            var limits = Limits(node);
            float volume = basis is null ? 0 : Volume(basis.Props, WwiseHierarchy.VolumeProp);
            AudioMeter? meter = null;
            if (music is { MeterFlag: not 0 })
            {
                meter = new(Finite(music.Meter.Tempo), music.Meter.BeatsPerBar, music.Meter.BeatValue,
                    Finite(music.Meter.GridPeriod), Finite(music.Meter.GridOffset));
                _ = meter.Frames();
            }
            var rtpcs = Rtpcs(node).Where(rtpc => rtpc.Id != 0 && rtpc.Points.Count > 0)
                .Select(rtpc => (rtpc.Id, Points: rtpc.Points.Select(point => Finite(point.From)).ToArray())).ToArray();
            AudioTrack? track = null; AudioSegment? segment = null; AudioPlaylist? playlist = null;
            IReadOnlyList<AudioTransition>? rules = null; var choices = new List<(uint Group, bool Switch, AudioChoice Choice)>();
            if (node is WwiseMusicSwitch or WwiseMusicPlaylist)
                rules = ReadTransitions(node is WwiseMusicSwitch value ? value.Rules : ((WwiseMusicPlaylist)node).Rules);
            if (node is WwiseMusicTrack musicTrack)
            {
                var clips = musicTrack.Clips.Select(clip => new AudioClip(clip.Source, clip.Track, Finite(clip.PlayAt),
                    Finite(clip.BeginTrim), Finite(clip.EndTrim), Finite(clip.SourceDuration))).ToArray();
                var automation = musicTrack.Automation.Select(lane => new AudioClipAutomation(lane.Clip, (int)lane.Type,
                    lane.Points.Select(point => new AudioAutomationPoint(Finite(point.From), Finite(point.To), (int)point.Curve)).ToArray())).ToArray();
                if (automation.Any(value => value.ClipIndex >= clips.Length))
                    throw new InvalidDataException("Music clip automation count or clip index is invalid.");
                if (automation.GroupBy(value => (value.ClipIndex, value.Type)).Any(group => group.Count() > 1))
                    throw new InvalidDataException("Music clip has duplicate automation lanes.");
                var trackLimits = limits.ToList();
                foreach (var lane in automation)
                {
                    if (lane.Type is 0 or 3 or 4) _ = new AudioAutomationCurve(lane);
                    else trackLimits.Add($"Clip automation type {lane.Type}");
                }
                if (musicTrack.TrackType != 0) trackLimits.Add("Random or switch track");
                var (pan, spatial, _) = hierarchy.Positioning(id);
                track = new(id, label, basis!.Parent, volume, musicTrack.TrackType, clips, trackLimits, automation, pan, spatial);
            }
            else if (node is WwiseMusicSegment musicSegment)
            {
                var markers = musicSegment.Markers.Select(marker => new AudioCue(marker.Id, Finite(marker.Position), marker.Name)).ToArray();
                double duration = Finite(musicSegment.Duration);
                if (markers.Length < 2 || markers[0].PositionMs < 0 || markers[^1].PositionMs > duration + .01
                    || markers.Zip(markers.Skip(1)).Any(pair => pair.First.PositionMs > pair.Second.PositionMs))
                    throw new InvalidDataException("Music segment markers are invalid.");
                static double Clock(double ms) => AudioSynchronization.MillisecondsToFrames(ms)
                    * (1000.0 / AudioSynchronization.SampleRate);
                segment = new(id, label, basis!.Parent, volume, Clock(duration), Clock(markers[0].PositionMs),
                    Clock(markers[^1].PositionMs), musicSegment.Music.Children, limits, Array.AsReadOnly(markers));
            }
            else if (node is WwiseMusicPlaylist { Root: { } root })
                playlist = new(id, label, basis!.Parent, volume, ReadItem(root, 0), rules!, limits);
            if (node is WwiseMusicSwitch musicSwitch)
            {
                void ReadNode(WwiseDecisionNode branch, int depth)
                {
                    if (depth >= musicSwitch.Arguments.Count) return;
                    var (group, type) = musicSwitch.Arguments[depth];
                    foreach (var child in branch.Children)
                    {
                        choices.Add((group, type == 0, new(child.Key, child.Key == 0 ? "Default" : $"Value {child.Key}", child.AudioNode)));
                        ReadNode(child, depth + 1);
                    }
                }
                foreach (var branch in musicSwitch.Tree) ReadNode(branch, 0);
            }

            if (meter is not null) meters.TryAdd(id, meter);
            if (basis is not null || node is WwiseBus)
            {
                var bus = node as WwiseBus;
                routes.TryAdd(id, new(id, basis?.Parent ?? 0, bus?.OutputBus ?? basis!.OutputBus,
                    bus is null ? volume : Volume(bus.Props, WwiseHierarchy.BusVolumeProp), bus is not null, limits.ToArray()));
            }
            if (rules is not null) transitions.TryAdd(id, new(id, label, rules));
            if (track is not null) tracks.TryAdd(id, track);
            if (segment is not null) segments.TryAdd(id, segment);
            if (playlist is not null) playlists.TryAdd(id, playlist);
            foreach (var (group, isSwitch, choice) in choices)
            {
                if (!groups.TryGetValue((group, isSwitch), out var values)) groups[(group, isSwitch)] = values = [];
                values.Add(choice);
            }
            foreach (var (key, points) in rtpcs)
            {
                var prior = parameters.GetValueOrDefault(key, (Min: points.Min(), Max: points.Max()));
                parameters[key] = (Math.Min(prior.Min, points.Min()), Math.Max(prior.Max, points.Max()));
            }
        }
    }

    /** Nested playlist rows remain choices; random branches are not silently flattened. */
    private static AudioPlaylistItem ReadItem(WwiseMusicItem item, int depth)
    {
        if (depth > 64) throw new InvalidDataException("Music playlist is too deeply nested.");
        return new(item.Id, item.Segment, item.Kind, item.Loop, item.Weight, item.AvoidRepeat, item.Shuffle,
            item.Children.Select(child => ReadItem(child, depth + 1)).ToArray(), item.LoopMinimum, item.LoopMaximum,
            item.UsesWeights);
    }

    /** One transition per source and destination pair of each rule, in rule order. */
    private static IReadOnlyList<AudioTransition> ReadTransitions(IReadOnlyList<WwiseTransitionRule> rules)
    {
        var result = new List<AudioTransition>();
        foreach (var rule in rules)
        {
            var source = rule.Source; var destination = rule.Destination; var bridge = rule.Bridge;
            AudioBridge? bridgeRule = bridge is null ? null : new(bridge.Segment, bridge.FadeIn.Time, (int)bridge.FadeIn.Curve,
                bridge.FadeIn.Offset, bridge.FadeOut.Time, (int)bridge.FadeOut.Curve, bridge.FadeOut.Offset,
                bridge.PreEntry, bridge.PostExit);
            foreach (uint src in rule.Sources)
            foreach (uint dst in rule.Destinations)
                result.Add(new(src, dst, (int)source.Sync, destination.Entry,
                    source.Fade.Time, destination.Fade.Time, (int)source.Fade.Curve, (int)destination.Fade.Curve,
                    source.Fade.Offset, destination.Fade.Offset, source.PostExit, destination.PreEntry,
                    bridge?.Segment ?? 0, destination.JumpTo, bridgeRule, source.CueFilter, destination.CueFilter,
                    destination.MatchSourceCue));
        }
        return result;
    }

    private static WwiseMusicNode? Music(WwiseObject node) => node switch
    {
        WwiseMusicSegment value => value.Music, WwiseMusicSwitch value => value.Music, WwiseMusicPlaylist value => value.Music,
        _ => null
    };

    private static IEnumerable<WwiseRtpc> Rtpcs(WwiseObject node) => node switch
    {
        WwiseBus bus => bus.Rtpcs,
        WwiseOther other => other.Rtpcs,
        WwiseLayerContainer layer => layer.Node.Rtpcs.Concat(layer.Layers.SelectMany(value => value.Rtpcs)),
        _ => WwiseHierarchy.NodeOf(node)?.Rtpcs ?? []
    };

    /**
     * Effects, curves, states, pitch and filters that a dry preview leaves out.
     * Panning is applied from the positioning owner; front-rear pan and LFE do not reach a stereo mix.
     */
    private static List<string> Limits(WwiseObject node)
    {
        var result = new List<string>();
        (int Effects, int Rtpcs, int States, IReadOnlyList<WwiseProp> Props)? source = node switch
        {
            WwiseBus bus => (bus.EffectCount, bus.Rtpcs.Count, bus.States.Count, bus.Props),
            _ when WwiseHierarchy.NodeOf(node) is { } basis => (basis.EffectCount, basis.Rtpcs.Count, basis.States.Count, basis.Props),
            _ => null
        };
        if (source is not { } value) return result;
        if (value.Effects != 0) result.Add("Wwise effects");
        if (value.Rtpcs != 0) result.Add("RTPC curves");
        if (value.States != 0) result.Add("State properties");
        foreach (var property in value.Props)
            if (property.Id is 2 or 3 or 4 && Number(property) != 0) result.Add("Pitch or filter properties");
        return result.Distinct().ToList();
    }

    private static float Volume(IReadOnlyList<WwiseProp> props, byte id) =>
        (float)props.Where(property => property.Id == id).Sum(Number);

    /** A value whose bits fall below the smallest normal float is an integer property. */
    private static double Number(WwiseProp property) =>
        property.Raw is > 0 and < 0x00800000 ? property.Raw : Finite(property.Value);

    private static float Finite(float value) => float.IsFinite(value) ? value
        : throw new InvalidDataException("Non-finite music field.");
    private static double Finite(double value) => double.IsFinite(value) ? value
        : throw new InvalidDataException("Non-finite music field.");

    private static string Label(WwiseObject node) => (node switch
    {
        WwiseMusicPlaylist => "Playlist", WwiseMusicSegment => "Segment", WwiseMusicTrack => "Track", _ => "Audio object"
    }) + " " + node.Id;
}
