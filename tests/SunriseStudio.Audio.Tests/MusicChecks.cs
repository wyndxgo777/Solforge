using System.Buffers.Binary;
using SunriseStudio.Audio;
using SunriseStudio.Packages;

/** Checks music track source lookup, entry/exit markers, PCM overlap mixing and choice identity, which the transition renderer depends on. */
internal static class MusicChecks
{
    /** A two-segment owned fixture makes overlapping samples independently predictable. */
    public static void Synthetic(string directory)
    {
        TransitionChecks.Synthetic(directory);
        AutomationChecks.Synthetic(directory);
        byte[] source = new byte[19]; BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(1), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(5), 0x00040001); source[9] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(10), 77);
        var bank = new AudioBank(1, 2, 113, [], [new(11, 3, source)], []);
        Check(AudioSources.Read(bank).Single() is { MediaId: 77, External: true }, "Music track source was missed.");
        Reject(() => AudioSources.Read(bank with { Objects = [new(11, 3, source[..^1])] }));
        var analysis = new TestMusic()
            .Track(10, 20, TestMusic.Clip(11, 3000)).Track(12, 21, TestMusic.Clip(13, 2000))
            .Playlist(30, 0, TestMusic.Item(100, 0, 0, 1, TestMusic.Item(101, 20, -1), TestMusic.Item(102, 21, -1)),
                TestMusic.Rule(sync: 7, postExit: true, preEntry: true))
            .Segment(20, 30, 3000, [new(0, 500), new(0, 2000)], 10).Segment(21, 30, 2000, [new(0, 500), new(0, 1500)], 12)
            .Build();
        var plan = AudioTimeline.Build(analysis, 30);
        Check(plan.Segments.Count == 2 && Math.Abs(plan.Segments[1].StartSeconds - 1.5) < 1e-9
            && Math.Abs(plan.DurationSeconds - 3.5) < 1e-9, "Entry and exit markers did not overlap segments.");
        var rendered = AudioTimeline.Render(plan, new Dictionary<uint, PcmWave> { [11] = Tone(3, 1000), [13] = Tone(2, 2000) },
            Path.Combine(directory, "music.wav"));
        var wave = PcmWave.Read(rendered.Path);
        short At(double seconds) => BinaryPrimitives.ReadInt16LittleEndian(wave.Samples.AsSpan((int)(seconds * 48000) * 4));
        Check(At(1) == 1000 && At(2) == 3000 && At(3.2) == 2000 && rendered.OverlapFrames == 72000,
            "PCM overlap mixed or aligned the wrong samples.");
        var choices = new AudioCompositionOptions { Variables = [new(42, .5f)], GameSyncs = [new(false, 12, 34)], LoopCount = 3 };
        Check(choices.Key == (choices with { }).Key
            && choices.Key != (choices with { Variables = [new(42, MathF.BitIncrement(.5f))] }).Key
            && choices.Key != (choices with { GameSyncs = [new(false, 12, 35)] }).Key
            && choices.Key != (choices with { LoopCount = 4 }).Key, "Audio choices lost precision or cache identity.");
        Reject(() => (choices with { Variables = [new(1, float.NaN)] }).Validate());
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { AudioTimeline.Render(plan, new Dictionary<uint, PcmWave>(), Path.Combine(directory, "canceled.wav"), canceled.Token); throw new Exception("Timeline cancellation ignored."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("audio_music synthetic=pass sources=music-track markers=overlap pcm=exact choices=checked");
    }

    /** Installed music has three authored playlists and pre-entry/post-exit material the timeline keeps. */
    public static async Task Owned(PackageCatalog catalog, string directory, string? output = null)
    {
        using var session = new AudioSession(catalog, 0x80B4F00D, cacheDirectory: Path.Combine(directory, "music-cache"));
        Console.WriteLine($"audio_music discovered={session.Document.Media.Count} diagnostics={string.Join(" | ", session.Document.Diagnostics)}");
        Check(session.Document.Media.Count == 39 && session.Document.Diagnostics.Count == 0, "Music-only bank failed media discovery.");
        await session.Analyze();
        var analysis = session.Analysis!;
        Check(analysis.Tracks.Count == 60 && analysis.Segments.Count == 59 && analysis.Playlists.Count == 3,
            "Installed music routing coverage changed.");
        Check(analysis.GameSyncs.Any(group => group.Id == 2923559205 && group.Choices.Any(value => value.Value == 1341174422)),
            "Authored music state choice is missing.");
        // One loop of the endless random item plays each of its six children once.
        var rendered = await session.RenderTimeline(958553257, 1, 0);
        Check(session.Timeline!.Segments.Select(segment => segment.Id).SequenceEqual(
            new uint[] { 93887718, 93887718, 102189870, 406586311, 82986931, 758479771, 430981145 }),
            "Playlist's repeatable branch policy changed.");
        Check(Math.Abs(session.Timeline.DurationSeconds - 230) < .0001 && rendered.OverlapFrames == 60 * 48000,
            "Installed cue tails or lead-ins were truncated.");
        var wave = PcmWave.Read(rendered.Path);
        Check(wave.Samples.Any(value => value != 0) && Math.Abs(wave.DurationSeconds - 230) < .0001, "Installed timeline did not render its full audio.");
        if (output is not null)
        {
            if (!Path.GetExtension(output).Equals(".wav", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Music fixture output needs a .wav extension.");
            AudioSession.ExportWave(rendered.Path, output, catalog.GameRoot);
            File.WriteAllText(Path.ChangeExtension(output, ".json"), System.Text.Json.JsonSerializer.Serialize(session.Timeline,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        // Compositions belong to an event; 80B4F00E plays this bank's music.
        using var eventSession = new AudioSession(catalog, 0x80B4F00E, cacheDirectory: Path.Combine(directory, "music-event-cache"));
        var choices = new AudioCompositionOptions { GameSyncs = [new(false, 2923559205, 1341174422)], LoopCount = 2 };
        await eventSession.GenerateCompositions(choices);
        var chosen = eventSession.Compositions;
        Check(chosen.Count == 1 && chosen[0].Routing.Contains("State 2923559205 = 1341174422\n", StringComparison.Ordinal)
            && chosen[0].Routing.Contains("Music playlist 958553257", StringComparison.Ordinal),
            "State selection did not reach generated routing.");
        await eventSession.GenerateCompositions(choices with { GameSyncs = [new(false, 2923559205, 1197836086), new(false, 3356667444, 979470758)] });
        var changed = eventSession.Compositions;
        Check(changed.Count == 1 && changed[0].Routing.Contains("State 2923559205 = 1197836086\n", StringComparison.Ordinal)
            && !changed[0].Plan.Media().SequenceEqual(chosen[0].Plan.Media()), "Changing an authored state reused the old composition.");
        Console.WriteLine($"audio_music owned=80B4F00D media=39 segments=59 playlists=3 timeline_seconds=230 overlap_seconds=60 clipped={rendered.ClippedSamples} state_choices=pass");
    }

    private static PcmWave Tone(int seconds, short value)
    {
        var samples = new byte[seconds * 48000 * 4];
        for (int i = 0; i < samples.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(i), value);
        return new(48000, 2, 16, samples);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return; } throw new Exception("Invalid music data accepted."); }
}

/** Music routing models in the shape the bank reader builds: every node has a route, every music container a rule list. */
internal sealed class TestMusic
{
    private readonly Dictionary<uint, AudioTrack> tracks = [];
    private readonly Dictionary<uint, AudioSegment> segments = [];
    private readonly Dictionary<uint, AudioPlaylist> playlists = [];
    private readonly Dictionary<uint, AudioTransitionContainer> containers = [];
    private readonly Dictionary<uint, AudioRoute> routes = [];
    private readonly Dictionary<uint, AudioMeter> meters = [];

    /** The renderer's per-channel placement: the pan law when the panner is on, else mono at -3 dB and stereo unchanged. */
    public static double ChannelGain(AudioTimelineClip clip, int sourceChannels, int channel) =>
        clip.Pan is { } x ? (channel == 0 ? MathF.Sqrt(1 - x) : MathF.Sqrt(x))
            : sourceChannels == 1 ? AudioTimeline.MonoToStereoGain : 1;

    /** Adds a music switch container with an optional meter and transition rules. */
    public TestMusic Switch(uint id, uint parent, AudioMeter? meter = null, params AudioTransition[] rules)
    {
        Route(id, parent);
        containers[id] = new(id, "Audio object " + id, rules);
        if (meter is not null) meters[id] = meter;
        return this;
    }

    /** Adds a music playlist container. */
    public TestMusic Playlist(uint id, uint parent, AudioPlaylistItem root, params AudioTransition[] rules)
    {
        Route(id, parent);
        containers[id] = new(id, "Playlist " + id, rules);
        playlists[id] = new(id, "Playlist " + id, parent, 0, root, rules, []);
        return this;
    }

    /** The first cue is the entry and the last the exit, as the bank's markers are. */
    public TestMusic Segment(uint id, uint parent, double durationMs, AudioCue[] cues, params uint[] children)
    {
        Route(id, parent);
        segments[id] = new(id, "Segment " + id, parent, 0, durationMs, cues[0].PositionMs, cues[^1].PositionMs, children, [], cues);
        return this;
    }

    /** Adds a music track with its clips. */
    public TestMusic Track(uint id, uint parent, params AudioClip[] clips) => Track(id, parent, clips, []);

    /** Adds a music track with its clips. */
    public TestMusic Track(uint id, uint parent, AudioClip[] clips, AudioClipAutomation[] automation)
    {
        Route(id, parent);
        tracks[id] = new(id, "Track " + id, parent, 0, 0, clips, [], automation);
        return this;
    }

    /** The dictionaries stay mutable so a check can swap one row in place. */
    public AudioAnalysis Build() => new()
    {
        Tracks = tracks, Segments = segments, Playlists = playlists, TransitionContainers = containers, Routes = routes, Meters = meters
    };

    /** Makes a clip with play-at and trim times in milliseconds. */
    public static AudioClip Clip(uint source, double durationMs, double playAtMs = 0, double beginTrimMs = 0, double endTrimMs = 0) =>
        new(source, 0, playAtMs, beginTrimMs, endTrimMs, durationMs);

    /** Makes a playlist item for a segment. */
    public static AudioPlaylistItem Item(uint id, uint segment, int kind, int loops = 1, params AudioPlaylistItem[] children) =>
        new(id, segment, kind, loops, 1, 0, false, children);

    /** Unset fields are the zero a bank rule holds; any source and any destination by default. */
    public static AudioTransition Rule(uint source = uint.MaxValue, uint destination = uint.MaxValue, int sync = 0, int entry = 0,
        double fadeOutMs = 0, int fadeOutCurve = 0, double fadeOutOffsetMs = 0, bool postExit = false,
        double fadeInMs = 0, int fadeInCurve = 0, double fadeInOffsetMs = 0, bool preEntry = false,
        uint jump = 0, uint destinationCue = 0, bool matchCue = false, AudioBridge? bridge = null) =>
        new(source, destination, sync, entry, fadeOutMs, fadeInMs, fadeOutCurve, fadeInCurve, fadeOutOffsetMs, fadeInOffsetMs,
            postExit, preEntry, bridge?.Segment ?? 0, jump, bridge, 0, destinationCue, matchCue);

    private void Route(uint id, uint parent) => routes[id] = new(id, parent, 0, 0, false, []);
}
