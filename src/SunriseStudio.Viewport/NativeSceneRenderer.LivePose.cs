using System.Numerics;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** Palette rows and centre for a live-posed entity. */
public sealed record NativeLivePalette(NativeEntityPalette Kind, IReadOnlyList<Vector4> Rows, Vector3 Center);

/** Live pose half of NativeSceneRenderer: per-instance palettes that replace authored ones. */
public sealed partial class NativeSceneRenderer
{
    private readonly Dictionary<MeshInstance, NativeLivePalette> livePalettes = [];
    private readonly Dictionary<(MeshInstance, NativeEntityPalette), bool> livePaletteAdmission = [];
    public int LivePaletteCount => livePalettes.Count;

    /** Captured world palettes replace only admitted scope-7 shaders; no placement is applied twice. */
    public bool CanApplyLivePalette(MeshInstance instance, NativeEntityPalette kind)
    {
        if (livePaletteAdmission.TryGetValue((instance, kind), out bool allowed)) return allowed;
        return livePaletteAdmission[(instance, kind)] = kind != NativeEntityPalette.None && instanceGroups.TryGetValue(instance, out var matching)
        && matching.Count > 0 && matching.All(group => group.Source.Kind == NativeMeshKind.Entity
            && group.Source.Material.Palette == kind && !group.Source.Material.ReadsWaterObject
            && (group.Source.Material.Execution.Pixel.Shader is not { } pixel
                || !NativeShaderUsage.Get(Plan.Reader.ReadShaderBytecode(pixel)).Words.ContainsKey(11)
                    && !NativeShaderUsage.Get(Plan.Reader.ReadShaderBytecode(pixel)).DynamicSlots.Contains(11)));
    }

    /** Validation precedes publication; caller arrays cannot later alter the active constants. */
    public void SetLivePalettes(IReadOnlyDictionary<MeshInstance, NativeLivePalette> palettes)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var next = new Dictionary<MeshInstance, NativeLivePalette>();
        foreach (var (instance, palette) in palettes)
        {
            int stride = palette.Kind == NativeEntityPalette.Matrix ? 3 : 2;
            if (!CanApplyLivePalette(instance, palette.Kind) || palette.Rows.Count is < 10 or > NativeEntityPalettes.MaximumRows
                || (palette.Rows.Count - 8) % stride != 0 || palette.Rows.Any(row =>
                    !float.IsFinite(row.X) || !float.IsFinite(row.Y) || !float.IsFinite(row.Z) || !float.IsFinite(row.W))
                || !float.IsFinite(palette.Center.X + palette.Center.Y + palette.Center.Z))
                throw new NotSupportedException("This mesh cannot use the captured native palette.");
            int count = Math.Max(NativeEntityPalettes.Rows, (palette.Rows.Count + 15) / 16 * 16);
            var rows = new Vector4[count];
            for (int i = 0; i < palette.Rows.Count; i++) rows[i] = palette.Rows[i];
            next.Add(instance, palette with { Rows = Array.AsReadOnly(rows) });
        }
        var changed = livePalettes.Keys.Concat(next.Keys).Distinct().ToArray();
        livePalettes.Clear();
        foreach (var pair in next) livePalettes.Add(pair.Key, pair.Value);
        RefreshLiveTransforms(changed);
    }

    /** Drops every live palette. */
    public void ClearLivePalettes() { if (livePalettes.Count > 0) SetLivePalettes(new Dictionary<MeshInstance, NativeLivePalette>()); }
    /** Drops the live palettes of the given instances and refreshes their transforms. */
    public void RemoveLivePalettes(IEnumerable<MeshInstance> instances)
    {
        var removed = instances.Where(livePalettes.ContainsKey).ToArray();
        foreach (var instance in removed) livePalettes.Remove(instance);
        if (removed.Length > 0) RefreshLiveTransforms(removed);
    }
}
