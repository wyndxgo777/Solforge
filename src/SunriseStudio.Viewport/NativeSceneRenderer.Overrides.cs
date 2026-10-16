/**
 * File: Part of NativeSceneRenderer: applies live MaterialOverrides (Rendering/MaterialOverrides.cs) to draws, with
 * GPU textures for override images.
 */
using SunriseStudio.Rendering;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SunriseStudio.Viewport;

/**
 * Live material overrides (the node graph's preview): per material tag, constant values written into copies of the
 * authored template or expression constants, and texture slots pointed at other textures. They change only what this
 * renderer draws; no tag is written. With no override set, or an empty one, nothing here runs and every material
 * draws its authored data.
 */
public sealed partial class NativeSceneRenderer
{
    /** Empty override map used when no source is set. */
    private static readonly IReadOnlyDictionary<uint, MaterialOverride> NoOverrides = new Dictionary<uint, MaterialOverride>();
    private MaterialOverrideSet? overrideSource;
    private int overrideRevision;
    private IReadOnlyDictionary<uint, MaterialOverride> appliedOverrides = NoOverrides;
    // Textures an override named that the scene had not uploaded; released when no override names them.
    private readonly Dictionary<uint, GpuTexture> overrideTextures = [];

    /** The shared live overrides this renderer draws; read when each frame starts. */
    public MaterialOverrideSet? MaterialOverrides { get; set; }

    /** Applies the overrides that changed since the last frame and puts back the ones that were removed. */
    private void SyncMaterialOverrides()
    {
        var set = MaterialOverrides;
        if (set is null && appliedOverrides.Count == 0) return;
        if (set is not null && ReferenceEquals(set, overrideSource) && set.Revision == overrideRevision) return;
        var (revision, next) = set?.Snapshot() ?? (0, NoOverrides);
        if (!ReferenceEquals(set, overrideSource)) overrideSource?.Forget(this);
        overrideSource = set;
        overrideRevision = revision;
        var previous = appliedOverrides;
        appliedOverrides = next;
        var changed = previous.Keys.Concat(next.Keys).Where(tag =>
            !ReferenceEquals(previous.GetValueOrDefault(tag), next.GetValueOrDefault(tag))).ToHashSet();
        if (changed.Count == 0) return;
        var skipped = changed.ToDictionary(tag => tag, _ => new List<string>());
        var touched = new HashSet<MaterialGpu>();
        foreach (var material in materials.Values)
            if (skipped.TryGetValue(material.Source.Execution.Material.Tag, out var notes))
            {
                ApplyLiveOverride(material, notes);
                touched.Add(material);
            }
        ReleaseOverrideTextures();
        if (set is null) return;
        foreach (uint tag in changed)
        {
            if (!next.ContainsKey(tag)) { set.Report(this, tag, null); continue; }
            int count = touched.Count(material => material.Source.Execution.Material.Tag == tag);
            int parts = groups.Where(group => touched.Contains(group.Material) && group.Material.Source.Execution.Material.Tag == tag)
                .Sum(group => group.Source.Parts.Count);
            set.Report(this, tag, new(count, parts, skipped[tag].Distinct(StringComparer.Ordinal).ToArray()));
        }
    }

    /**
     * Puts the material back on its authored data, then applies the override its tag has now, if any.
     * @param skipped Receives the writes this material leaves out; null discards them.
     */
    private void ApplyLiveOverride(MaterialGpu material, List<string>? skipped)
    {
        var value = appliedOverrides.GetValueOrDefault(material.Source.Execution.Material.Tag);
        if (value is null && material.OverrideExecution is null && material.ReplacedResources is null) return;
        if (material.ReplacedResources is { } replaced)
        {
            foreach (var (key, view) in replaced) material.Bindings.Resources[key] = view;
            material.ReplacedResources = null;
        }
        if (material.SuspendedShares is { } shares)
        {
            material.VertexShare = shares.Vertex;
            material.PixelShare = shares.Pixel;
            material.SuspendedShares = null;
        }
        var authored = material.Source.Execution;
        MaterialExecutionData? execution = null;
        if (value is not null)
        {
            foreach (var write in value.Constants.Where(write => write.Stage is not (GameShaderStage.Vertex or GameShaderStage.Pixel)))
                skipped?.Add($"{write.Stage} constants are not drawn in the viewport.");
            var vertex = MaterialOverrideSet.Apply(authored.Vertex, value.Constants, skipped);
            var pixel = MaterialOverrideSet.Apply(authored.Pixel, value.Constants, skipped);
            if (!ReferenceEquals(vertex, authored.Vertex) || !ReferenceEquals(pixel, authored.Pixel))
                execution = authored with { Vertex = vertex, Pixel = pixel };
        }
        bool hadConstants = material.OverrideExecution is not null;
        material.OverrideExecution = execution;
        if (execution is not null)
        {
            // Changed constants make a program of its own: it no longer shares evaluations with the authored one.
            material.SuspendedShares = (material.VertexShare, material.PixelShare);
            material.VertexShare = material.PixelShare = TfxShare.None;
        }
        if (execution is not null || hadConstants) EvaluateLive(material);
        if (value is not null) ApplyTextureOverrides(material, value.Textures, skipped);
    }

    /**
     * Evaluates both stages with the inputs of the material's first evaluation and binds them, so a material whose
     * TFX never runs again shows the new constants; one that runs each frame evaluates again with that frame's inputs.
     */
    private void EvaluateLive(MaterialGpu material)
    {
        try
        {
            var execution = material.Execution;
            foreach (var stage in new[] { execution.Vertex, execution.Pixel })
                ApplyEvaluation(material, stage, TfxEvaluator.Evaluate(stage, CreationInputs(material.Source, material.Decorator)));
            if (material.Source.ExternResources) ApplyExternResources(material);
        }
        catch (Exception error) when (Recoverable(error) || error is InvalidOperationException or NotSupportedException)
        {
            AddDiagnostic($"Material 0x{material.Source.Execution.Material.Tag:X8} live override: {error.Message}");
        }
        material.LastTime = double.NaN;
    }

    /** Points declared texture slots at the override textures; a slot keeps its texture when the new one does not fit. */
    private void ApplyTextureOverrides(MaterialGpu material, IReadOnlyList<MaterialTextureOverride> overrides, List<string>? skipped)
    {
        foreach (var write in overrides)
        {
            var key = new NativeBindingKey(write.Stage, write.Slot);
            bool declared = material.Source.Execution.Material.Textures.Any(texture => texture.DeclaredByShader
                && texture.Stage == write.Stage && texture.Slot == write.Slot);
            if (!declared || !material.Bindings.Resources.TryGetValue(key, out var authored))
            {
                skipped?.Add($"{write.Stage} texture slot {write.Slot} is not read by the material's shader.");
                continue;
            }
            if (OverrideTexture(write) is not { } texture)
            {
                skipped?.Add($"Texture 0x{write.TextureTag:X8} is not loaded yet.");
                continue;
            }
            var dimension = authored.Description.ViewDimension;
            if (texture.Dimension != dimension)
            {
                skipped?.Add($"Texture 0x{write.TextureTag:X8} is {texture.Dimension}; {write.Stage} slot {write.Slot} reads {dimension}.");
                continue;
            }
            (material.ReplacedResources ??= new()).TryAdd(key, authored);
            material.Bindings.Resources[key] = texture.View;
        }
    }

    /** The scene's own upload of a texture, else one made from the override's payload; null when neither exists. */
    private GpuTexture? OverrideTexture(MaterialTextureOverride write)
    {
        if (textures.TryGetValue(write.TextureTag, out var resident)) return resident;
        if (overrideTextures.TryGetValue(write.TextureTag, out var uploaded)) return uploaded;
        if (write.Texture is not { } t) return null;
        try
        {
            var created = renderer.CreateTextureResource(t.Width, t.Height, t.Depth, t.ArraySize, t.MipCount, (Format)t.Format,
                t.IsCube, t.IsVolume, t.Subresources.Select(s => new TextureSubresourceUpload(s.MipLevel, s.ArraySlice,
                    s.Data, s.RowPitch, s.SlicePitch)).ToArray());
            overrideTextures.Add(write.TextureTag, created);
            return created;
        }
        catch (Exception error) when (Recoverable(error))
        {
            AddDiagnostic($"Texture 0x{write.TextureTag:X8} for a live override: {error.Message}");
            return null;
        }
    }

    /** Releases the uploads no current override names; every material that bound them was put back first. */
    private void ReleaseOverrideTextures()
    {
        if (overrideTextures.Count == 0) return;
        var named = appliedOverrides.Values.SelectMany(value => value.Textures).Select(write => write.TextureTag).ToHashSet();
        foreach (uint tag in overrideTextures.Keys.Where(tag => !named.Contains(tag)).ToArray())
        {
            overrideTextures[tag].Dispose();
            overrideTextures.Remove(tag);
        }
    }

    /** Disposes and clears the GPU textures made for overrides. */
    private void DisposeLiveOverrides()
    {
        foreach (var texture in overrideTextures.Values) texture.Dispose();
        overrideTextures.Clear();
        overrideSource?.Forget(this);
        overrideSource = null;
    }
}
