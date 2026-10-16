using System.Numerics;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Colour and font tokens the editor pages use, backed by SolTheme. */
public static class StudioTheme
{
    // Solforge: the upstream names now read the Sol tokens (SolTheme, Studio.md §3.10).
    public static readonly Vector4 Accent = SolTheme.Sun;
    public static readonly Vector4 Muted = SolTheme.Dim;
    public static readonly Vector4 Canvas = SolTheme.Bg;
    public static readonly Vector4 Panel = SolTheme.Panel;

    /** Item name colour by rarity tier: 2 uncommon, 3 rare, 4 legendary, 5 exotic; others are plain. */
    public static Vector4 Rarity(int tier) => tier switch
    {
        2 => new(.5f, .8f, .5f, 1), 3 => new(.4f, .65f, 1, 1), 4 => new(.8f, .55f, 1, 1), 5 => new(1, .8f, .3f, 1),
        _ => new(.85f, .85f, .85f, 1)
    };

    /** Solforge: the Sol theme (SolTheme) replaces the Sunrise palette; safe to call again on a DPI change. */
    public static void Apply(float scale) => SolTheme.Apply(scale);
}
