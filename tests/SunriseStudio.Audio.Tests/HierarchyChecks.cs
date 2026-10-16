using SunriseStudio.Audio;

/** The managed version-113 bank reader: every object type, refusals and the music routing built from it. */
internal static class HierarchyChecks
{
    private const uint Event = 100, Play = 101, Sound = 200, Mixer = 300, Random = 400, Switch = 500, Layer = 600;
    private const uint Bus = 800, Segment = 1000, Track = 1100, MusicSwitch = 1200, Playlist = 1300, State = 1400;
    private const uint StateGroup = 8000, StateValue = 8001, SwitchGroup = 7000, Parameter = 42;

    /** Runs the checks that use generated fixtures instead of installed game content. */
    public static void Synthetic()
    {
        var hierarchy = WwiseHierarchy.Read([TestHirc.Bank(113, 900, Objects())]);
        Check(hierarchy.Diagnostics.Count == 0 && hierarchy.Banks.Single().Objects.Count == Objects().Count(), "A valid object was skipped.");
        Check(hierarchy.Get<WwiseEvent>(Event)!.Actions.SequenceEqual([Play]), "Event actions are wrong.");
        var action = hierarchy.Get<WwiseAction>(Play)!;
        // The delay range -100..300 ms adds its 100 ms midpoint; the bounds are integers.
        Check(action.Kind == 0x04 && action.Target == Sound && action.Delay == 350 && action.Ranged.Single().Varies,
            "Play action fields are wrong.");
        var sound = hierarchy.Get<WwiseSound>(Sound)!;
        Check(sound.Source is { Codec: true, MediaId: 5000 } && sound.Node.Parent == Mixer
            && Math.Abs(sound.Node.Prop(WwiseHierarchy.VolumeProp) + 1) < 1e-6, "Sound source or node is wrong.");
        Check(sound.Node.States.Single() is { Group: StateGroup } group && group.States.Single() == (StateValue, State)
            && sound.Node.Rtpcs.Single() is { Id: Parameter, Parameter: 0, Points.Count: 2 }, "Sound states or curves are wrong.");
        Check(hierarchy.Get<WwiseActorMixer>(Mixer) is { Node.OutputBus: Bus } mixer && mixer.Children.SequenceEqual([Sound, Random, Switch, Layer]),
            "Actor-mixer links are wrong.");
        var random = hierarchy.Get<WwiseRandomSequence>(Random)!;
        Check(!random.Sequence && random.Continuous && random.LoopCount == 2 && random.TransitionMode == 1
            && random.Playlist.Select(item => item.Id).SequenceEqual([201u, 202u]), "Random container fields are wrong.");
        var choice = hierarchy.Get<WwiseSwitch>(Switch)!;
        Check(choice.Group == SwitchGroup && choice.Groups.Count == 2 && choice.Groups[1].Nodes.SequenceEqual([204u])
            && choice.Parameters.Count == 2, "Switch container fields are wrong.");
        var layer = hierarchy.Get<WwiseLayerContainer>(Layer)!;
        Check(layer.Children.Count == 2 && layer.Layers.Single().Associations.Single() is { Child: 205, Points.Count: 2 },
            "Layer container fields are wrong.");
        Check(hierarchy.Get<WwiseBus>(Bus) is { OutputBus: 0 } bus && Math.Abs(bus.Prop(WwiseHierarchy.BusVolumeProp) + 2) < 1e-6,
            "Bus fields are wrong.");
        var segment = hierarchy.Get<WwiseMusicSegment>(Segment)!;
        Check(segment.Duration == 2000 && segment.Markers.Count == 2 && segment.Markers[1].Name == "Exit"
            && segment.Music.Children.SequenceEqual([Track]), "Music segment fields are wrong.");
        var track = hierarchy.Get<WwiseMusicTrack>(Track)!;
        Check(track.Sources.Single().MediaId == 5003 && track.Clips.Single() is { SourceDuration: 2000, PlayAt: 0 }
            && track.Automation.Single() is { Clip: 0, Type: 0, Points.Count: 2 }, "Music track fields are wrong.");
        var musicSwitch = hierarchy.Get<WwiseMusicSwitch>(MusicSwitch)!;
        Check(musicSwitch.Arguments.Single() == (StateGroup, (byte)1) && musicSwitch.Tree.Single().Children.Count == 2
            && musicSwitch.Tree[0].Children[0] is { Key: StateValue, AudioNode: Playlist }, "Music switch tree is wrong.");
        var playlist = hierarchy.Get<WwiseMusicPlaylist>(Playlist)!;
        Check(playlist.Root is { Children.Count: 1 } root && root.Children[0] is { Segment: Segment, Loop: 3 }
            && playlist.Rules.Single() is { Source.Sync: 7, Destination.PreEntry: true }, "Music playlist fields are wrong.");
        Check(hierarchy.Get<WwiseState>(State)!.Props.Single().Value == -5, "State properties are wrong.");

        var analysis = AudioAnalysis.Read([TestHirc.Bank(113, 900, Objects())]);
        Check(analysis.Diagnostics.Count == 0 && analysis.Segments[Segment] is { Duration: 2000, Entry: 0, Exit: 2000 }
            && analysis.Tracks[Track].Clips.Single().SourceId == 5003 && analysis.Tracks[Track].Automation!.Count == 1,
            "Music routing lost segment or track fields.");
        Check(analysis.Playlists[Playlist].Transitions.Single() is { SourceSync: 7, PreEntry: true, PostExit: true }
            && analysis.Playlists[Playlist].Root.Children.Single().Loops == 3, "Music routing lost playlist fields.");
        Check(analysis.GameSyncs.Single() is { Id: StateGroup, Switch: false } sync && sync.Choices.Select(value => value.Value).SequenceEqual([0u, StateValue]),
            "Music switch choices are wrong.");
        Check(analysis.Parameters.Single() is { Id: Parameter, Minimum: 0, Maximum: 1 }, "Game parameter range is wrong.");
        Check(analysis.Routes[Bus] is { Bus: true, VolumeDb: -2 } && analysis.Routes[Sound] is { Parent: Mixer, VolumeDb: -1 },
            "Volume routes are wrong.");

        // One byte too many, or a list that leaves its object, skips only that object.
        var broken = Objects().ToList();
        broken.Add((2, 299, [.. TestHirc.Sound(TestHirc.Vorbis, 5009, 0), 0]));
        broken.Add((4, 199, ((byte[])[.. TestHirc.Ids(Play), .. BitConverter.GetBytes(Play)])[..^5]));
        broken.Add((4, 198, [5, 0, 0, 0, 1, 0, 0, 0]));
        var partial = WwiseHierarchy.Read([TestHirc.Bank(113, 900, broken)]);
        Check(partial.Banks.Single().Objects.Count == Objects().Count() && partial.Diagnostics.Count == 3
            && partial.Diagnostics.Any(value => value.Contains("object 299", StringComparison.Ordinal) && value.Contains("1 bytes left", StringComparison.Ordinal))
            && partial.Diagnostics.Any(value => value.Contains("object 198", StringComparison.Ordinal)), "A malformed object was not skipped by name.");
        Check(WwiseHierarchy.Parse(new(16, 77, [1, 2, 3])) is null, "An unknown object type was parsed.");

        // An event may share a bus's ID; each keeps its own.
        var shared = WwiseHierarchy.Read([TestHirc.Bank(113, 902, [(8, 77, TestHirc.Bus(0, 0)), (4, 77, TestHirc.Ids(Play))])]);
        Check(shared.Get<WwiseBus>(77) is not null && shared.Get<WwiseEvent>(77) is not null, "An event and a bus with one ID collided.");

        var old = WwiseHierarchy.Read([TestHirc.Bank(112, 901, Objects())]);
        Check(old.Objects.Count == 0 && old.Diagnostics.SequenceEqual(["Detailed music timelines need bank version 113."]),
            "A version-112 bank was read.");
        Console.WriteLine($"audio hierarchy=v113 objects={hierarchy.Objects.Count} malformed=skipped version112=refused analysis=bank_bytes");
    }

    /** One object of every type the reader knows, linked into one event and one music switch. */
    private static IEnumerable<(byte Type, uint Id, byte[] Data)> Objects()
    {
        var curve = new TestHirc.Curve(Parameter, 0, 0, [(0, 0, 4), (1, -12, 4)]);
        yield return (4, Event, TestHirc.Ids(Play));
        yield return (3, Play, TestHirc.Action(0x0403, Sound, 250, (-100, 300)));
        yield return (2, Sound, TestHirc.Sound(TestHirc.Vorbis, 5000, Mixer, props: [(WwiseHierarchy.VolumeProp, TestHirc.F(-1))],
            states: [(StateGroup, [(StateValue, State)])], curves: [curve]));
        yield return (7, Mixer, [.. TestHirc.Node(Bus, 0), .. TestHirc.Ids(Sound, Random, Switch, Layer)]);
        yield return (5, Random, TestHirc.RandomSequence(Mixer, false, true, 2, 1, 0, [201, 202]));
        yield return (6, Switch, TestHirc.Switch(Mixer, 0, SwitchGroup, [(7001, [203]), (7002, [204])]));
        yield return (9, Layer, TestHirc.Layer(Mixer, [205, 206]));
        yield return (8, Bus, TestHirc.Bus(0, -2));
        yield return (10, Segment, TestHirc.Segment(0, Track, 2000));
        yield return (11, Track, TestHirc.Track(Segment, 5003, 2000));
        yield return (12, MusicSwitch, TestHirc.MusicSwitch(0, StateGroup, 1, [(StateValue, Playlist), (0, Segment)]));
        yield return (13, Playlist, TestHirc.Playlist(0, Segment, 3));
        yield return (1, State, TestHirc.Props((WwiseHierarchy.VolumeProp, TestHirc.F(-5))));
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}

/** Little-endian writer for synthetic bank objects. */
internal sealed class HircBytes
{
    private readonly List<byte> data = [];
    /** Appends one byte. */
    public HircBytes U8(int value) { data.Add((byte)value); return this; }
    /** Appends a little-endian 16-bit value. */
    public HircBytes U16(int value) { data.AddRange(BitConverter.GetBytes((ushort)value)); return this; }
    /** Appends a little-endian 32-bit value. */
    public HircBytes U32(uint value) { data.AddRange(BitConverter.GetBytes(value)); return this; }
    /** Appends a little-endian signed 32-bit value. */
    public HircBytes S32(int value) { data.AddRange(BitConverter.GetBytes(value)); return this; }
    /** Appends a 32-bit float. */
    public HircBytes F32(float value) { data.AddRange(BitConverter.GetBytes(value)); return this; }
    /** Appends a 64-bit float. */
    public HircBytes F64(double value) { data.AddRange(BitConverter.GetBytes(value)); return this; }
    /** Appends the bytes unchanged. */
    public HircBytes Raw(byte[] value) { data.AddRange(value); return this; }
    /** Returns the bytes built so far. */
    public byte[] ToArray() => [.. data];
}

/** Version-113 object layouts, field by field, for the reader and composer checks. */
internal static class TestHirc
{
    public const uint Vorbis = 0x00040001;

    /** An RTPC curve: parameter, scaling and (x, y, shape) points. */
    public sealed record Curve(uint Id, byte Parameter, byte Scaling, (float X, float Y, uint Shape)[] Points);

    /** Reinterprets a float as the raw 32-bit property value. */
    public static uint F(float value) => BitConverter.SingleToUInt32Bits(value);

    /** Encodes a count-prefixed list of ids. */
    public static byte[] Ids(params uint[] ids) => [.. BitConverter.GetBytes(ids.Length), .. ids.SelectMany(BitConverter.GetBytes)];

    /** Encodes a property block of (id, raw value) pairs. */
    public static byte[] Props(params (byte Id, uint Raw)[] props)
    {
        var bytes = new HircBytes().U8(props.Length);
        foreach (var prop in props) bytes.U8(prop.Id);
        foreach (var prop in props) bytes.U32(prop.Raw);
        return bytes.ToArray();
    }

    /** Randomized ranges: the count, the IDs, then each minimum and maximum as raw bits. */
    public static byte[] Ranged(params (byte Id, uint Minimum, uint Maximum)[] ranges)
    {
        var bytes = new HircBytes().U8(ranges.Length);
        foreach (var range in ranges) bytes.U8(range.Id);
        foreach (var range in ranges) bytes.U32(range.Minimum).U32(range.Maximum);
        return bytes.ToArray();
    }

    /** Shared node parameters: effects, links, properties, ranges, states and game-parameter curves. */
    public static byte[] Node(uint bus, uint parent, (byte Id, uint Raw)[]? props = null, uint[]? effects = null,
        (uint Group, (uint State, uint Instance)[] States)[]? states = null, Curve[]? curves = null,
        (byte Id, uint Minimum, uint Maximum)[]? ranged = null, byte positioning = 0)
    {
        var bytes = new HircBytes().U8(0);
        effects ??= [];
        bytes.U8(effects.Length);
        if (effects.Length > 0)
        {
            bytes.U8(0);
            for (int index = 0; index < effects.Length; index++) bytes.U8(index).U32(effects[index]).U8(0).U8(0);
        }
        bytes.U8(0).U32(bus).U32(parent).U8(0).Raw(Props(props ?? [])).Raw(Ranged(ranged ?? []));
        // A 3D override carries the simplest 3D block; no auxiliary sends or advanced settings.
        bytes.U8(positioning);
        if ((positioning & 9) == 9) bytes.U8(1).U32(0);
        bytes.U8(0).Raw(new byte[6]);
        states ??= [];
        bytes.U32((uint)states.Length);
        foreach (var (group, values) in states)
        {
            bytes.U32(group).U8(0).U16(values.Length);
            foreach (var (state, instance) in values) bytes.U32(state).U32(instance);
        }
        return [.. bytes.ToArray(), .. Curves(curves ?? [])];
    }

    /** Encodes RTPC curves. */
    public static byte[] Curves(Curve[] curves)
    {
        var bytes = new HircBytes().U16(curves.Length);
        foreach (var curve in curves)
        {
            bytes.U32(curve.Id).U8(0).U8(1).U8(curve.Parameter).U32(77).U8(curve.Scaling).U16(curve.Points.Length);
            foreach (var (x, y, shape) in curve.Points) bytes.F32(x).F32(y).U32(shape);
        }
        return bytes.ToArray();
    }

    /** A codec source streams; a source plug-in keeps its parameters in the effect object named by the media ID. */
    public static byte[] Source(uint plugin, uint media)
    {
        var bytes = new HircBytes().U32(plugin).U8(plugin == Vorbis ? 2 : 0).U32(media).U32(100).U8(0);
        if ((plugin & 0xF) is 2 or 5) bytes.U32(0);
        return bytes.ToArray();
    }

    /** Builds the bytes of a sound object. */
    public static byte[] Sound(uint plugin, uint media, uint parent, uint bus = 0, (byte Id, uint Raw)[]? props = null,
        uint[]? effects = null, (uint Group, (uint State, uint Instance)[] States)[]? states = null, Curve[]? curves = null,
        (byte Id, uint Minimum, uint Maximum)[]? ranged = null, byte positioning = 0) =>
        [.. Source(plugin, media), .. Node(bus, parent, props, effects, states, curves, ranged, positioning)];

    /** A play action ends with its fade curve and target bank; delay bounds are integer milliseconds. */
    public static byte[] Action(ushort type, uint target, uint delayMs = 0, (int Minimum, int Maximum)? delayRange = null) =>
        [.. BitConverter.GetBytes(type), .. BitConverter.GetBytes(target), 0,
            .. delayMs == 0 ? Props() : Props((WwiseHierarchy.DelayProp, delayMs)),
            .. delayRange is { } range ? Ranged((WwiseHierarchy.DelayProp, unchecked((uint)range.Minimum), (uint)range.Maximum)) : Ranged(),
            4, .. BitConverter.GetBytes(900u)];

    /** Builds the bytes of a random or sequence container. */
    public static byte[] RandomSequence(uint parent, bool sequence, bool continuous, ushort loops, byte transitionMode,
        float transitionMs, uint[] items, (byte Id, uint Raw)[]? props = null)
    {
        var bytes = new HircBytes().Raw(Node(0, parent, props)).U16(loops).U16(0).U16(0)
            .F32(transitionMs).F32(0).F32(0).U16(1).U8(transitionMode).U8(0).U8(sequence ? 1 : 0).U8(continuous ? 8 : 0)
            .Raw(Ids(items)).U16(items.Length);
        foreach (uint item in items) bytes.U32(item).S32(50000);
        return bytes.ToArray();
    }

    /** Builds the bytes of a switch container. */
    public static byte[] Switch(uint parent, byte groupType, uint group, (uint Value, uint[] Nodes)[] values)
    {
        var children = values.SelectMany(value => value.Nodes).Distinct().ToArray();
        var bytes = new HircBytes().Raw(Node(0, parent)).U8(groupType).U32(group).U32(0).U8(0).Raw(Ids(children)).U32((uint)values.Length);
        foreach (var (value, nodes) in values) bytes.U32(value).Raw(Ids(nodes));
        bytes.U32((uint)children.Length);
        foreach (uint child in children) bytes.U32(child).U8(0).U8(0).S32(0).S32(0);
        return bytes.ToArray();
    }

    /** One layer whose cross-fade curve names the first child. */
    public static byte[] Layer(uint parent, uint[] children) =>
        new HircBytes().Raw(Node(0, parent)).Raw(Ids(children)).U32(1).U32(1).U16(0).U32(0).U8(0)
            .U32(1).U32(children[0]).U32(2).F32(0).F32(0).U32(4).F32(1).F32(0).U32(4).ToArray();

    /** Builds the bytes of a bus with the given volume. */
    public static byte[] Bus(uint output, float volumeDb) =>
        new HircBytes().U32(output).Raw(Props((WwiseHierarchy.BusVolumeProp, F(volumeDb))))
            .Raw(new byte[1 + 1 + 2 + 4 + 1 + 8]).U32(0).U8(0).Raw(new byte[4 + 1 + 1]).U16(0).U32(0).ToArray();

    private static HircBytes Music(uint parent, params uint[] children) =>
        new HircBytes().U8(0).Raw(Node(0, parent)).Raw(Ids(children)).F64(0).F64(0).F32(120).U8(4).U8(4).U8(0).U32(0);

    /** Entry marker at the start and a named exit marker at the end. */
    public static byte[] Segment(uint parent, uint track, double durationMs) =>
        Music(parent, track).F64(durationMs).U32(2).U32(1).F64(0).U32(0)
            .U32(2).F64(durationMs).U32(5).Raw("Exit\0"u8.ToArray()).ToArray();

    /** One clip that plays the whole source, with a two-point volume automation. */
    public static byte[] Track(uint parent, uint media, double durationMs) =>
        new HircBytes().U8(0).U32(1).Raw(Source(Vorbis, media))
            .U32(1).U32(0).U32(media).F64(0).F64(0).F64(0).F64(durationMs).U32(1)
            .U32(1).U32(0).U32(0).U32(2).F32(0).F32(0).U32(4).F32(1).F32(0).U32(4)
            .Raw(Node(0, parent)).U8(0).S32(0).ToArray();

    /** No transition rules; a one-level decision tree under a root. */
    public static byte[] MusicSwitch(uint parent, uint group, byte groupType, (uint Key, uint Node)[] leaves)
    {
        var bytes = Music(parent, leaves.Select(leaf => leaf.Node).Distinct().ToArray()).U32(0).U8(0).U32(1).U32(group).U8(groupType)
            .U32((uint)(12 * (1 + leaves.Length))).U8(0);
        // A branch's value packs its first child index and its child count.
        bytes.U32(0).U32(1u | ((uint)leaves.Length << 16)).U16(50).U16(100);
        foreach (var (key, node) in leaves) bytes.U32(key).U32(node).U16(50).U16(100);
        return bytes.ToArray();
    }

    /** One exit-cue rule for any source and destination, then a root of the given kind over one segment. */
    public static byte[] Playlist(uint parent, uint segment, short loops, int rootKind = 0)
    {
        var bytes = Music(parent, segment).U32(1).Raw(Ids(uint.MaxValue)).Raw(Ids(uint.MaxValue))
            .S32(0).U32(4).S32(0).U32(7).U32(0).U8(1)
            .S32(0).U32(4).S32(0).U32(0).U32(0).U16(0).U8(1).U8(0).U8(0);
        bytes.U32(2);
        bytes.U32(0).U32(10).U32(1).S32(rootKind).U16(1).U16(0).U16(0).U32(50000).U16(0).U8(0).U8(0);
        bytes.U32(segment).U32(11).U32(0).S32(-1).U16((ushort)loops).U16(0).U16(0).U32(50000).U16(0).U8(0).U8(0);
        return bytes.ToArray();
    }

    /** An effect or source plug-in's settings: plug-in ID, then a parameter block of floats. */
    public static byte[] Effect(uint plugin, params float[] parameters) =>
        new HircBytes().U32(plugin).U32((uint)(parameters.Length * 4)).Raw(parameters.SelectMany(BitConverter.GetBytes).ToArray())
            .U8(0).U16(0).U16(0).ToArray();

    /** Wraps the object records into a parsed sound bank. */
    public static AudioBank Bank(uint version, uint id, IEnumerable<(byte Type, uint Id, byte[] Data)> objects)
    {
        using var stream = new MemoryStream();
        void Chunk(string kind, byte[] data)
        {
            stream.Write(System.Text.Encoding.ASCII.GetBytes(kind)); stream.Write(BitConverter.GetBytes(data.Length)); stream.Write(data);
        }
        byte[] header = new byte[16];
        BitConverter.GetBytes(version).CopyTo(header, 0); BitConverter.GetBytes(id).CopyTo(header, 4);
        Chunk("BKHD", header);
        var list = objects.ToList();
        using var table = new MemoryStream();
        table.Write(BitConverter.GetBytes(list.Count));
        foreach (var (type, objectId, data) in list)
        { table.WriteByte(type); table.Write(BitConverter.GetBytes(data.Length + 4)); table.Write(BitConverter.GetBytes(objectId)); table.Write(data); }
        Chunk("HIRC", table.ToArray());
        return AudioCatalog.ReadBank(id, stream.ToArray());
    }
}
