using System.Buffers.Binary;
using SunriseStudio.Audio;
using SunriseStudio.Packages;

/** Checks music transition destinations (entry cue, matching source, custom cue, same-time scheduling) so the chosen start position matches native behavior. */
internal static class DestinationChecks
{
    /** One rule on switch 100 from segment 10 into segment 20, which sits in playlist 200 as leaf item 999. */
    public static AudioAnalysis Fixture(int kind = 1, int sync = 0,
        int fade = 500, int offset = 0, uint filter = 0, bool match = false, uint jump = 0, bool bridge = false)
    {
        var rule = TestMusic.Rule(sync: sync, entry: kind, fadeInMs: fade, fadeInCurve: 4, fadeInOffsetMs: offset, preEntry: true,
            jump: jump, destinationCue: filter, matchCue: match, bridge: bridge ? new(30, 0, 4, 0, 0, 4, 0, true, true) : null);
        return new TestMusic()
            .Switch(100, 0, null, rule)
            .Playlist(200, 100, TestMusic.Item(500, 0, 0, 1, TestMusic.Item(999, 20, 0)))
            .Segment(10, 100, 10000, [new(11, 1000), new(42, 2000), new(12, 9000)], 1).Track(1, 10, TestMusic.Clip(101, 10000))
            .Segment(20, 200, 8000, [new(21, 2000), new(42, 3000), new(43, 4000), new(42, 5000), new(22, 6000)], 2)
            .Track(2, 20, TestMusic.Clip(102, 8000))
            .Segment(30, 100, 3000, [new(31, 500), new(32, 2500)], 3).Track(3, 30, TestMusic.Clip(103, 3000))
            .Build();
    }

    /** Runs the checks that use generated fixtures instead of installed game content. */
    public static void Synthetic(string directory)
    {
        var analysis = Fixture(); var segment = analysis.Segments[20];
        Check(AudioDestination.Resolve(segment, 1, 5 * 48000).FrameFromEntry == 48000
            && !AudioDestination.Resolve(segment, 1, 0).AllowsPreEntry, "Same-time wrapping or pre-entry gating differs.");
        Check(AudioDestination.Resolve(segment, 2, cueFilter: 42, variation: 0).Cue == 21
            && AudioDestination.Resolve(segment, 2, cueFilter: 42, variation: 1).FrameFromEntry == 48000
            && AudioDestination.Resolve(segment, 3, sourceCue: 42, matchSourceCue: true, variation: 1).FrameFromEntry == 144000,
            "Named destination choices lost their entry-cue or matching-source behavior.");
        Check(AudioDestination.Resolve(segment, 3, cueFilter: 999).Cue == 21, "Missing custom destination did not fall back to entry.");
        var same = AudioTimeline.BuildTransition(analysis, 100, 10, 20, 5);
        Check(same.Destination is { FrameFromEntry: 48000, Kind: 1 } && same.Segments[1].StartSeconds == 6
            && same.Clips.Single(clip => clip.Segment == 20).SourceSeconds == 3 && same.DurationSeconds == 11,
            "Same-time scheduling did not skip the destination prefix and align its selected position.");
        var sources = new Dictionary<uint, PcmWave> { [101] = Wave(10, false), [102] = Wave(8, true), [103] = Wave(3, false) };
        var rendered = AudioTimeline.Render(same, sources, Path.Combine(directory, "same-time.wav"));
        var pcm = PcmWave.Read(rendered.Path);
        Check(At(pcm, 5.5) == 1000 && At(pcm, 6.25) == 2000 && At(pcm, 7) == 5000,
            "Same-time PCM read the wrong source interval or fade.");

        analysis = Fixture(3, 4, 1000, -500, match: true, jump: 999);
        var named = AudioTimeline.BuildTransition(analysis, 100, 10, 20, .5, 1);
        Check(named.Synchronization is { Cue: 42, FrameFromEntry: 48000 } && named.Destination is { Cue: 42, FrameFromEntry: 144000, ChoiceCount: 2 }
            && named.Segments[1].StartSeconds == 1.5 && named.DurationSeconds == 5,
            "Named destination did not retain its jump, cue match and negative fade offset.");
        rendered = AudioTimeline.Render(named, sources, Path.Combine(directory, "named-destination.wav")); pcm = PcmWave.Read(rendered.Path);
        Check(At(pcm, 1.25) == 1000 && At(pcm, 1.75) == 2250 && At(pcm, 2) == 3000 && At(pcm, 2.5) == 6000,
            "Named destination overlap did not produce the exact authored PCM envelope.");
        var bridged = AudioTimeline.BuildTransition(Fixture(bridge: true, jump: 999), 100, 10, 20, 5);
        Check(bridged.Synchronization is { FrameFromEntry: 264000 } && bridged.Destination is { FrameFromEntry: 72000 }
            && bridged.Segments[1].StartSeconds == 6 && bridged.Segments[2].StartSeconds == 8.5,
            "Bridge lead-in or source-cursor preservation changed same-time entry.");
        var earlyFade = AudioTimeline.BuildTransition(Fixture(0, fade: 1000, offset: -3000), 100, 10, 20, 3);
        Check(earlyFade.Synchronization is { FrameFromEntry: 288000 } && earlyFade.Segments[1].StartSeconds == 5
            && earlyFade.Clips.Single(value => value.Segment == 20).FadeIn is { StartSeconds: 4 },
            "A fade before the file's first audio did not reserve its full authored lead-in.");
        Reject(() => AudioTimeline.BuildTransition(Fixture(jump: 12345), 100, 10, 20, 5));
        Reject(() => AudioTimeline.BuildTransition(Fixture(jump: 500), 100, 10, 20, 5));
        Reject(() => AudioDestination.Resolve(segment, 4));
        Console.WriteLine("audio_destination synthetic=pass same_time=wrapped named_cues=matched leaf_jump=checked lead_in=after_request pcm=exact");
    }

    /** Runs the same kind of checks against the real packages of the owned installation. */
    public static async Task Owned(PackageCatalog catalog, string directory, string? output)
    {
        using var session = new AudioSession(catalog, 0x80BBEE11, cacheDirectory: Path.Combine(directory, "destination-cache"));
        var rendered = await session.RenderTransition(598869076, 615101587, 861492828, 8);
        var plan = session.Timeline!;
        var exact = AudioTimeline.BuildTransition(session.Analysis!, 598869076, 615101587, 861492828, 5);
        Check(exact.Synchronization is { FrameFromEntry: 240000 }, "Same-time source synchronization skipped an exact beat boundary.");
        Check(plan.Synchronization is { FrameFromEntry: 400000, Kind: 3 }
            && plan.Destination is { FrameFromEntry: 400000, Kind: 1 }, "Installed same-time rule lost its beat or destination position.");
        var destination = plan.Clips.Where(clip => clip.Segment == 861492828).ToArray();
        Check(destination.Length > 0 && Math.Abs(plan.Segments[^1].StartSeconds - 35.0 / 3) < 1e-8
            && destination.All(clip => clip.StartSeconds >= plan.Segments[^1].StartSeconds),
            "Installed same-time entry played the destination prefix.");
        VerifySameTimeSamples(session, rendered, plan);
        if (output is not null) Export(rendered, plan, output);
        var bridge = await session.RenderTransition(598869076, 295650803, 989625971, 8);
        var bridgePlan = session.Timeline!;
        Check(bridgePlan.Segments.Select(row => row.Id).SequenceEqual(new uint[] { 295650803, 896159909, 989625971 })
            && bridgePlan.Destination!.FrameFromEntry == bridgePlan.Synchronization!.FrameFromEntry
            && bridgePlan.Segments[1].StartSeconds >= 8 && bridgePlan.Segments[2].StartSeconds >= bridgePlan.Segments[1].EntrySeconds,
            "Installed bridge lost its lead-in or replaced the source's same-time cursor.");
        Check(PcmWave.Read(bridge.Path).Samples.Any(value => value != 0), "Installed same-time bridge rendered silence.");
        if (output is not null) Export(bridge, bridgePlan, Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-bridge.wav"));
        Console.WriteLine($"audio_destination owned=80BBEE11 same_frame={plan.Destination!.FrameFromEntry} bridge_frame={bridgePlan.Destination!.FrameFromEntry} pcm=checked");
        void Export(AudioTimelineRender render, AudioTimelinePlan timeline, string path)
        {
            AudioSession.ExportWave(render.Path, path, catalog.GameRoot);
            File.WriteAllText(Path.ChangeExtension(path, ".json"), System.Text.Json.JsonSerializer.Serialize(timeline,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void VerifySameTimeSamples(AudioSession session, AudioTimelineRender rendered, AudioTimelinePlan plan)
    {
        var mixed = PcmWave.Read(rendered.Path);
        Check(mixed.Samples.Any(value => value != 0), "Installed destination is silent.");
        foreach (var clip in plan.Clips) Check(clip.LoopSeconds == 0, "Fixture source unexpectedly loops.");
        var sources = plan.Clips.Select(value => value.Media).Distinct().ToDictionary(id => id,
            id => WemDecoder.DecodeSamples(File.ReadAllBytes(session.MediaPath(id)), 2, CancellationToken.None));
        double sync = plan.Segments[^1].StartSeconds;
        foreach (double sampleTime in new[] { sync + .001, sync + .025, sync + .05, sync + .099, sync + .2 })
        {
            int frame = (int)Math.Round(sampleTime * 48000); double time = frame / 48000.0;
            for (int channel = 0; channel < 2; channel++)
            {
                float sum = 0;
                foreach (var clip in plan.Clips)
                {
                    int start = (int)Math.Round(clip.StartSeconds * 48000), count = (int)Math.Round(clip.DurationSeconds * 48000);
                    if (frame < start || frame >= start + count) continue;
                    long clock = frame - start + (long)Math.Round(clip.AutomationOffsetSeconds * 48000);
                    foreach (var lane in clip.Automation ?? []) Check(new AudioAutomationCurve(lane).GainAt(clock) == 1,
                        "Installed sample no longer isolates its transition envelope.");
                    double factor = 1;
                    if (clip.FadeIn is { } fadeIn) { Check(fadeIn.Curve == 4, "Fixture fade-in curve changed."); factor *= Math.Clamp((time - fadeIn.StartSeconds) / fadeIn.DurationSeconds, 0, 1); }
                    if (clip.FadeOut is { } fadeOut) { Check(fadeOut.Curve == 4, "Fixture fade-out curve changed."); factor *= 1 - Math.Clamp((time - fadeOut.StartSeconds) / fadeOut.DurationSeconds, 0, 1); }
                    // The source offset is a whole output sample.
                    long step = (long)Math.Round(clip.SourceSeconds * 48000, MidpointRounding.AwayFromZero) + frame - start;
                    var wave = sources[clip.Media]; double position = step * (double)wave.SampleRate / 48000;
                    int at = (int)Math.Floor(position), input = Math.Min(channel, wave.Channels - 1);
                    float a = wave.Samples[at * wave.Channels + input];
                    float b = wave.Samples[(at + 1) * wave.Channels + input];
                    double centre = TestMusic.ChannelGain(clip, wave.Channels, channel);
                    sum += (float)((a + (b - a) * (position - at)) * Math.Pow(10, clip.GainDb / 20.0) * centre * factor);
                }
                int expected = (int)Math.Clamp(Math.Round(sum), short.MinValue, short.MaxValue);
                int actual = BinaryPrimitives.ReadInt16LittleEndian(mixed.Samples.AsSpan(frame * 4 + channel * 2));
                Check(Math.Abs(expected - actual) <= 1, "Installed destination PCM differs from independently summed decoded sources.");
            }
        }
    }

    private static PcmWave Wave(int seconds, bool ramp)
    {
        var bytes = new byte[seconds * 48000 * 4];
        for (int frame = 0; frame < seconds * 48000; frame++)
            for (int channel = 0; channel < 2; channel++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(frame * 4 + channel * 2),
                (short)(ramp ? (frame / 48000 + 1) * 1000 : 1000));
        return new(48000, 2, 16, bytes);
    }
    private static short At(PcmWave wave, double time) => BinaryPrimitives.ReadInt16LittleEndian(wave.Samples.AsSpan((int)Math.Round(time * 48000) * 4));
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return; } throw new Exception("Unsupported destination was accepted."); }
}
