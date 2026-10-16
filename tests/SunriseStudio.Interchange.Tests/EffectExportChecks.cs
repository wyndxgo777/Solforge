using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using SunriseStudio.Interchange;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/**
 * A fixture emitter spawns 10 particles a second that live 1 s and fly +X at 1 m/s from (5, 6, 7).
 * Without a timer it bakes as a seamless stream; with one it bakes one start-to-start run.
 */
internal static class EffectExportChecks
{
    private static readonly Vector3 Origin = new(5, 6, 7);

    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(string parent, string? keep = null)
    {
        string root = Path.Combine(parent, "effect-export-checks"), game = Path.Combine(root, "game");
        Directory.CreateDirectory(Path.Combine(game, "packages"));
        PackageWriter.WriteNew(Path.Combine(game, "packages", "w64_effects_0100_0.pkg"), 0x100, [new(0x80800001, 8 << 9, new byte[16])]);
        using var catalog = PackageCatalog.Open(game, cacheOptions: new() { Enabled = false });
        var scene = new SceneDocument { Name = "Effect fixture", TagHash = 0x80A00000 };
        scene.Markers.Add(Marker(0x80A00010, timed: false, gameplay: false));
        scene.Markers.Add(Marker(0x80A00020, timed: true, gameplay: false));
        scene.Markers.Add(Marker(0x80A00030, timed: false, gameplay: true));
        scene.Markers.Add(new SceneMarker { Name = "Flare", TagHash = 0x80A00040, Layer = SceneLayer.Effects,
            Effect = new EffectDescription { Kind = EffectKind.LensFlare, SourceTag = 0x80A00041 } });

        var baked = EffectBake.Run(scene);
        Check(baked.Count == 3 && baked.All(value => value.Failure is null), "The fixture emitters did not all bake.");
        var stream = baked[0];
        Check(stream is { Mode: "wrap", Default: true } && stream.Frames == (int)MathF.Round(4 * EffectBake.Rate), $"Stream baked as {stream.Mode}.");
        var counts = Enumerable.Range(0, stream.Frames).Select(frame => stream.Samples.Count(s => s.Frame == frame)).ToArray();
        // A steady stream holds about rate times life particles in every frame, the seam included.
        Check(counts.All(count => count is >= 7 and <= 12) && Math.Abs(counts[0] - counts[^1]) <= 1,
            $"The stream is not steady across the loop: {string.Join(",", counts)}.");
        Check(stream.Samples.All(s => s.Position.X - Origin.X is >= 0 and <= 1.05f && MathF.Abs(s.Position.Y - Origin.Y) < 1e-4f
            && MathF.Abs(s.Position.Z - Origin.Z) < 1e-4f && s.Alpha is > 0 and <= 1 && s.Size > 0), "A stream sample left its path.");
        var run = baked[1];
        Check(run.Mode == "cycle" && run.Frames is > 30 and < 120, $"The timed emitter baked as {run.Mode} over {run.Frames} frames.");
        Check(!baked[2].Default && baked[2].Samples.Count > 0, "A gameplay effect was not baked as Show all runs it.");

        string output = keep ?? Path.Combine(root, "export");
        var exported = WorldSceneExport.Export(catalog, scene, output, new(IncludeMaterials: false));
        string folder = Path.GetDirectoryName(exported.ManifestPath)!;
        var set = JsonSerializer.Deserialize<EffectExportSet>(File.ReadAllBytes(Path.Combine(folder, WorldSceneExport.EffectsName)))!;
        Check(set is { Format: EffectExport.Format, Version: EffectExport.Version, SampleStride: 20 } && set.Emitters.Count == 3
            && set.Skipped.Single().Kind == "LensFlare", "The effects document differs from the bake.");
        byte[] data = File.ReadAllBytes(Path.Combine(folder, set.DataPath));
        Check(data.Length == set.DataLength && Convert.ToHexString(SHA256.HashData(data)) == set.DataSha256
            && data.Length == baked.Sum(value => value.Samples.Count) * 20, "The sample file differs from its document.");
        foreach (var (emitter, source) in set.Emitters.Zip(baked))
        {
            Check(emitter.Count == source.Samples.Count && emitter.Frames == source.Frames && emitter.Material == 0
                && set.Materials[0].Sprite == -1, "An emitter row differs from its bake.");
            for (int i = 0; i < emitter.Count; i++)
            {
                var row = data.AsSpan(checked((int)(emitter.First + i) * 20), 20);
                var sample = source.Samples[i];
                var position = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(row), BinaryPrimitives.ReadSingleLittleEndian(row[4..]),
                    BinaryPrimitives.ReadSingleLittleEndian(row[8..]));
                float size = (float)BinaryPrimitives.ReadHalfLittleEndian(row[12..]), alpha = (float)BinaryPrimitives.ReadHalfLittleEndian(row[14..]);
                int frame = BinaryPrimitives.ReadUInt16LittleEndian(row[18..]);
                Check(position == sample.Position && frame == sample.Frame && MathF.Abs(size - sample.Size) < 2e-3f * sample.Size
                    && MathF.Abs(alpha - sample.Alpha) < 1e-3f && (i == 0 || frame >= BinaryPrimitives.ReadUInt16LittleEndian(
                        data.AsSpan(checked((int)(emitter.First + i - 1) * 20) + 18))), "A stored sample differs from the bake.");
            }
        }
        Check(exported.Manifest.Notices.Any(notice => notice is { Surface: "Effects", Status: "Baked" })
            && exported.Manifest.Notices.Any(notice => notice.Status == "Markers only" && notice.Message.Contains("lens flares")),
            "The world export does not name what it baked and what it skipped.");
        Console.WriteLine($"effect_export stream=pass cycle={run.Frames} samples={set.Statistics.Samples} bytes={data.Length} path={folder}");
    }

    private static SceneMarker Marker(uint tag, bool timed, bool gameplay)
    {
        // Rate, life and speed are emitter constants; position and initial position share lanes, as do the velocities.
        var emitter = new EffectEmitter(tag + 1,
        [
            new(10, "emission_rate", 6, 0, new(10, 0, 0, 0)), new(5, "particle_lifetime", 6, 1, new(1, 0, 0, 0)),
            new(15, "initial_speed", 6, 2, new(1, 0, 0, 0)), new(6, "particle_position", 0, 0, null),
            new(8, "initial_particle_position", 0, 0, null), new(7, "particle_velocity", 0, 4, null),
            new(9, "initial_particle_velocity", 0, 4, null), new(1, "particle_random", 0, 8, null)
        ]) { ChannelConstants = [new(10, 1, 1, 0)] };
        var activation = new EffectActivation
        {
            AutoStart = !gameplay, Reachable = true, Continuous = true, Timed = timed, Duration = timed ? 1 : 0
        };
        return new SceneMarker
        {
            Name = $"Fixture {tag:X8}", TagHash = tag, SourceTag = 0x80A00001, SourceOffset = (int)(tag & 0xFF), Layer = SceneLayer.Effects,
            Effect = new EffectDescription
            {
                Kind = EffectKind.ParticleSystem, SourceTag = tag, DurationMin = timed ? 1 : 0, DurationMax = timed ? 1 : 0,
                Systems = [new EffectSystem(tag, tag + 1, 0, 0, [], emitter)], Activation = activation,
                Placement = Matrix4x4.CreateTranslation(Origin)
            }
        };
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
