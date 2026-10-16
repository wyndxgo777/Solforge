using System.Buffers.Binary;
using SunriseStudio.Audio;
using SunriseStudio.Packages;

/** Checks that clip automation lanes stay tied to their clip index and trimmed playback time, and that malformed or duplicate lanes are refused. */
internal static class AutomationChecks
{
    /** Runs the checks that use generated fixtures instead of installed game content. */
    public static void Synthetic(string directory)
    {
        AudioClipAutomation[] lanes =
        [
            new(0, 0, [new(0, -.5f, 9)]),
            new(0, 3, [new(0, 0, 4), new(1, 1, 9)]),
            new(0, 4, [new(0, 1, 9), new(1.5f, 1, 4), new(2.5f, 0, 9)])
        ];
        AudioClip[] clips = [TestMusic.Clip(11, 4000, -1000, 1500), TestMusic.Clip(11, 4000, 3500)];
        var analysis = new TestMusic()
            .Track(10, 20, clips, lanes)
            .Segment(20, 30, 5000, [new(0, 1000), new(0, 5000)], 10)
            .Switch(30, 0, null, TestMusic.Rule(sync: 7))
            .Build();
        var plan = AudioTimeline.BuildTransition(analysis, 30, 0, 20, 0);
        Check(plan.Clips.Count == 2 && plan.Clips[0].AutomationOffsetSeconds == .5 && plan.Clips[0].SourceSeconds == 2
            && plan.Clips[0].Automation!.Count == 3 && plan.Clips[1].Automation!.Count == 0,
            "Automation was not tied to its clip index and trimmed playback time.");
        byte[] bytes = new byte[48000 * 4 * 4];
        for (int i = 0; i < bytes.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i), 8000);
        var render = AudioTimeline.Render(plan, new Dictionary<uint, PcmWave> { [11] = new(48000, 2, 16, bytes) }, Path.Combine(directory, "automation.wav"));
        var pcm = PcmWave.Read(render.Path);
        Check(At(pcm, .75) == 0 && At(pcm, 1) == 2000 && At(pcm, 2) == 4000 && At(pcm, 2.5) == 2000
            && At(pcm, 3.25) == 0 && At(pcm, 3.5) == 8000,
            "Trimmed clip envelopes did not produce the exact linear-gain sample products.");
        Reject(() => new AudioAutomationCurve(new(0, 1, [new(0, 0, 4)])));
        Reject(() => new AudioAutomationCurve(new(0, 0, [new(1, 0, 4), new(0, 0, 4)])));
        Reject(() => new AudioAutomationCurve(new(0, 0, [new(0, float.NaN, 4)])));
        // The bank reader skips a track with two volume lanes on one clip and names it.
        var read = AudioAnalysis.Read([TestHirc.Bank(113, 920, [(11, 10, TrackBytes(20, clips, lanes))])]);
        Check(read.Diagnostics.Count == 0 && read.Tracks[10] is { Clips.Count: 2, Automation.Count: 3 }, "A valid automated track was refused.");
        var duplicate = AudioAnalysis.Read([TestHirc.Bank(113, 921, [(11, 10, TrackBytes(20, clips, [.. lanes, new(0, 0, [new(0, 1, 4)])]))])]);
        Check(!duplicate.Tracks.ContainsKey(10) && duplicate.Diagnostics.Single().StartsWith("Music object 10 skipped", StringComparison.Ordinal),
            "A track with duplicate automation lanes was read.");
        Console.WriteLine("audio_automation synthetic=pass clip_index=checked trim_clock=checked gain_product=sample_exact malformed=rejected");
    }

    /** Runs the same kind of checks against the real packages of the owned installation. */
    public static async Task Owned(PackageCatalog catalog, string directory, string? output)
    {
        using var session = new AudioSession(catalog, 0x80B4F065, cacheDirectory: Path.Combine(directory, "automation-cache"));
        var rendered = await session.RenderTransition(818598526, 0, 693377003, 0);
        var plan = session.Timeline!;
        Check(plan.Clips.Count == 2 && plan.Clips[0].Automation is { Count: 1 } && plan.Clips[1].Automation is { Count: 1 }
            && plan.Clips[1].StartSeconds == 6 && plan.Clips[1].SourceSeconds == 26 && plan.Clips[1].AutomationOffsetSeconds == 0,
            "Installed trimmed crossfade did not retain its source offset and clip-local envelope clock.");
        Check(plan.Clips[0].Automation![0].Type == 4 && plan.Clips[1].Automation![0].Type == 3,
            "Installed fade lanes were not decoded.");
        var wave = PcmWave.Read(rendered.Path);
        Check(Math.Abs(wave.DurationSeconds - 24.1729166666667) < 1.0 / 48000 && wave.Samples.Any(value => value != 0),
            "Installed automated two-clip segment did not render fully.");
        if (output is not null)
        {
            AudioSession.ExportWave(rendered.Path, output, catalog.GameRoot);
            File.WriteAllText(Path.ChangeExtension(output, ".json"), System.Text.Json.JsonSerializer.Serialize(plan,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        using var volumeSession = new AudioSession(catalog, 0x80B4F00D, cacheDirectory: Path.Combine(directory, "volume-cache"));
        var volumeRender = await volumeSession.RenderTransition(431469067, 0, 101955951, 0);
        Check(volumeSession.Timeline!.Clips.Single().Automation!.Single().Type == 0
            && PcmWave.ReadInfo(volumeRender.Path).DurationSeconds > 113, "Installed volume envelope was skipped.");
        Console.WriteLine($"audio_automation owned=80B4F065 crossfade_seconds={wave.DurationSeconds:F6} trimmed_source=26s volume_bank=80B4F00D clipped={rendered.ClippedSamples}");
    }

    private static short At(PcmWave wave, double seconds) => BinaryPrimitives.ReadInt16LittleEndian(wave.Samples.AsSpan((int)(seconds * 48000) * 4));
    /** A version-113 track with one Vorbis source, the given clips and lanes, and a plain node under the parent. */
    private static byte[] TrackBytes(uint parent, AudioClip[] clips, AudioClipAutomation[] lanes)
    {
        var bytes = new HircBytes().U8(0).U32(1).Raw(TestHirc.Source(TestHirc.Vorbis, 11)).U32((uint)clips.Length);
        foreach (var clip in clips)
            bytes.U32(clip.Lane).U32(clip.SourceId).F64(clip.PlayAt).F64(clip.BeginTrim).F64(clip.EndTrim).F64(clip.SourceDuration);
        bytes.U32(1).U32((uint)lanes.Length);
        foreach (var lane in lanes)
        {
            bytes.U32(lane.ClipIndex).U32((uint)lane.Type).U32((uint)lane.Points.Count);
            foreach (var point in lane.Points) bytes.F32(point.Seconds).F32(point.Value).U32((uint)point.Curve);
        }
        return bytes.Raw(TestHirc.Node(0, parent)).U8(0).S32(0).ToArray();
    }
    private static void Check(bool condition, string error) { if (!condition) throw new InvalidDataException(error); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return; } throw new Exception("Invalid automation was accepted."); }
}
