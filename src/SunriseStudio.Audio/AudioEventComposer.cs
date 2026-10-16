/**
 * File: Builds a playable plan from a Wwise event (layers, sequences, random picks, silences) for preview
 * and export.
 */
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace SunriseStudio.Audio;

/** Whether a group plays its children together (Layer) or one after another (Sequence). */
public enum AudioEventGroupKind { Layer, Sequence }

/** One part of a composition; it starts DelaySeconds after its parent places it. */
public abstract record AudioEventNode(uint ObjectId, string Label, double DelaySeconds);
/**
 * A media file played Loops times back to back.
 * @param Pan The 2D panner position 0..1 when the panner is on; null plays it centred with the panner off.
 */
public sealed record AudioEventSound(uint ObjectId, string Label, double DelaySeconds, uint Media, float GainDb, int Loops,
    float? Pan = null)
    : AudioEventNode(ObjectId, Label, DelaySeconds);
/** A timed gap in a plan. */
public sealed record AudioEventSilence(uint ObjectId, string Label, double DelaySeconds, double Seconds, int Loops)
    : AudioEventNode(ObjectId, Label, DelaySeconds);
/** Transition modes are the random/sequence container's: 1 and 2 cross-fade, 3 adds a gap, 5 is a trigger rate. */
public sealed record AudioEventGroup(uint ObjectId, string Label, double DelaySeconds, AudioEventGroupKind Kind,
    IReadOnlyList<AudioEventNode> Items, int Loops = 1, int TransitionMode = 0, double TransitionSeconds = 0)
    : AudioEventNode(ObjectId, Label, DelaySeconds);
/** Music keeps its own timeline; clip times start at the music object's start. */
public sealed record AudioEventMusic(uint ObjectId, string Label, double DelaySeconds, AudioTimelinePlan Timeline)
    : AudioEventNode(ObjectId, Label, DelaySeconds);
/** A part Studio cannot render; Reason is the text the UI shows. */
public sealed record AudioEventUnsupported(uint ObjectId, string Label, double DelaySeconds, string Reason)
    : AudioEventNode(ObjectId, Label, DelaySeconds);

/** A composed event: the node tree plus notices about what was simplified. */
public sealed record AudioEventPlan(AudioEventNode Root, IReadOnlyList<string> Notices)
{
    /** Every media ID the plan plays, sounds and music clips alike. */
    public IReadOnlyList<uint> Media()
    {
        var result = new List<uint>();
        void Visit(AudioEventNode node)
        {
            switch (node)
            {
                case AudioEventSound sound: result.Add(sound.Media); break;
                case AudioEventMusic music: result.AddRange(music.Timeline.Clips.Select(clip => clip.Media)); break;
                case AudioEventGroup group: foreach (var item in group.Items) Visit(item); break;
            }
        }
        Visit(Root);
        return result.Distinct().ToArray();
    }
}

/** A named plan with its routing description. */
public sealed record AudioEventComposition(string Name, string Routing, AudioEventPlan Plan);

/** Walks an event's play actions into a plan per reachable choice, without the Wwise engine. */
public static class AudioEventComposer
{
    public const int CompositionLimit = 10000;

    /**
     * Lists the compositions of one event.
     * @param analysis Music routing of the same banks; read from the hierarchy when null.
     * @return One composition per switch or state path, and per base random item when AllRandomChoices is set.
     */
    public static IReadOnlyList<AudioEventComposition> Compose(WwiseHierarchy hierarchy, uint eventId,
        AudioCompositionOptions? options = null, AudioAnalysis? analysis = null)
    {
        options ??= new();
        options.Validate();
        return new Composer(hierarchy, options, analysis).Run(eventId);
    }

    private sealed record Context(ImmutableDictionary<(bool Switch, uint Group), uint> Fixed, bool Base);

    private sealed record Alternative(AudioEventNode Node, ImmutableDictionary<(bool Switch, uint Group), uint> Used,
        string Suffix, ImmutableHashSet<string> Notices);

    private sealed class Composer(WwiseHierarchy hierarchy, AudioCompositionOptions options, AudioAnalysis? analysis)
    {
        // Action kinds sit in the action type's high byte.
        private const int PlayAction = 0x04, PlayEventAction = 0x21;
        // Plug-in IDs: Wwise Silence (a source) and Wwise Gain (an effect).
        private const uint SilencePlugin = 0x00650002, GainPlugin = 0x008B0003;
        // Version-113 RTPC parameter IDs: object, bus, make-up gain and output-bus volume.
        private const byte VolumeParameter = 0, BusVolumeParameter = 5, MakeUpGainParameter = 0x24, OutputBusVolumeParameter = 0x3C;
        // Nodes apply object volume; the node that picks the output bus adds output-bus volume.
        private static readonly byte[] NodeParameters = [VolumeParameter, MakeUpGainParameter];
        private static readonly byte[] HolderParameters = [VolumeParameter, MakeUpGainParameter, OutputBusVolumeParameter];
        private static readonly byte[] BusParameters = [VolumeParameter, BusVolumeParameter, MakeUpGainParameter, OutputBusVolumeParameter];
        // Property IDs; initial delay is in seconds and make-up gain in decibels.
        private const byte InitialDelayProp = 59, MakeUpGainProp = 33, OutputBusVolumeProp = 0x17;
        private const byte PitchProp = 2, LowPassProp = 3, HighPassProp = 4;
        private const string SpatialNotice = "3D positioning is not applied; the sound plays centred.";
        // RTPC scaling 2 holds linear gain minus one, clamped to [-1, 1]; -1 is -96.3 dB.
        private const byte DecibelScaling = 2;
        private const double MutedDb = -96.3;
        // A total at or below -96 dB is dropped so the preview stays audible.
        private const double SilentDb = -96;
        private const int DepthLimit = 64;
        // Music playlist item kinds.
        private const int SequenceContinuous = 0, RandomContinuous = 2, RandomStep = 3;
        private const string RangeNotice = "Randomized ranges use their midpoint.";
        private const string OnePickNotice = "Random: one pick, not the game seed";

        private static readonly ImmutableDictionary<(bool, uint), uint> NoPicks = ImmutableDictionary<(bool, uint), uint>.Empty;
        private static readonly ImmutableHashSet<string> NoNotices = ImmutableHashSet.Create<string>(StringComparer.Ordinal);

        /** Composes every path of the event; paths that play the same media merge into one. */
        public IReadOnlyList<AudioEventComposition> Run(uint eventId)
        {
            var root = hierarchy.Get<WwiseEvent>(eventId) ?? throw new InvalidDataException($"Event {eventId} is not in the loaded banks.");
            var fixedPicks = options.GameSyncs.ToImmutableDictionary(value => (value.Switch, value.Group), value => value.Value);
            var alternatives = Event(root, new(fixedPicks, true), 0);
            // Paths that play the same media in the same order collapse into the first; its routing names the others.
            var result = new List<(string Name, AudioEventNode Node, ImmutableDictionary<(bool, uint), uint> Used, string[] Notices, List<string> Same)>();
            var bySound = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var alternative in alternatives)
            {
                var node = options.IncludeInitialDelay ? alternative.Node : StripLead(alternative.Node);
                var notices = alternative.Notices.Order(StringComparer.Ordinal).ToArray();
                string picks = string.Concat(alternative.Used.OrderBy(pair => pair.Key.Switch ? 0 : 1).ThenBy(pair => pair.Key.Group)
                    .Select(pair => pair.Key.Switch ? $" [{pair.Key.Group}={pair.Value}]" : $" ({pair.Key.Group}={pair.Value})"));
                string name = $"Event {eventId}{picks}{alternative.Suffix}";
                string sound = Signature(node);
                if (bySound.TryGetValue(sound, out int first)) { result[first].Same.Add(name); continue; }
                bySound.Add(sound, result.Count);
                result.Add((name, node, alternative.Used, notices, []));
            }
            return result.Select(value => new AudioEventComposition(value.Name, Routing(value.Node, value.Used, value.Notices, value.Same),
                new(value.Node, value.Notices))).ToList();
        }

        /** The media, loops, timing and structure of a path; volumes, fades and start delays are left out. */
        private static string Signature(AudioEventNode node)
        {
            var text = new StringBuilder();
            void Visit(AudioEventNode value)
            {
                text.Append(value.GetType().Name).Append(':');
                switch (value)
                {
                    case AudioEventSound item: text.Append(item.Media).Append(',').Append(item.Loops); break;
                    case AudioEventSilence item: text.Append(item.Seconds.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(item.Loops); break;
                    case AudioEventUnsupported item: text.Append(item.ObjectId).Append(',').Append(item.Reason); break;
                    case AudioEventMusic item:
                        var clips = item.Timeline.Clips.Select(clip => clip with { GainDb = 0, FadeIn = null, FadeOut = null, Automation = null }).ToArray();
                        text.Append(System.Text.Json.JsonSerializer.Serialize(item.Timeline with { Playlist = 0, Segments = [], Clips = clips, Notices = [] }));
                        break;
                    case AudioEventGroup item:
                        text.Append(item.Kind).Append(',').Append(item.Loops).Append(',').Append(item.TransitionMode).Append(',')
                            .Append(item.TransitionSeconds.ToString("R", CultureInfo.InvariantCulture)).Append('[');
                        foreach (var child in item.Items) Visit(child);
                        text.Append(']');
                        break;
                }
                text.Append(';');
            }
            Visit(node);
            return text.ToString();
        }

        /** One alternative per combination of the event's play-action targets. */
        private List<Alternative> Event(WwiseEvent value, Context context, int depth)
        {
            Depth(depth);
            var plays = new List<(WwiseAction Action, uint Target)>(); var notices = NoNotices;
            foreach (uint id in value.Actions)
            {
                var action = hierarchy.Get<WwiseAction>(id);
                if (action is null) notices = notices.Add($"Action {id} is not in the loaded banks.");
                else if (action.Kind is PlayAction or PlayEventAction) plays.Add((action, action.Target));
                else notices = notices.Add($"Action type 0x{action.ActionType:X4} is not previewed.");
            }
            if (plays.Count == 0) return [];
            var inner = plays.Count == 1 ? context : context with { Base = false };
            var rows = Product(plays.Count, (index, row) => PlayTarget(plays[index].Action, row, depth + 1), inner);
            return rows.Select(row => new Alternative(plays.Count == 1 ? row.Nodes[0]
                : new AudioEventGroup(value.Id, $"Event {value.Id}: {plays.Count} play actions", 0, AudioEventGroupKind.Layer, row.Nodes),
                row.Used, row.Suffix, row.Notices.Union(notices))).ToList();
        }

        /** Plays an action's target after its delay; a play-event action composes the nested event. */
        private List<Alternative> PlayTarget(WwiseAction action, Context context, int depth)
        {
            double delay = action.Delay / 1000.0;
            if (!double.IsFinite(delay) || delay > 3600) throw new InvalidDataException($"Action {action.Id} delay is invalid.");
            delay = Math.Max(0, delay);
            var notices = new HashSet<string>(StringComparer.Ordinal);
            if (action.Ranged.Any(range => range.Id == WwiseHierarchy.DelayProp && range.Varies)) notices.Add(RangeNotice);
            if (action.Kind != PlayEventAction)
                return Noted(Wrap(Build(action.Target, context, depth), action.Id, $"Play action {action.Id}", delay), notices);
            if (hierarchy.Get<WwiseEvent>(action.Target) is not { } nested)
                return Noted([Leaf(new AudioEventUnsupported(action.Target, $"Event {action.Target}", delay,
                    $"Event {action.Target} is not in the loaded banks."))], notices);
            var played = Event(nested, context, depth);
            // An event without play actions plays nothing; its sibling actions still play.
            if (played.Count == 0)
                return Noted([Leaf(new AudioEventSilence(action.Target, $"Event {action.Target}: plays nothing", delay, 0, 1))], notices);
            return Noted(Wrap(played, action.Id, $"Play event action {action.Id}", delay), notices);
        }

        /** Composes one object by type; a missing or unknown object becomes an unsupported part. */
        private List<Alternative> Build(uint id, Context context, int depth)
        {
            Depth(depth);
            return hierarchy.Objects.GetValueOrDefault(id) switch
            {
                null => [Leaf(new AudioEventUnsupported(id, $"Object {id}", 0, $"Object {id} is not in the loaded banks."))],
                WwiseSound value => [Sound(value)],
                WwiseRandomSequence value => RandomSequence(value, context, depth),
                WwiseSwitch value => Switch(value, context, depth),
                WwiseLayerContainer value => Layer(value, context, depth),
                WwiseMusicSegment or WwiseMusicPlaylist or WwiseMusicSwitch => Music(id, context, depth),
                var other => [Leaf(new AudioEventUnsupported(id, $"Object {id}", 0, $"Object type {other.Type} is not previewed."))]
            };
        }

        /** A codec source plays with its routed gain and pan; silence and plug-ins get their own parts. */
        private Alternative Sound(WwiseSound sound)
        {
            var notices = new HashSet<string>(StringComparer.Ordinal);
            double delay = InitialDelay(sound.Node, notices); int loops = Loops(sound.Node);
            var source = sound.Source;
            AudioEventNode node;
            if (source.Codec)
            {
                float gain = Gain(sound.Id, notices); float? pan = Pan(sound.Id, notices);
                string panned = pan is { } value ? $", pan {value.ToString("0.###", CultureInfo.InvariantCulture)}" : "";
                node = new AudioEventSound(sound.Id, $"Sound {sound.Id}: media {source.MediaId}, {Decibels(gain)}{panned}", delay,
                    source.MediaId, gain, loops, pan);
            }
            else if (source.Plugin == SilencePlugin)
                node = new AudioEventSilence(sound.Id, $"Sound {sound.Id}: silence", delay, SilenceSeconds(source.MediaId, notices), loops);
            else if (source.Plugin == 0)
                node = new AudioEventUnsupported(sound.Id, $"Sound {sound.Id}", delay, $"Sound {sound.Id} has no source.");
            else
                node = new AudioEventUnsupported(sound.Id, $"Sound {sound.Id}: plug-in 0x{source.Plugin:X8}", delay,
                    "Generator plug-in: needs the Wwise engine");
            return new(node, NoPicks, "", NoNotices.Union(notices));
        }

        /** Step containers play one item per trigger; continuous ones play every item in authored order. */
        private List<Alternative> RandomSequence(WwiseRandomSequence container, Context context, int depth)
        {
            var items = container.Playlist.Count > 0 ? container.Playlist.Select(entry => entry.Id).ToArray() : container.Children.ToArray();
            string kind = container.Sequence ? "Sequence container" : "Random container";
            var notices = new HashSet<string>(StringComparer.Ordinal);
            double delay = InitialDelay(container.Node, notices);
            if (items.Length == 0)
                return Noted([Leaf(new AudioEventSilence(container.Id, $"{kind} {container.Id}: plays nothing", delay, 0, 1))], notices);
            if (container.Continuous)
            {
                int loops = container.LoopCount == 0 ? options.LoopCount : container.LoopCount;
                if (!container.Sequence && items.Length > 1) notices.Add("Continuous random containers play every item once, in authored order.");
                string label = $"{kind} {container.Id}: continuous, {items.Length} items{Transition(container)}";
                return Noted(Product(items.Length, (index, row) => Build(items[index], row, depth + 1), context with { Base = false })
                    .Select(row => new Alternative(new AudioEventGroup(container.Id, label, delay, AudioEventGroupKind.Sequence, row.Nodes,
                        loops, container.TransitionMode, container.TransitionTime / 1000.0), row.Used, row.Suffix, row.Notices))
                    .ToList(), notices);
            }
            if (context.Base && options.AllRandomChoices && items.Length > 1)
            {
                var result = new List<Alternative>();
                for (int index = 0; index < items.Length; index++)
                {
                    int number = index + 1;
                    foreach (var value in Build(items[index], context with { Base = false }, depth + 1))
                        result.Add(value with
                        {
                            Node = Wrap(container.Id, $"{kind} {container.Id}: item {number} of {items.Length}", delay, value.Node),
                            Suffix = $" {{r{number}}}" + value.Suffix
                        });
                    Limit(result.Count);
                }
                return Noted(result, notices);
            }
            var first = context with { Base = context.Base && items.Length == 1 };
            return Noted(Wrap(Build(items[0], first, depth + 1), container.Id, $"{kind} {container.Id}: item 1 of {items.Length}", delay),
                notices);
        }

        /**
         * A chosen value plays its children; without a choice every listed value is its own path.
         * A value missing from the list plays the default value; a listed value with no children plays nothing.
         */
        private List<Alternative> Switch(WwiseSwitch container, Context context, int depth)
        {
            var key = (container.GroupType == 0, container.Group);
            string kind = container.GroupType == 0 ? "switch" : "state";
            var notices = new HashSet<string>(StringComparer.Ordinal);
            double delay = InitialDelay(container.Node, notices);
            IReadOnlyList<uint>? Nodes(uint value) => container.Groups.FirstOrDefault(group => group.Switch == value).Nodes;
            var values = context.Fixed.TryGetValue(key, out uint chosen) ? [chosen]
                : container.Groups.Select(group => group.Switch).Distinct().ToList();
            if (values.Count == 0)
                return Noted([Leaf(new AudioEventSilence(container.Id, $"Switch container {container.Id}: plays nothing", delay, 0, 1))],
                    notices);
            var result = new List<Alternative>();
            foreach (uint value in values)
            {
                var children = Nodes(value) ?? (value != container.DefaultSwitch ? Nodes(container.DefaultSwitch) : null) ?? [];
                string label = $"Switch container {container.Id}: {kind} {container.Group} = {value}";
                var picks = context.Fixed.SetItem(key, value);
                if (children.Count == 0)
                {
                    result.Add(new(new AudioEventSilence(container.Id, label + ": plays nothing", delay, 0, 1),
                        NoPicks.SetItem(key, value), "", NoNotices));
                    continue;
                }
                var inner = new Context(picks, context.Base && children.Count == 1);
                foreach (var row in Product(children.Count, (index, next) => Build(children[index], next, depth + 1), inner))
                    result.Add(new(new AudioEventGroup(container.Id, label, delay, AudioEventGroupKind.Layer, row.Nodes),
                        row.Used.SetItem(key, value), row.Suffix, row.Notices));
                Limit(result.Count);
            }
            return Noted(result, notices);
        }

        /** Plays every child together; layer cross-fade curves are not applied. */
        private List<Alternative> Layer(WwiseLayerContainer container, Context context, int depth)
        {
            var notices = new HashSet<string>(StringComparer.Ordinal);
            double delay = InitialDelay(container.Node, notices);
            var children = container.Children;
            if (children.Count == 0)
                return Noted([Leaf(new AudioEventSilence(container.Id, $"Layer container {container.Id}: plays nothing", delay, 0, 1))],
                    notices);
            if (container.Layers.Any(layer => layer.Rtpc != 0 || layer.Associations.Any(link => link.Points.Count > 0)))
                notices.Add("Layer cross-fade curves are not applied.");
            string label = $"Layer container {container.Id}: {children.Count} layers";
            return Noted(Product(children.Count, (index, row) => Build(children[index], row, depth + 1), context with { Base = false })
                .Select(row => new Alternative(new AudioEventGroup(container.Id, label, delay, AudioEventGroupKind.Layer, row.Nodes),
                    row.Used, row.Suffix, row.Notices)).ToList(), notices);
        }

        /** Music plays from the segment start to its end; a playlist follows the timeline's routing. */
        private List<Alternative> Music(uint id, Context context, int depth)
        {
            Depth(depth);
            var item = hierarchy.Objects.GetValueOrDefault(id);
            if (item is WwiseMusicSwitch musicSwitch) return MusicSwitch(musicSwitch, context, depth);
            var node = item switch { WwiseMusicSegment value => value.Music.Node, WwiseMusicPlaylist value => value.Music.Node, _ => null };
            if (node is null)
                return [Leaf(new AudioEventUnsupported(id, $"Music object {id}", 0, $"Music object {id} is not in the loaded banks."))];
            var notices = new HashSet<string>(StringComparer.Ordinal);
            double delay = InitialDelay(node, notices);
            string label = item is WwiseMusicSegment ? $"Music segment {id}" : $"Music playlist {id}";
            if (item is WwiseMusicSegment) return Noted([Plan(-1, "")], notices);
            int count = context.Base && options.AllRandomChoices ? RandomBranches(id) : 1;
            var result = new List<Alternative>();
            for (int variation = 0; variation < count; variation++)
            {
                result.Add(Plan(variation, count > 1 ? $" {{r{variation + 1}}}" : ""));
                Limit(result.Count);
            }
            return Noted(result, notices);

            // Variation -1 is the segment alone; otherwise it picks the random playlist branches.
            Alternative Plan(int variation, string suffix)
            {
                try
                {
                    var picked = variation < 0 ? null : OnePick(id, variation);
                    var plan = variation < 0 ? Segment(id)
                        : AudioTimeline.Build(picked ?? Dry(), id, options.LoopCount, variation, new AudioPlaylistHistory());
                    var planNotices = NoNotices.Union(plan.Notices).Add(DryNotice);
                    if (picked is not null) planNotices = planNotices.Add(OnePickNotice);
                    return new(new AudioEventMusic(id, $"{label}: {plan.Clips.Count} clips, {plan.DurationSeconds:0.000} s", delay, plan),
                        NoPicks, suffix, planNotices);
                }
                catch (Exception error) when (error is NotSupportedException or InvalidDataException)
                {
                    return new(new AudioEventUnsupported(id, label, delay, error.Message), NoPicks, suffix, NoNotices);
                }
            }
        }

        /** The largest child count of any random item; each child is one listed composition. */
        private int RandomBranches(uint id)
        {
            if (!Dry().Playlists.TryGetValue(id, out var playlist)) return 1;
            static int Most(AudioPlaylistItem item) => Math.Max(item.Kind is RandomContinuous or RandomStep ? item.Children.Count : 1,
                item.Children.Select(Most).DefaultIfEmpty(1).Max());
            return Math.Max(1, Most(playlist.Root));
        }

        /**
         * Replaces each random step item with one of its children, played once.
         * @return The routing with the picked playlist, or null when the playlist has no such item.
         */
        private AudioAnalysis? OnePick(uint id, int variation)
        {
            var routing = Dry();
            if (!routing.Playlists.TryGetValue(id, out var playlist)) return null;
            bool picked = false;
            AudioPlaylistItem Pick(AudioPlaylistItem item)
            {
                var children = item.Children.Select(Pick).ToArray();
                if (item.Segment != 0 || children.Length == 0 || item.Kind != RandomStep) return item with { Children = children };
                picked = true;
                return item with
                {
                    Kind = SequenceContinuous, Loops = 1, LoopMinimum = 0, LoopMaximum = 0, Children = [children[variation % children.Length]]
                };
            }
            var root = Pick(playlist.Root);
            if (!picked) return null;
            var playlists = routing.Playlists.ToDictionary(pair => pair.Key, pair => pair.Value);
            playlists[id] = playlist with { Root = root };
            return routing with { Playlists = playlists };
        }

        /** One alternative per decision-tree path; a fixed game sync picks its branch. */
        private List<Alternative> MusicSwitch(WwiseMusicSwitch container, Context context, int depth)
        {
            var result = new List<Alternative>();
            var notices = new HashSet<string>(StringComparer.Ordinal);
            double delay = InitialDelay(container.Music.Node, notices);
            void Walk(WwiseDecisionNode node, int level, Context inner, ImmutableDictionary<(bool, uint), uint> used, string path)
            {
                if (level == container.Arguments.Count)
                {
                    // A path that ends in no node switches the music to nothing: a real, silent path.
                    if (node.AudioNode == 0)
                    {
                        result.Add(new(new AudioEventSilence(container.Id, $"Music switch {container.Id}:{path}: plays nothing", delay, 0, 1),
                            used, "", NoNotices));
                        Limit(result.Count);
                        return;
                    }
                    foreach (var value in Music(node.AudioNode, inner, depth + 1))
                        result.Add(value with
                        {
                            Node = Wrap(container.Id, $"Music switch {container.Id}:{path}", delay, value.Node),
                            Used = value.Used.SetItems(used)
                        });
                    Limit(result.Count);
                    return;
                }
                var (group, type) = container.Arguments[level];
                var key = (type == 0, group);
                string kind = type == 0 ? "switch" : "state";
                if (inner.Fixed.TryGetValue(key, out uint chosen))
                {
                    var match = node.Children.FirstOrDefault(child => child.Key == chosen) ?? node.Children.FirstOrDefault(child => child.Key == 0);
                    if (match is not null) Walk(match, level + 1, inner, used.SetItem(key, chosen), path + $" {kind} {group} = {chosen}");
                    return;
                }
                foreach (var child in node.Children)
                {
                    if (child.Key == 0) Walk(child, level + 1, inner, used, path + $" {kind} {group} = any");
                    else Walk(child, level + 1, inner with { Fixed = inner.Fixed.SetItem(key, child.Key) }, used.SetItem(key, child.Key),
                        path + $" {kind} {group} = {child.Key}");
                }
            }
            foreach (var root in container.Tree) Walk(root, 0, context with { Base = false }, NoPicks, "");
            if (result.Count == 0)
                result.Add(Leaf(new AudioEventSilence(container.Id, $"Music switch {container.Id}: plays nothing", delay, 0, 1)));
            return Noted(result, notices);
        }

        private const string DryNotice = "Music plays dry: effects, game-parameter curves, states, pitch and filters are not applied.";
        // Music limits that a dry preview reports instead of refusing.
        private static readonly HashSet<string> DryLimits =
            ["Wwise effects", "RTPC curves", "State properties", "Pitch or filter properties"];
        private AudioAnalysis? dry;

        /** The music routing without the limits a dry preview leaves out. */
        private AudioAnalysis Dry()
        {
            if (dry is not null) return dry;
            var source = analysis ??= AudioAnalysis.Read(hierarchy);
            IReadOnlyList<string> Keep(IReadOnlyList<string> limits) => limits.Where(limit => !DryLimits.Contains(limit)).ToArray();
            return dry = source with
            {
                Tracks = source.Tracks.ToDictionary(pair => pair.Key, pair => pair.Value with { Limits = Keep(pair.Value.Limits) }),
                Segments = source.Segments.ToDictionary(pair => pair.Key, pair => pair.Value with { Limits = Keep(pair.Value.Limits) }),
                Playlists = source.Playlists.ToDictionary(pair => pair.Key, pair => pair.Value with { Limits = Keep(pair.Value.Limits) }),
                Routes = source.Routes.ToDictionary(pair => pair.Key, pair => pair.Value with { Limits = Keep(pair.Value.Limits) })
            };
        }

        /** One segment alone plays from its start to its end, pre-entry and post-exit included. */
        private AudioTimelinePlan Segment(uint id)
        {
            var routing = Dry();
            if (!routing.Segments.TryGetValue(id, out var segment)) throw new InvalidDataException($"Music segment {id} is not in the loaded banks.");
            if (segment.Limits.Count > 0) throw new NotSupportedException(string.Join(", ", segment.Limits));
            var notices = new HashSet<string>(StringComparer.Ordinal); var clips = new List<AudioTimelineClip>();
            foreach (uint trackId in segment.Tracks)
            {
                if (!routing.Tracks.TryGetValue(trackId, out var track)) throw new InvalidDataException("Segment track is missing.");
                if (track.Limits.Count > 0) throw new NotSupportedException(string.Join(", ", track.Limits));
                float gain = Gain(trackId, notices, music: true); float? pan = Pan(trackId, notices);
                for (int clipIndex = 0; clipIndex < track.Clips.Count; clipIndex++)
                {
                    var clip = track.Clips[clipIndex];
                    if (clip.SourceDuration <= 0 || clip.BeginTrim < 0 || clip.BeginTrim >= clip.SourceDuration)
                        throw new NotSupportedException("This music clip has an unsupported source trim.");
                    double clipStart = clip.PlayAt + clip.BeginTrim, clipEnd = clip.PlayAt + clip.SourceDuration + clip.EndTrim;
                    if (clipEnd < clipStart) throw new InvalidDataException("Music clip trim is invalid.");
                    double start = Math.Max(0, clipStart), end = Math.Min(segment.Duration, clipEnd);
                    if (end <= start) continue;
                    int index = clipIndex;
                    clips.Add(new(segment.Id, trackId, clip.SourceId, start / 1000, (start - clip.PlayAt) / 1000, (end - start) / 1000, gain,
                        LoopSeconds: clip.EndTrim > 0 ? clip.SourceDuration / 1000 : 0,
                        Automation: track.Automation?.Where(value => value.ClipIndex == index).ToArray(),
                        AutomationOffsetSeconds: (start - clipStart) / 1000, Pan: pan));
                }
            }
            double duration = segment.Duration / 1000;
            if (!double.IsFinite(duration) || duration is <= 0 or > 1200) throw new InvalidDataException("Music segment length is invalid.");
            return new(id, [new(segment.Id, segment.Name, 0, segment.Entry / 1000, segment.Exit / 1000, duration)], clips, duration,
                notices.Order(StringComparer.Ordinal).ToArray());
        }

        /** Sums object volumes up the parent chain, then bus volumes down the first output bus. */
        private float Gain(uint id, ISet<string> notices, bool music = false)
        {
            double total = 0; uint bus = 0; var visited = new HashSet<uint>();
            for (uint at = id; at != 0;)
            {
                if (!visited.Add(at)) throw new InvalidDataException("Audio parent routing contains a cycle.");
                var node = WwiseHierarchy.NodeOf(hierarchy.Objects.GetValueOrDefault(at));
                if (node is null) { notices.Add("A parent audio node is outside the loaded banks."); break; }
                bool holder = bus == 0 && node.OutputBus != 0;
                bool Volume(byte prop) => prop == WwiseHierarchy.VolumeProp
                    || !music && (prop == MakeUpGainProp || holder && prop == OutputBusVolumeProp);
                total += node.Props.Where(prop => Volume(prop.Id)).Sum(prop => (double)prop.Value);
                total += node.Ranged.Where(range => Volume(range.Id)).Sum(range => range.Midpoint);
                if (node.Ranged.Any(range => Volume(range.Id) && range.Varies)) notices.Add(RangeNotice);
                if (node.Props.Any(prop => prop.Id is PitchProp or LowPassProp or HighPassProp && prop.Value != 0)
                    || node.Ranged.Any(range => range.Id is PitchProp or LowPassProp or HighPassProp && range.Varies))
                    notices.Add("Pitch and filter properties are not applied.");
                if (!music)
                    total += Choices(node.Rtpcs, node.States, node.Effects, holder ? HolderParameters : NodeParameters,
                        WwiseHierarchy.VolumeProp, notices);
                if (holder) bus = node.OutputBus;
                at = node.Parent;
            }
            while (bus != 0)
            {
                if (!visited.Add(bus)) throw new InvalidDataException("Audio bus routing contains a cycle.");
                if (hierarchy.Get<WwiseBus>(bus) is not { } value) { notices.Add("An output bus is outside the loaded banks."); break; }
                total += value.Props.Where(prop => prop.Id == WwiseHierarchy.BusVolumeProp).Sum(prop => (double)prop.Value);
                if (!music) total += Choices(value.Rtpcs, value.States, value.Effects, BusParameters, WwiseHierarchy.BusVolumeProp, notices);
                bus = value.OutputBus;
            }
            if (!double.IsFinite(total)) throw new InvalidDataException("Audio volume is not finite.");
            if (total <= SilentDb)
            {
                notices.Add($"Volume of {total:0.0} dB is below -96 dB and is not applied.");
                return 0;
            }
            return (float)total;
        }

        /** The 2D panner position, or null when the panner is off or the node is 3D; notes what a preview leaves out. */
        private float? Pan(uint id, ISet<string> notices)
        {
            var (pan, spatial, curve) = hierarchy.Positioning(id);
            if (spatial) notices.Add(SpatialNotice);
            if (curve) notices.Add("Game parameter curves on panning are not applied.");
            return pan;
        }

        /** Volume from chosen game parameters and states, and from Wwise Gain effects when effect volumes are on. */
        private double Choices(IReadOnlyList<WwiseRtpc> rtpcs, IReadOnlyList<WwiseStateGroup> states, IReadOnlyList<uint>? effects,
            byte[] parameters, byte stateProp, ISet<string> notices)
        {
            double total = 0;
            foreach (var rtpc in rtpcs.Where(value => parameters.Contains(value.Parameter) && value.Points.Count > 0))
            {
                var chosen = options.Variables.FirstOrDefault(value => value.Id == rtpc.Id);
                if (chosen is null) continue;
                total += Curve(rtpc, chosen.Value);
                notices.Add($"Game parameter {rtpc.Id} = {chosen.Value.ToString("R", CultureInfo.InvariantCulture)} applied.");
            }
            foreach (var group in states)
            {
                var chosen = options.States.FirstOrDefault(value => value.Group == group.Group);
                if (chosen is null) continue;
                foreach (var (state, instance) in group.States.Where(value => value.State == chosen.Value))
                    if (hierarchy.Get<WwiseState>(instance) is { } values)
                        total += values.Props.Where(prop => prop.Id == stateProp).Sum(prop => (double)prop.Value);
            }
            if (options.IncludeEffectVolumes && effects is not null)
                foreach (uint effect in effects)
                    if (hierarchy.Get<WwiseOther>(effect) is { Plugin: GainPlugin, Parameters.Length: >= 4 } gain)
                        total += BitConverter.ToSingle(gain.Parameters, 0);
            return total;
        }

        /** Evaluates a game-parameter curve; each segment uses its left point's curve shape. */
        private static double Curve(WwiseRtpc rtpc, float value)
        {
            var points = rtpc.Points;
            float y;
            if (value <= points[0].From) y = points[0].To;
            else if (value >= points[^1].From) y = points[^1].To;
            else
            {
                int index = 0;
                while (index < points.Count - 2 && value >= points[index + 1].From) index++;
                var left = points[index]; var right = points[index + 1];
                float span = right.From - left.From;
                float x = span > 0 ? Math.Clamp((value - left.From) / span, 0, 1) : 1;
                y = AudioCurves.Evaluate(left.Curve <= 9 ? (int)left.Curve : 4, x, left.To, right.To);
            }
            if (!float.IsFinite(y)) throw new InvalidDataException("Game parameter curve is not finite.");
            if (rtpc.Scaling != DecibelScaling) return y;
            y = Math.Clamp(y, -1, 1);
            return y == -1 ? MutedDb : 20 * Math.Log10(1 + (double)y);
        }

        private double SilenceSeconds(uint source, ISet<string> notices)
        {
            if (hierarchy.Get<WwiseOther>(source) is { Plugin: SilencePlugin, Parameters.Length: >= 4 } settings)
            {
                double seconds = BitConverter.ToSingle(settings.Parameters, 0);
                if (!double.IsFinite(seconds) || seconds is < 0 or > 3600) throw new InvalidDataException("Silence length is invalid.");
                return seconds;
            }
            notices.Add("Silence length is not in the loaded banks; 1 second is used.");
            return 1;
        }

        /** Seconds before the node plays; a range adds its midpoint and the sum stops at zero, as in Wwise. */
        private static double InitialDelay(WwiseNodeBase node, ISet<string> notices)
        {
            double seconds = node.Props.FirstOrDefault(value => value.Id == InitialDelayProp)?.Value ?? 0;
            var ranges = node.Ranged.Where(value => value.Id == InitialDelayProp).ToArray();
            seconds += ranges.Sum(range => range.Midpoint);
            if (ranges.Any(range => range.Varies)) notices.Add(RangeNotice);
            if (!double.IsFinite(seconds) || seconds > 3600) throw new InvalidDataException("Initial delay is invalid.");
            return Math.Max(0, seconds);
        }

        /** Loop 0 is endless and plays the chosen preview loop count. */
        private int Loops(WwiseNodeBase node)
        {
            var prop = node.Props.FirstOrDefault(value => value.Id == WwiseHierarchy.LoopProp);
            if (prop is null) return 1;
            return prop.Raw == 0 ? options.LoopCount : (int)Math.Min(prop.Raw, 1000);
        }

        /** Builds rows of one alternative per item; later items see the picks of earlier ones. */
        private static List<(List<AudioEventNode> Nodes, ImmutableDictionary<(bool, uint), uint> Used, string Suffix, ImmutableHashSet<string> Notices)>
            Product(int count, Func<int, Context, List<Alternative>> build, Context context)
        {
            var rows = new List<(List<AudioEventNode>, ImmutableDictionary<(bool, uint), uint>, string, ImmutableHashSet<string>)>
                { ([], NoPicks, "", NoNotices) };
            for (int index = 0; index < count; index++)
            {
                var next = new List<(List<AudioEventNode>, ImmutableDictionary<(bool, uint), uint>, string, ImmutableHashSet<string>)>();
                foreach (var (nodes, used, suffix, notices) in rows)
                {
                    foreach (var value in build(index, context with { Fixed = context.Fixed.SetItems(used) }))
                        next.Add(([.. nodes, value.Node], used.SetItems(value.Used), suffix + value.Suffix, notices.Union(value.Notices)));
                    Limit(next.Count);
                }
                rows = next;
            }
            return rows;
        }

        private static AudioEventNode Wrap(uint id, string label, double delay, AudioEventNode child) =>
            new AudioEventGroup(id, label, delay, AudioEventGroupKind.Layer, [child]);

        private static List<Alternative> Wrap(List<Alternative> items, uint id, string label, double delay) =>
            items.Select(value => value with { Node = Wrap(id, label, delay, value.Node) }).ToList();

        private static List<Alternative> Noted(List<Alternative> items, IReadOnlyCollection<string> notices) =>
            notices.Count == 0 ? items : items.Select(value => value with { Notices = value.Notices.Union(notices) }).ToList();

        private static Alternative Leaf(AudioEventNode node) => new(node, NoPicks, "", NoNotices);

        private static void Depth(int depth)
        {
            if (depth > DepthLimit) throw new InvalidDataException("Audio routing is nested too deeply.");
        }

        private static void Limit(int count)
        {
            if (count > CompositionLimit) throw new InvalidDataException("Audio event generated too many compositions.");
        }

        private static string Transition(WwiseRandomSequence container) => container.TransitionMode switch
        {
            1 or 2 => $", cross-fade {container.TransitionTime / 1000:0.###} s",
            3 => $", gap {container.TransitionTime / 1000:0.###} s",
            5 => $", trigger every {container.TransitionTime / 1000:0.###} s",
            _ => ""
        };
    }

    /** Removes the silence before the first sound starts. */
    private static AudioEventNode StripLead(AudioEventNode node)
    {
        double lead = Lead(node);
        return lead <= 0 ? node : Shift(node, lead);

        static double Lead(AudioEventNode value) => value switch
        {
            AudioEventGroup { Kind: AudioEventGroupKind.Layer, Items.Count: > 0 } group =>
                group.DelaySeconds + group.Items.Min(Lead),
            AudioEventGroup { Kind: AudioEventGroupKind.Sequence, Items.Count: > 0 } group => group.DelaySeconds + Lead(group.Items[0]),
            _ => value.DelaySeconds
        };

        static AudioEventNode Shift(AudioEventNode value, double amount)
        {
            if (amount <= value.DelaySeconds) return value with { DelaySeconds = value.DelaySeconds - amount };
            amount -= value.DelaySeconds;
            if (value is not AudioEventGroup group || group.Items.Count == 0) return value with { DelaySeconds = 0 };
            var items = group.Kind == AudioEventGroupKind.Layer ? group.Items.Select(item => Shift(item, amount)).ToArray()
                : [Shift(group.Items[0]!, amount), .. group.Items.Skip(1)];
            return group with { DelaySeconds = 0, Items = items };
        }
    }

    /** The routing text: picks, the part tree, merged paths and notices. */
    private static string Routing(AudioEventNode root, IReadOnlyDictionary<(bool Switch, uint Group), uint> picks,
        IReadOnlyList<string> notices, IReadOnlyList<string> same)
    {
        var text = new StringBuilder();
        foreach (var pair in picks.OrderBy(pair => pair.Key.Switch ? 0 : 1).ThenBy(pair => pair.Key.Group))
            text.Append(pair.Key.Switch ? "Switch " : "State ").Append(pair.Key.Group).Append(" = ").Append(pair.Value).Append('\n');
        void Print(AudioEventNode node, int depth)
        {
            text.Append(' ', depth * 2).Append(node.Label);
            if (node.DelaySeconds > 0) text.Append(", starts after ").Append(node.DelaySeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(" s");
            int loops = node switch { AudioEventSound value => value.Loops, AudioEventSilence value => value.Loops, AudioEventGroup value => value.Loops, _ => 1 };
            if (loops != 1) text.Append(", plays ").Append(loops).Append(" times");
            if (node is AudioEventSilence silence) text.Append(", ").Append(silence.Seconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(" s");
            if (node is AudioEventUnsupported unsupported) text.Append(" -- not playable: ").Append(unsupported.Reason);
            text.Append('\n');
            if (node is AudioEventGroup group) foreach (var item in group.Items) Print(item, depth + 1);
        }
        Print(root, 0);
        foreach (string name in same) text.Append("Same media, volume or delay may differ: ").Append(name).Append('\n');
        foreach (string notice in notices) text.Append("Note: ").Append(notice).Append('\n');
        return text.ToString();
    }

    private static string Decibels(float value) => value.ToString("0.0", CultureInfo.InvariantCulture) + " dB";
}
