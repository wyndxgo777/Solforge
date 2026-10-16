using System.Buffers.Binary;
using SunriseStudio.Audio;
using SunriseStudio.Packages;

/** Checks music playlist step history, step selection, reset and rejected timelines so playlists advance and render like the native engine. */
internal static class PlaylistChecks
{
    /** A step container over segments 21 to 23, alone or nested between segments 20 and 24. */
    public static AudioAnalysis Fixture(bool nested = false, bool infinite = false, uint jump = 0)
    {
        var step = Item(110, 0, 1, 1, Item(111, 21, -1), Item(112, 22, -1), Item(113, 23, -1));
        var root = nested ? Item(100, 0, 0, 2, Item(101, 20, -1, infinite ? 0 : 1), step, Item(114, 24, -1)) : step;
        var music = new TestMusic()
            .Switch(300, 0, null, TestMusic.Rule(sync: 7, jump: jump))
            .Playlist(30, 300, root, TestMusic.Rule(sync: 7));
        for (uint id = 20; id <= 24; id++)
            music.Segment(id, 30, 1000, [new(0, 0), new(0, 1000)], id + 100).Track(id + 100, id, TestMusic.Clip(id, 1000));
        return music.Build();
    }

    /** Runs the checks that use generated fixtures instead of installed game content. */
    public static void Synthetic(string directory)
    {
        var analysis = Fixture(); var history = new AudioPlaylistHistory();
        var tones = Enumerable.Range(20, 5).ToDictionary(id => (uint)id, id => Tone((short)(id * 100)));
        for (int play = 0; play < 7; play++)
        {
            var plan = AudioTimeline.Build(analysis, 30, history: history);
            uint expected = (uint)(21 + play % 3);
            Check(plan.Segments.Single().Id == expected && plan.PlaylistPath!.PlayNumber == play + 1
                && history.PlayCount(30) == play + 1, "Step history did not advance once per play or wrap.");
            var rendered = AudioTimeline.Render(plan, tones, Path.Combine(directory, "step.wav"));
            Check(BinaryPrimitives.ReadInt16LittleEndian(PcmWave.Read(rendered.Path).Samples.AsSpan(48000 * 2)) == expected * 100,
                "Step selection rendered another segment's PCM.");
        }
        history.Reset(); Check(AudioTimeline.Build(analysis, 30, history: history).Segments.Single().Id == 21, "Reset did not restore the first step.");
        var tracks = (Dictionary<uint, AudioTrack>)analysis.Tracks; var original = tracks[122];
        tracks[122] = original with { Limits = ["Fixture rejection"] };
        Reject(() => AudioTimeline.Build(analysis, 30, history: history));
        Check(history.PlayCount(30) == 1, "A rejected timeline committed its history.");
        tracks[122] = original;
        Check(AudioTimeline.Build(analysis, 30, history: history).Segments.Single().Id == 22, "Rejected preparation skipped a step.");

        var nested = Fixture(true); history.Reset();
        var fromContainer = AudioTimeline.Build(nested, 30, history: history, startItem: 110);
        Check(fromContainer.Segments.Select(value => value.Id).SequenceEqual(new uint[] { 21, 24, 20, 22, 24 }),
            "Container jump did not resume the parent sequence and later full loop.");
        history.Reset();
        var fromLeaf = AudioTimeline.Build(nested, 30, history: history, startItem: 112);
        Check(fromLeaf.Segments.Select(value => value.Id).SequenceEqual(new uint[] { 22, 24, 20, 23, 24 }),
            "Leaf jump did not update its step ancestor's retained cursor.");
        var afterJump = AudioTimeline.Build(nested, 30, history: history);
        Check(afterJump.Segments.Select(value => value.Id).SequenceEqual(new uint[] { 20, 21, 24, 20, 22, 24 }),
            "The next play forgot the history established by an explicit jump.");
        Reject(() => AudioTimeline.Build(nested, 30, history: history, startItem: uint.MaxValue));
        Reject(() => AudioTimeline.Build(analysis, 30, history: history));
        var truncated = AudioTimeline.Build(Fixture(true, true), 30, loopIterations: 2);
        Check(truncated.Segments.Select(value => value.Id).SequenceEqual(new uint[] { 20, 20 })
            && truncated.PlaylistPath!.Truncated, "A bounded infinite item fell through into an unreachable sibling.");
        history.Reset(); var authoredJump = Fixture(true, jump: 110);
        var transition = AudioTimeline.BuildPlaylistTransition(authoredJump, 300, 0, 30, 0, history: history);
        Check(transition.Segments.Select(value => value.Id).SequenceEqual(new uint[] { 21, 24, 20, 22, 24 })
            && transition.PlaylistPath is { StartItem: 110, PlayNumber: 1 }, "Authored container jump was not resolved before scheduling the complete destination.");
        var mix = AudioTimeline.Render(transition, tones, Path.Combine(directory, "container-transition.wav"));
        var wave = PcmWave.Read(mix.Path);
        Check(Enumerable.Range(0, 5).Select(i => BinaryPrimitives.ReadInt16LittleEndian(wave.Samples.AsSpan((i * 48000 + 24000) * 4)))
            .SequenceEqual(new short[] { 2100, 2400, 2000, 2200, 2400 }), "Authored container transition lost its parent continuation PCM.");
        Console.WriteLine("audio_playlist synthetic=pass history=retained jumps=parent_resume failure=rollback infinite=bounded pcm=selected_source");
    }

    /** Runs the same kind of checks against the real packages of the owned installation. */
    public static async Task Owned(PackageCatalog catalog, string directory, string? output)
    {
        using var session = new AudioSession(catalog, 0x80BBEC39, cacheDirectory: Path.Combine(directory, "playlist-cache"));
        uint[] expected = [589906398, 564244962, 334179480, 589906398];
        for (int play = 0; play < expected.Length; play++)
        {
            var rendered = await session.RenderTimeline(81713458, 1, 0);
            var plan = session.Timeline!;
            Check(plan.Segments.Single().Id == expected[play] && plan.PlaylistPath!.PlayNumber == play + 1
                && session.PlaylistPlayCount(81713458) == play + 1, "Installed step sequence did not advance and wrap.");
            var pcm = PcmWave.Read(rendered.Path);
            if (play is 0 or 1 or 3) Check(pcm.Samples.Any(value => value != 0), "Installed musical step rendered silence.");
            Check(pcm.Samples.Length / pcm.BlockAlign == (int)Math.Round(plan.DurationSeconds * 48000), "Installed step duration changed during rendering.");
            if (output is not null)
            {
                string path = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + $"-play{play + 1}.wav");
                AudioSession.ExportWave(rendered.Path, path, catalog.GameRoot);
                File.WriteAllText(Path.ChangeExtension(path, ".json"), System.Text.Json.JsonSerializer.Serialize(plan,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
        }
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await session.RenderTimeline(81713458, 1, 0, canceled.Token); throw new InvalidDataException("Canceled history advanced."); }
            catch (OperationCanceledException) { }
        }
        Check(session.PlaylistPlayCount(81713458) == 4, "Canceled playback consumed a step.");
        await session.RenderTimeline(81713458, 1, 0, startItem: 102554337);
        Check(session.Timeline!.Segments.Single().Id == 334179480, "Installed leaf jump chose a different segment.");
        await session.RenderTimeline(81713458, 1, 0, startItem: 978736873);
        Check(session.Timeline!.Segments.Single().Id == 589906398, "Installed container jump did not use its retained step cursor.");
        session.ResetPlaylistHistory(); await session.RenderTimeline(81713458, 1, 0);
        Check(session.PlaylistPlayCount(81713458) == 1 && session.Timeline!.Segments.Single().Id == 589906398,
            "Installed history reset failed.");
        await session.RenderPlaylistTransition(528193615, 0, 81713458, 0, 1, 0);
        // The bank authors a jump from silence into this playlist to leaf item 102554337.
        var destination = session.Timeline!;
        Check(destination.Segments.Single().Id == 334179480 && destination.PlaylistPath?.StartItem == 102554337
            && session.PlaylistPlayCount(81713458) == 2,
            $"Playlist destination ignored its authored jump or the session history: segments="
            + $"{string.Join(",", destination.Segments.Select(row => row.Id))} plays={session.PlaylistPlayCount(81713458)} "
            + $"start={destination.PlaylistPath?.StartItem} items={string.Join(",", destination.PlaylistPath?.Items ?? [])}");
        Console.WriteLine("audio_playlist owned=80BBEC39 playlist=81713458 plays=3_then_wrap leaf_jump=checked container_jump=checked destination=history cancel=rollback reset=pass");
    }

    private static PcmWave Tone(short value)
    {
        var bytes = new byte[48000 * 4];
        for (int i = 0; i < bytes.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i), value);
        return new(48000, 2, 16, bytes);
    }
    private static AudioPlaylistItem Item(uint id, uint segment, int kind, int loops = 1, params AudioPlaylistItem[] children) =>
        TestMusic.Item(id, segment, kind, loops, children);
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException or NotSupportedException) { return; } throw new Exception("Unsupported playlist was accepted."); }
}
