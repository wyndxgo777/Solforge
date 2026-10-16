using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** How much the 3D viewport draws: each step adds cost and detail. */
public enum ViewportQuality
{
    /** Flat grey geometry, coarsest static levels and a short far cull; no game shading, effects or decals. */
    Low,
    /** Colour-textured geometry at coarse static levels; no game shading, effects or decals. */
    Medium,
    /** Game shaders and lighting (when on) at the game's own distance levels; no particle effects or decals. */
    High,
    /** Everything: the most detailed level at every distance, game shading, decals and particle effects. */
    Full
}

public sealed partial class AssetViewport
{
    /** Asset views keep Full; the host lowers it for the map viewport. */
    public ViewportQuality Quality { get; set; } = ViewportQuality.Full;

    /** Scales the game's level switch distances; smaller switches to coarser levels and culls sooner. */
    private float QualityLodScale => Quality switch
    {
        ViewportQuality.Low => .15f,
        ViewportQuality.Medium => .4f,
        _ => 1
    };

    /** True when this frame is drawn by the native game-shader path. */
    private bool NativeActive => useGameShaders && !wireframe && Quality >= ViewportQuality.High
        && nativeScene is not null && NativePartCount > 0;

    /** Fallback geometry is drawn with its material's colour texture from Medium up. */
    private bool FallbackTextured => Quality >= ViewportQuality.Medium;

    /** The game's distance levels, made coarser for Low and Medium. */
    private StaticLodView QualityLod(StaticLodView view)
    {
        float scale = QualityLodScale;
        return scale == 1 ? view : view with { K0 = view.K0 * scale, K1 = view.K1 * scale, K2 = view.K2 * scale };
    }
}
