using System.Buffers.Binary;
using SunriseStudio.Audio;

/** The managed event composer and its renderer on synthetic version-113 banks. */
internal static class ComposerChecks
{
    private const uint Bus = 800, Mixer = 300, Plain = 201, Looping = 202, Quieter = 203, Silence = 204, Generator = 205;
    private const uint Delayed = 206, Random = 400, Sequence = 401, Switch = 500, Layer = 600, Segment = 1000, Track = 1100;
    private const uint SilenceSettings = 700, GeneratorSettings = 701, StateGroup = 8000, StateValue = 8001, State = 1400;
    private const uint SwitchGroup = 7000, Parameter = 42, BusParameter = 43, Broken = 299;
    private const uint RangedVolume = 207, RangedDelay = 208, EmptyRandom = 402, RandomPlaylist = 1300;
    private const uint MusicSwitchId = 1500, MusicStateGroup = 9000;
    private const uint PannedLeft = 209, PannedChild = 210, Spatial = 211, PanMixer = 301;
    private const uint SilencePlugin = 0x00650002, SynthPlugin = 0x00940002;
    // Media IDs of the synthetic sources.
    private const uint MediaA = 5001, MediaB = 5002, MediaC = 5003, MediaMusic = 5004;

    /** Runs the checks that use generated fixtures instead of installed game content. */
    public static void Synthetic(string temporary)
    {
        var hierarchy = WwiseHierarchy.Read([TestHirc.Bank(113, 900, Objects())]);
        Check(hierarchy.Diagnostics.Count == 1 && hierarchy.Diagnostics[0].Contains($"object {Broken}", StringComparison.Ordinal),
            "The malformed sound was not reported by name.");
        var sources = new Dictionary<uint, PcmWave>
        {
            [MediaA] = Constant(4800, 1000), [MediaB] = Constant(2400, 2000), [MediaC] = Constant(4800, 1000), [MediaMusic] = Constant(9600, 500)
        };
        var all = new AudioCompositionOptions { AllRandomChoices = true };

        var plain = Single(hierarchy, 100);
        Check(Find<AudioEventSound>(plain) is { Media: MediaA, Loops: 1 } sound && Math.Abs(sound.GainDb + 3) < 1e-4,
            "Object and bus volumes were not summed.");
        var wave = Render(plain, sources, temporary, "plain");
        Check(Frames(wave) == 4800 && Math.Abs(Sample(wave, 100) - 1000 * Math.Pow(10, -3 / 20.0)) <= 1, "Plain sound rendered wrong.");

        var looping = Single(hierarchy, 110);
        Check(Find<AudioEventSound>(looping)?.Loops == 2 && Frames(Render(looping, sources, temporary, "loop")) == 4800,
            "Loop count was not applied.");

        Check(AudioEventComposer.Compose(hierarchy, 120).Count == 1, "A random container without all choices listed every item.");
        var randoms = AudioEventComposer.Compose(hierarchy, 120, all);
        Check(randoms.Count == 2 && randoms[0].Name.EndsWith("{r1}", StringComparison.Ordinal)
            && Find<AudioEventSound>(randoms[1].Plan)?.Media == MediaB, "Each random item was not its own composition.");

        // Gap transition: a 0.1 s item, a 0.1 s gap, then a 0.05 s item played twice.
        var sequence = Single(hierarchy, 130);
        Check(sequence.Root is AudioEventGroup { Items: [AudioEventGroup { Kind: AudioEventGroupKind.Sequence, TransitionMode: 3 }] },
            "Sequence container lost its transition.");
        Check(Frames(Render(sequence, sources, temporary, "sequence")) == 14400, "Sequence transition gap was not placed.");

        var switches = AudioEventComposer.Compose(hierarchy, 140);
        Check(switches.Count == 3 && switches[0].Routing.Contains("Same media, volume or delay may differ", StringComparison.Ordinal),
            "Switch values with the same media were not merged, or distinct ones were.");
        // A listed value with no children plays nothing; only an unlisted value uses the default.
        Check(switches[2].Name.Contains("=7004]", StringComparison.Ordinal) && Find<AudioEventSilence>(switches[2].Plan) is { Seconds: 0 },
            "A listed switch value with no children was not its own silent path.");
        var chosen = AudioEventComposer.Compose(hierarchy, 140, new() { GameSyncs = [new(true, SwitchGroup, 7002)] });
        Check(chosen.Count == 1 && Find<AudioEventSound>(chosen[0].Plan) is { Media: MediaA } quiet && Math.Abs(quiet.GainDb + 6) < 1e-4,
            "The chosen switch value was not used.");

        var layer = Single(hierarchy, 150);
        var layered = Render(layer, sources, temporary, "layer");
        // The plain sound is at -3 dB; the looping one has only the bus's -2 dB.
        double mix = 1000 * Math.Pow(10, -3 / 20.0) + 2000 * Math.Pow(10, -2 / 20.0);
        Check(Frames(layered) == 4800 && Math.Abs(Sample(layered, 100) - mix) <= 1 && Math.Abs(Sample(layered, 3000) - mix) <= 1,
            "Layer children were not mixed.");

        var silence = Single(hierarchy, 160);
        Check(Find<AudioEventSilence>(silence)?.Seconds == 0.5, "Silence did not keep its authored length.");
        var silent = Render(silence, sources, temporary, "silence");
        Check(Frames(silent) == 24000 && silent.Samples.All(value => value == 0), "Silence rendered sound.");

        var generator = Single(hierarchy, 170);
        Check(Find<AudioEventUnsupported>(generator)?.Reason == "Generator plug-in: needs the Wwise engine", "Generator plug-in was not refused.");
        Reject<NotSupportedException>(() => Render(generator, sources, temporary, "generator"));

        var choices = new AudioCompositionOptions { Variables = [new(Parameter, 0.5f)], States = [new(StateGroup, StateValue)] };
        var delayed = AudioEventComposer.Compose(hierarchy, 180, choices).Single().Plan;
        Check(Find<AudioEventSound>(delayed) is { } chained && Math.Abs(chained.GainDb + 13) < 1e-4,
            "Game parameter and state volumes were not applied.");
        Check(Frames(Render(delayed, sources, temporary, "delay")) == 4800 + 24000, "Initial delay was dropped.");
        var undelayed = AudioEventComposer.Compose(hierarchy, 180, choices with { IncludeInitialDelay = false }).Single().Plan;
        Check(Frames(Render(undelayed, sources, temporary, "nodelay")) == 4800, "Initial delay was kept when turned off.");

        var music = Single(hierarchy, 190);
        Check(Find<AudioEventMusic>(music)?.Timeline.Clips.Single().Media == MediaMusic, "Music segment did not use the timeline.");
        var musical = Render(music, sources, temporary, "music");
        Check(Frames(musical) == 9600 && Math.Abs(Sample(musical, 100) - 500) <= 1, "Music segment rendered wrong.");

        Check(Find<AudioEventUnsupported>(Single(hierarchy, 195))?.Reason.Contains("not in the loaded banks", StringComparison.Ordinal) == true,
            "A skipped object did not become a refusal.");

        // A part that plays nothing is silent; the sound beside it still plays.
        var plainOnly = 1000 * Math.Pow(10, -3 / 20.0);
        foreach (var (eventId, choice) in new[] { (2000u, new AudioCompositionOptions()), (2020u, new AudioCompositionOptions()),
            (2030u, new AudioCompositionOptions { GameSyncs = [new(true, SwitchGroup, 7004)] }) })
        {
            var quietPart = AudioEventComposer.Compose(hierarchy, eventId, choice).Single().Plan;
            var heard = Render(quietPart, sources, temporary, "nothing-" + eventId);
            Check(Find<AudioEventSilence>(quietPart)?.Label.EndsWith("plays nothing", StringComparison.Ordinal) == true
                && Frames(heard) == 4800 && Math.Abs(Sample(heard, 100) - plainOnly) <= 1, $"Event {eventId} lost the sound beside a silent part.");
        }

        // Ranges add their midpoints: volume -1 dB plus (-4..0) dB, and delay 0.5 s plus (0..0.5) s.
        var rangedVolume = Single(hierarchy, 2040);
        Check(Find<AudioEventSound>(rangedVolume) is { } ranged && Math.Abs(ranged.GainDb + 5) < 1e-4
            && rangedVolume.Notices.Contains("Randomized ranges use their midpoint."), "A volume range was not applied at its midpoint.");
        var rangedDelay = Single(hierarchy, 2050);
        Check(Frames(Render(rangedDelay, sources, temporary, "ranged-delay")) == 4800 + 36000, "A delay range was not applied at its midpoint.");

        // The mixer picks the output bus, so its output-bus curve applies; 3 clamps to 1, +6.02 dB.
        var holder = AudioEventComposer.Compose(hierarchy, 100, new() { Variables = [new(BusParameter, 0.5f)] }).Single().Plan;
        Check(Find<AudioEventSound>(holder) is { } routed && Math.Abs(routed.GainDb - (-3 + 20 * Math.Log10(2))) < 1e-4,
            "The output-bus volume curve of the bus holder was not applied.");

        // A random step playlist plays one pick instead of being refused.
        var picked = Single(hierarchy, 2060);
        Check(Find<AudioEventMusic>(picked) is not null && picked.Notices.Contains("Random: one pick, not the game seed")
            && Frames(Render(picked, sources, temporary, "random-step")) == 9600, "A random step playlist was not composed.");
        // A music switch path that ends in no node switches the music to nothing; it is its own silent path.
        var musicPaths = AudioEventComposer.Compose(hierarchy, 2070);
        Check(musicPaths.Count == 2 && Find<AudioEventMusic>(musicPaths[0].Plan) is not null
            && musicPaths[1].Name.Contains("(9000=9002)", StringComparison.Ordinal) && Find<AudioEventSilence>(musicPaths[1].Plan) is { Seconds: 0 },
            "A music switch path to no node was dropped or not silent.");
        // Pan -100 with the panner on keeps only the left channel; the bus's -2 dB still applies.
        double busOnly = 1000 * Math.Pow(10, -2 / 20.0);
        var left = Render(Single(hierarchy, 2080), sources, temporary, "pan-left");
        Check(Math.Abs(Sample(left, 100) - busOnly) <= 1 && Right(left, 100) == 0, "The 2D pan law was not applied.");
        // The pan comes from the nearest node that overrides positioning, not from the sound's own property.
        var inherited = Render(Single(hierarchy, 2090), sources, temporary, "pan-inherited");
        Check(Sample(inherited, 100) == 0 && Math.Abs(Right(inherited, 100) - busOnly) <= 1, "The positioning owner's pan was not used.");
        // A 3D sound plays centred, with a note.
        var spatial = Single(hierarchy, 2100);
        var centred = Render(spatial, sources, temporary, "pan-3d");
        Check(Math.Abs(Sample(centred, 100) - busOnly) <= 1 && Math.Abs(Right(centred, 100) - busOnly) <= 1
            && spatial.Notices.Contains("3D positioning is not applied; the sound plays centred."), "A 3D sound was panned or not noted.");
        Reject<InvalidDataException>(() => AudioEventComposer.Compose(WwiseHierarchy.Read([TestHirc.Bank(112, 901, Objects())]), 100));
        Check(new AudioCompositionOptions { LoopCount = 2 }.Key != new AudioCompositionOptions().Key
            && choices.Key == (choices with { }).Key, "Choice keys do not follow the choices.");
        Console.WriteLine("audio composer=v113 containers=checked silence=authored generator=refused choices=applied render=checked");
    }

    /** Twenty-two events over a mixer routed to one bus; three of them have a silent part. */
    private static IEnumerable<(byte Type, uint Id, byte[] Data)> Objects()
    {
        (uint Event, uint Target)[] events =
            [(100, Plain), (110, Looping), (120, Random), (130, Sequence), (140, Switch), (150, Layer), (160, Silence),
             (170, Generator), (180, Delayed), (190, Segment), (195, Broken), (2040, RangedVolume), (2050, RangedDelay),
             (2060, RandomPlaylist), (2010, Plain), (2070, MusicSwitchId), (2080, PannedLeft), (2090, PannedChild), (2100, Spatial)];
        foreach (var (id, target) in events)
        {
            yield return (4, id, TestHirc.Ids(id + 1));
            // Event 2010 only stops a sound, so it has no play action.
            yield return (3, id + 1, TestHirc.Action(id == 2010 ? (ushort)0x0102 : (ushort)0x0403, target));
        }
        // Two play actions each: a sound, then a part that plays nothing.
        (uint Event, ushort Type, uint Target)[] pairs = [(2000, 0x2103, 2010), (2020, 0x0403, EmptyRandom), (2030, 0x0403, Switch)];
        foreach (var (id, type, target) in pairs)
        {
            yield return (4, id, TestHirc.Ids(id + 1, id + 2));
            yield return (3, id + 1, TestHirc.Action(0x0403, Plain));
            yield return (3, id + 2, TestHirc.Action(type, target));
        }
        yield return (8, Bus, TestHirc.Bus(0, -2));
        // Scaling 2 holds linear gain minus one; 3 is out of range.
        yield return (7, Mixer, [.. TestHirc.Node(Bus, 0, curves: [new(BusParameter, 0x3C, 2, [(0, 3, 4), (1, 3, 4)])]),
            .. TestHirc.Ids(Plain, Looping, Quieter, Silence, Generator, Delayed, RangedVolume, RangedDelay)]);
        yield return (2, Plain, TestHirc.Sound(TestHirc.Vorbis, MediaA, Mixer, props: [(WwiseHierarchy.VolumeProp, TestHirc.F(-1))]));
        yield return (2, Looping, TestHirc.Sound(TestHirc.Vorbis, MediaB, Mixer, props: [(WwiseHierarchy.LoopProp, 2)]));
        yield return (2, Quieter, TestHirc.Sound(TestHirc.Vorbis, MediaA, Mixer, props: [(WwiseHierarchy.VolumeProp, TestHirc.F(-4))]));
        yield return (2, Silence, TestHirc.Sound(SilencePlugin, SilenceSettings, Mixer));
        yield return (19, SilenceSettings, TestHirc.Effect(SilencePlugin, 0.5f, 0, 0));
        yield return (2, Generator, TestHirc.Sound(SynthPlugin, GeneratorSettings, Mixer));
        yield return (19, GeneratorSettings, TestHirc.Effect(SynthPlugin, 1));
        // Initial delay is property 59, in seconds.
        yield return (2, Delayed, TestHirc.Sound(TestHirc.Vorbis, MediaC, Mixer, props: [(59, TestHirc.F(0.5f))],
            states: [(StateGroup, [(StateValue, State)])], curves: [new(Parameter, 0, 0, [(0, 0, 4), (1, -12, 4)])]));
        yield return (1, State, TestHirc.Props((WwiseHierarchy.VolumeProp, TestHirc.F(-5))));
        yield return (2, RangedVolume, TestHirc.Sound(TestHirc.Vorbis, MediaA, Mixer, props: [(WwiseHierarchy.VolumeProp, TestHirc.F(-1))],
            ranged: [(WwiseHierarchy.VolumeProp, TestHirc.F(-4), TestHirc.F(0))]));
        yield return (2, RangedDelay, TestHirc.Sound(TestHirc.Vorbis, MediaC, Mixer, props: [(59, TestHirc.F(0.5f))],
            ranged: [(59, TestHirc.F(0), TestHirc.F(0.5f))]));
        yield return (5, Random, TestHirc.RandomSequence(Mixer, false, false, 1, 0, 0, [Plain, Looping]));
        yield return (5, Sequence, TestHirc.RandomSequence(Mixer, true, true, 1, 3, 100, [Plain, Looping]));
        yield return (5, EmptyRandom, TestHirc.RandomSequence(Mixer, false, false, 1, 0, 0, []));
        // Value 7004 is assigned no children.
        yield return (6, Switch, TestHirc.Switch(Mixer, 0, SwitchGroup, [(7001, [Plain]), (7002, [Quieter]), (7003, [Looping]), (7004, [])]));
        yield return (9, Layer, TestHirc.Layer(Mixer, [Plain, Looping]));
        yield return (10, Segment, TestHirc.Segment(0, Track, 200));
        yield return (11, Track, TestHirc.Track(Segment, MediaMusic, 200));
        // Kind 3 is a random step root.
        yield return (13, RandomPlaylist, TestHirc.Playlist(0, Segment, 1, 3));
        // Positioning bits: 1 overrides the parent, 2 and 4 together enable the 2D panner, 8 is 3D. Property 11 is the pan.
        yield return (2, PannedLeft, TestHirc.Sound(TestHirc.Vorbis, MediaA, Mixer, props: [(11, TestHirc.F(-100))], positioning: 7));
        yield return (7, PanMixer, [.. TestHirc.Node(0, Mixer, props: [(11, TestHirc.F(100))], positioning: 7), .. TestHirc.Ids(PannedChild)]);
        yield return (2, PannedChild, TestHirc.Sound(TestHirc.Vorbis, MediaA, PanMixer, props: [(11, TestHirc.F(-100))]));
        yield return (2, Spatial, TestHirc.Sound(TestHirc.Vorbis, MediaA, Mixer, props: [(11, TestHirc.F(-100))], positioning: 9));
        // State value 9002 leads to no node.
        yield return (12, MusicSwitchId, TestHirc.MusicSwitch(0, MusicStateGroup, 1, [(9001, Segment), (9002, 0)]));
        // One byte too many: the reader skips this sound and names it.
        yield return (2, Broken, [.. TestHirc.Sound(TestHirc.Vorbis, MediaA, Mixer), 0]);
    }

    private static AudioEventPlan Single(WwiseHierarchy hierarchy, uint eventId) =>
        AudioEventComposer.Compose(hierarchy, eventId).Single().Plan;

    private static T? Find<T>(AudioEventPlan plan) where T : AudioEventNode
    {
        T? Visit(AudioEventNode node) => node as T ?? (node is AudioEventGroup group ? group.Items.Select(Visit).FirstOrDefault(found => found is not null) : null);
        return Visit(plan.Root);
    }

    private static PcmWave Render(AudioEventPlan plan, IReadOnlyDictionary<uint, PcmWave> sources, string temporary, string name)
    {
        string path = Path.Combine(temporary, "composer-" + name + ".wav");
        AudioEventRender.Render(plan, sources, path);
        return PcmWave.Read(path);
    }

    /** Stereo, so the mix holds the value without the mono centre gain. */
    private static PcmWave Constant(int frames, short value)
    {
        var samples = new byte[frames * 4];
        for (int i = 0; i < frames * 2; i++) BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(i * 2), value);
        return new(48000, 2, 16, samples);
    }

    private static int Frames(PcmWave wave) => wave.Samples.Length / wave.BlockAlign;
    private static short Sample(PcmWave wave, int frame) => BinaryPrimitives.ReadInt16LittleEndian(wave.Samples.AsSpan(frame * wave.BlockAlign));
    private static short Right(PcmWave wave, int frame) => BinaryPrimitives.ReadInt16LittleEndian(wave.Samples.AsSpan(frame * wave.BlockAlign + 2));

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidDataException($"Expected {typeof(T).Name} was not thrown.");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
