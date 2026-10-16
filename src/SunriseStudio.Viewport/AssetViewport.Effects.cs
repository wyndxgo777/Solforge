using System.Collections.Concurrent;
using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using Vortice.DXGI;

namespace SunriseStudio.Viewport;

/** Effect content: simulated particles and lens-flare sprites drawn after opaque geometry. */
public sealed partial class AssetViewport
{
    private sealed record EffectSprite(int Generation, uint Material, Format Format, int Width, int Height, byte[] Data,
        int RowPitch, bool SingleChannel, int? Blend, bool Failed);
    /** Live emitters that share one visual; kept between frames so grouping does not allocate. */
    private sealed class EffectGroup
    {
        public int Visual { get; set; }
        public int Order { get; set; }
        public float Farthest { get; set; }
        public List<EffectEmitterSetup> Emitters { get; } = [];
    }
    private static readonly IComparer<EffectGroup> FarthestGroupFirst = Comparer<EffectGroup>.Create((a, b) =>
        a.Farthest != b.Farthest ? b.Farthest.CompareTo(a.Farthest) : a.Order.CompareTo(b.Order));
    private readonly List<EffectGroup> effectGroups = [];
    private readonly Dictionary<int, EffectGroup> effectGroupByVisual = [];
    private int effectGroupCount;

    // Sprites use the largest mip within this edge; billboards are small on screen.
    private const int EffectSpriteEdge = 256;
    // Particles per frame across all emitters.
    private const int EffectParticleBudget = 200_000;
    // Emitters farther than this from the camera, in meters, are skipped.
    private const float EffectDistance = 250;
    private EffectRenderer? effectRenderer;
    private NativeRenderStateCatalog? effectStates;
    private SceneDocument? effectScene;
    private readonly List<EffectEmitterSetup> effectEmitters = [];
    // The placement each emitter last followed without a pose, by emitter index; null follows again.
    private Matrix4x4?[] effectStillPlacements = [];
    private readonly List<(EffectEmitterSetup Emitter, float Distance)> effectLive = [];
    private readonly List<EffectDrawRange> effectRanges = [];
    private readonly Dictionary<uint, int> effectVisuals = [];
    private readonly List<GpuTexture> effectTextures = [];
    private readonly HashSet<uint> effectRequested = [];
    private readonly Queue<uint> effectPending = [];
    private readonly ConcurrentQueue<EffectSprite> effectReady = new();
    private readonly CancellationTokenSource effectLifetime = new();
    private Task? effectLoad;
    private EffectBillboard[] effectBillboards = [];
    private int effectDefaultVisual = -1;
    private PackageCatalog? effectCatalog;
    private long effectRevision = -1;
    private int effectGeneration;

    /** Effect sprites are read from this catalog; without one every particle draws as a soft dot. */
    public PackageCatalog? Catalog { get; set; }
    public int EffectParticleCount { get; private set; }
    internal IReadOnlyList<EffectEmitterSetup> EffectEmitters => effectEmitters;
    /** Which effects run; Studio gives both of its viewports one instance. */
    public EffectControls EffectControls { get; set; } = new();
    private (PackageCatalog Catalog, EffectReader Reader)? effectClipReader;

    /** Runs on the graphics thread after visibility and before either renderer draws. */
    private void UpdateEffects(int width, int height, double seconds)
    {
        effectRanges.Clear();
        EffectParticleCount = 0;
        // Particle effects are a Full quality feature.
        if (Quality != ViewportQuality.Full) return;
        if (!ReferenceEquals(Scene, effectScene)) RebuildEffects();
        FollowEffectBones();
        ActivateEffects();
        PollEffectSprites();
        CollectLiveEmitters(width, height, seconds);
        if (effectLive.Count == 0) return;
        effectRenderer ??= new EffectRenderer(renderer);
        if (effectDefaultVisual < 0) effectDefaultVisual = effectRenderer.AddVisual(CreateSoftDot(), false, null, null);
        int capacity = Math.Min(EffectParticleBudget, effectLive.Count * EffectSimulation.EmitterCap);
        if (effectBillboards.Length < capacity) effectBillboards = new EffectBillboard[capacity];
        int count = 0;
        GroupLiveEmitters();
        // Emitters sharing a visual form one draw; farther visuals draw first.
        for (int index = 0; index < effectGroupCount; index++)
        {
            var group = effectGroups[index];
            int first = count;
            foreach (var emitter in group.Emitters)
            {
                if (count >= capacity) break;
                count += EffectSimulation.Emit(emitter, seconds, Vector3.Distance(Camera.Position, emitter.Origin),
                    effectBillboards.AsSpan(count, capacity - count));
            }
            if (count > first) effectRanges.Add(new EffectDrawRange(group.Visual, first, count - first));
        }
        EffectParticleCount = count;
        effectRenderer.Upload(effectBillboards.AsSpan(0, count));
    }

    /** Emitters follow their entity's placement; those of the opened entity also follow its pose. */
    private void FollowEffectBones()
    {
        var pose = Animation?.Pose;
        if (effectStillPlacements.Length != effectEmitters.Count) effectStillPlacements = new Matrix4x4?[effectEmitters.Count];
        for (int index = 0; index < effectEmitters.Count; index++)
        {
            var emitter = effectEmitters[index];
            var marker = emitter.Marker;
            bool posed = pose is not null && marker.SourceTag == Scene?.TagHash
                && marker.Effect!.EntityTag == pose.Rig.EntityTag;
            var placement = marker.Effect!.Placement;
            // Without a pose, Follow gives the same result for the same placement.
            if (!posed && effectStillPlacements[index] == placement) continue;
            EffectSimulation.Follow(emitter, posed ? pose!.ObjectTransforms : null);
            effectStillPlacements[index] = posed ? null : placement;
        }
    }

    /**
     * Sets each emitter's run state from its trigger and the viewer's choices.
     * While the posed entity shows a clip, a channel it lists reads 1; that clips drive it is assumed.
     */
    private void ActivateEffects()
    {
        var pose = Animation?.Pose;
        IReadOnlyList<uint> clipChannels = [];
        if (Animation?.Clip is { } clip && Catalog is { } catalog)
        {
            if (effectClipReader?.Catalog != catalog) effectClipReader = (catalog, new EffectReader(catalog));
            clipChannels = effectClipReader.Value.Reader.ReadClipChannels(clip.TagHash);
        }
        var controls = EffectControls;
        foreach (var emitter in effectEmitters)
        {
            if (emitter.Simulation is not { } simulation) continue;
            var marker = emitter.Marker;
            var effect = marker.Effect!;
            bool forced = controls.ShowAll || controls.Forced(marker);
            if (effect.Activation is not { } activation)
            {
                simulation.Active = forced;
                continue;
            }
            var key = EffectPlacementKey.Of(marker);
            bool animated = clipChannels.Count != 0 && pose is not null && marker.SourceTag == Scene?.TagHash
                && effect.EntityTag == pose.Rig.EntityTag;
            var (playing, generation) = controls.Sequence(key, activation.SequenceTag, activation.AutoStart);
            bool picked = Picked(key, activation, generation);
            simulation.Active = forced || playing && picked && activation.Startable()
                && activation.GatesOpen((key, Clip: animated ? clipChannels : [], controls),
                    static (state, hash) => state.Clip.Contains(hash) ? 1 : state.controls.Channel(state.key, hash));
            simulation.Loop = forced || activation.Continuous;
            simulation.Delay = forced ? 0 : activation.Delay + (activation.SerialStart ?? 0);
            if (emitter.Generation == generation) continue;
            emitter.Generation = generation;
            simulation.Start();
        }
    }

    /** Every random pick on the path chooses this node; a sequence that never played picks none. */
    private static bool Picked(EffectPlacementKey key, EffectActivation activation, int generation)
    {
        var picks = activation.RandomPicks;
        for (int index = 0; index < picks.Count; index++)
            if (generation <= 0 || !Pick(key, activation.SequenceTag, generation, picks[index])) return false;
        return true;
    }

    /** Each play picks one branch of a random flow, the same for every node of the placement. */
    private static bool Pick(EffectPlacementKey key, uint sequence, int generation, EffectRandomPick pick)
    {
        if (pick.Branches <= 0) return false;
        uint seed = EffectSimulation.Hash(EffectSimulation.Hash(key.SourceTag ^ (uint)key.SourceOffset, sequence),
            (uint)generation * 0x9E3779B9u ^ (uint)pick.Flow);
        return seed % (uint)pick.Branches == pick.Branch;
    }

    /** Groups keep live-emitter order; ties in distance keep the order a visual first appears. */
    private void GroupLiveEmitters()
    {
        effectGroupByVisual.Clear();
        effectGroupCount = 0;
        foreach (var (emitter, distance) in effectLive)
        {
            int visual = VisualFor(emitter);
            if (!effectGroupByVisual.TryGetValue(visual, out var group))
            {
                if (effectGroupCount == effectGroups.Count) effectGroups.Add(new EffectGroup());
                group = effectGroups[effectGroupCount];
                group.Visual = visual; group.Order = effectGroupCount; group.Farthest = distance;
                group.Emitters.Clear();
                effectGroupCount++;
                effectGroupByVisual.Add(visual, group);
            }
            group.Emitters.Add(emitter);
            group.Farthest = MathF.Max(group.Farthest, distance);
        }
        effectGroups.Sort(0, effectGroupCount, FarthestGroupFirst);
    }

    /** An emitter draws when its content is shown, it is near, and its reach can be in view. */
    private void CollectLiveEmitters(int width, int height, double seconds)
    {
        effectLive.Clear();
        if (effectEmitters.Count == 0) return;
        var planes = SceneGrid.FrustumPlanes(Camera.ViewProjection((float)width / height));
        var eye = Camera.Position;
        foreach (var emitter in effectEmitters)
        {
            if (emitter.Simulation is { Active: false, Running: false }) continue;
            float distance = Vector3.Distance(eye, emitter.Origin);
            if (distance > EffectDistance + emitter.Reach || !Visibility.IsContentVisible(emitter.Marker)) continue;
            // Reach comes only from particles, so an emitter without one runs unseen until it has particles.
            if (emitter.Reach == 0) EffectSimulation.Measure(emitter, seconds, distance);
            if (!SceneGrid.SphereInFrustum(emitter.Origin, emitter.Reach + 1, planes)) continue;
            effectLive.Add((emitter, distance));
            if (emitter.Material != 0 && effectRequested.Add(emitter.Material)) effectPending.Enqueue(emitter.Material);
        }
    }

    private int VisualFor(EffectEmitterSetup emitter) =>
        effectVisuals.TryGetValue(emitter.Material, out int visual) ? visual : effectDefaultVisual;

    /** The native frame calls this with the lit HDR target bound to scene depth. */
    private void DrawEffectsNative(NativeRenderTargets targets)
    {
        if (effectRenderer is null || effectRanges.Count == 0) return;
        float stops = nativeScene?.AppliedViewerStops ?? 0;
        // The viewer gain runs after this pass; divide it out so sprites keep their brightness.
        float gain = MathF.Pow(2, -(float.IsFinite(stops) ? stops : 0));
        effectRenderer.DrawNative(targets, Camera.View, Camera.ViewProjection((float)targets.Width / targets.Height),
            effectRanges, gain);
    }

    private void DrawEffectsPlain(NativeRenderTargets targets, Matrix4x4 viewProjection)
    {
        if (effectRenderer is null || effectRanges.Count == 0) return;
        effectRenderer.DrawNative(targets, Camera.View, viewProjection, effectRanges, 1);
    }

    /** Emitters follow the scene; sprite textures stay cached by material. */
    private void RebuildEffects()
    {
        effectScene = Scene;
        effectEmitters.Clear();
        effectStillPlacements = [];
        effectGroups.Clear();
        if (Scene is null) return;
        for (int index = 0; index < Scene.Markers.Count; index++)
        {
            var marker = Scene.Markers[index];
            if (marker.Effect is not { } effect) continue;
            if (effect.Kind == EffectKind.LensFlare) effectEmitters.Add(EffectSimulation.CreateFlare(marker, index));
            else for (int system = 0; system < effect.Systems.Count; system++)
                effectEmitters.AddRange(EffectSimulation.CreateSystems(marker, index, system));
        }
    }

    /** One worker reads queued sprites in order; texture uploads stay on the graphics thread. */
    private void PollEffectSprites()
    {
        // A project edit or a new catalog drops every sprite; results of older reads are ignored.
        long revision = Catalog?.OverlayRevision ?? -1;
        if (!ReferenceEquals(effectCatalog, Catalog) || revision != effectRevision)
        {
            effectRenderer?.Dispose();
            effectRenderer = null;
            foreach (var texture in effectTextures) texture.Dispose();
            effectTextures.Clear();
            effectVisuals.Clear();
            effectRequested.Clear();
            effectPending.Clear();
            effectDefaultVisual = -1;
            effectGeneration++;
            effectCatalog = Catalog;
            effectRevision = revision;
        }
        for (int uploads = 0; uploads < 16 && effectReady.TryDequeue(out var sprite); uploads++)
        {
            // A material without a usable sprite keeps the soft dot.
            if (sprite.Generation != effectGeneration || sprite.Failed) continue;
            try
            {
                effectRenderer ??= new EffectRenderer(renderer);
                effectStates ??= new NativeRenderStateCatalog();
                var texture = renderer.CreateTexture(sprite.Width, sprite.Height, sprite.Format, sprite.Data, sprite.RowPitch);
                effectTextures.Add(texture);
                effectVisuals[sprite.Material] = effectRenderer.AddVisual(texture, sprite.SingleChannel, sprite.Blend, effectStates);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or InvalidDataException
                or SharpGen.Runtime.SharpGenException) { }
        }
        if (effectLoad is { IsCompleted: false } || Catalog is not { } catalog || effectPending.Count == 0) return;
        var batch = effectPending.ToArray();
        effectPending.Clear();
        var token = effectLifetime.Token;
        int generation = effectGeneration;
        effectLoad = Task.Run(() =>
        {
            foreach (uint material in batch)
            {
                if (token.IsCancellationRequested) return;
                effectReady.Enqueue(ReadEffectSprite(catalog, material, generation));
            }
        }, token);
    }

    /** Picks a declared 2D pixel texture, preferring sRGB colour over masks and skipping normal maps. */
    private static EffectSprite ReadEffectSprite(PackageCatalog catalog, uint material, int generation)
    {
        try
        {
            var info = new GameMaterialReader(catalog).Read(material);
            var binding = info.Textures.Where(t => t.Stage == GameShaderStage.Pixel && t.DeclaredByShader
                    && t.ShaderDimension == "texture2d" && t.Depth <= 1
                    && t.Format is not (Format.BC5_UNorm or Format.BC5_SNorm or Format.BC6H_Uf16 or Format.BC6H_Sf16))
                .OrderByDescending(t => t.IsSrgb).ThenBy(t => t.Slot).FirstOrDefault();
            if (binding is null) return Failed();
            var data = new TextureDataReader(catalog).Read(binding.TextureTag);
            if (data.IsCube || data.IsVolume) return Failed();
            var mip = data.Subresources.Where(s => s.ArraySlice == 0)
                .OrderBy(s => Math.Max(s.Width, s.Height) <= EffectSpriteEdge ? 0 : 1)
                .ThenByDescending(s => s.Width).First();
            var format = (Format)data.Format;
            bool single = format is Format.BC4_UNorm or Format.BC4_SNorm or Format.R8_UNorm or Format.A8_UNorm;
            // Bit 7 marks an authored blend state; the low bits index the native blend table.
            byte blend = (byte)info.StateOverrides;
            return new EffectSprite(generation, material, format, mip.Width, mip.Height, mip.Data.AsSpan(0, mip.SlicePitch).ToArray(),
                mip.RowPitch, single, (blend & 0x80) != 0 ? blend & 0x7F : null, false);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException
            or KeyNotFoundException or ArgumentException) { return Failed(); }
        EffectSprite Failed() => new(generation, material, Format.Unknown, 0, 0, [], 0, false, null, true);
    }

    /** Stand-in sprite for materials still loading or without a usable texture. */
    private GpuTexture CreateSoftDot()
    {
        const int edge = 64;
        var pixels = new byte[edge * edge * 4];
        for (int y = 0; y < edge; y++)
            for (int x = 0; x < edge; x++)
            {
                float dx = (x + 0.5f) / edge * 2 - 1, dy = (y + 0.5f) / edge * 2 - 1;
                float a = MathF.Max(0, 1 - MathF.Sqrt(dx * dx + dy * dy));
                int i = (y * edge + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
                pixels[i + 3] = (byte)(a * a * 255);
            }
        var texture = renderer.CreateTexture(edge, edge, pixels);
        effectTextures.Add(texture);
        return texture;
    }

    private void DisposeEffects()
    {
        effectLifetime.Cancel();
        effectLifetime.Dispose();
        effectRenderer?.Dispose();
        effectRenderer = null;
        foreach (var texture in effectTextures) texture.Dispose();
        effectTextures.Clear();
        effectVisuals.Clear();
    }
}
