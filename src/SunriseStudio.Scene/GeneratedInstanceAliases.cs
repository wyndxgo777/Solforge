using System.Numerics;
using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

/**
 * Alias part of AssetSceneLoader: tracks replaced static instances so the original editable row stays
 * while the renderer reads compact native bytes.
 */
public sealed partial class AssetSceneLoader
{
    private PackagePlacementOrigin? staticPlacement;

    /** Replacement aliases keep the original editable row while the renderer reads compact native bytes. */
    private (uint Tag, int Index) InstanceOrigin(uint tag, int index) =>
        catalog.TryGetInstanceOrigin(tag, index, out var origin) ? (origin.SourceTag, origin.SourceIndex) : (tag, index);

    private bool ReplacedInstance(uint tag, int index) => staticPlacement is { } placement
        && catalog.IsReplacedInstance(tag, index, placement.SourceTag, placement.SourceOffset);

    /** Placement scope follows only the static constructor edge and is restored after nested visits. */
    private void VisitStaticPlacement(uint list, int row, int index, uint owner, uint bubble,
        Matrix4x4 transform, int depth)
    {
        var previous = staticPlacement;
        staticPlacement = catalog.TryGetPlacementOrigin(list, row, out var origin)
            ? origin : new(list, row, index);
        try { Visit(owner, bubble, transform, depth); }
        finally { staticPlacement = previous; }
    }
}
