using System.Numerics;

namespace SunriseStudio.Formats;

/** How a widget is positioned along one axis inside its parent. */
public enum CuiAnchor { Stored, Start, Center, End, Stretch }

/** Anchoring rules of a widget: per-axis anchor, margin (start/end per axis), minimum size and optional maximum size. */
public sealed record CuiAnchors(CuiAnchor Horizontal = CuiAnchor.Start, CuiAnchor Vertical = CuiAnchor.Start,
    Vector4 Margin = default, Vector2 Minimum = default, Vector2? Maximum = null);

/** Container anchors use parent padding, child margins and authored size limits. */
public static class CuiLayoutRules
{
    /** Resolves a stored rectangle (x, y, width, height) against the parent's size and padding, clamping size and position per axis. */
    public static Vector4 Resolve(Vector2 parentSize, Vector4 parentPadding, Vector4 stored, CuiAnchors anchors)
    {
        var maximum = anchors.Maximum ?? new Vector2(float.MaxValue);
        var horizontal = Axis(parentSize.X, parentPadding.X, parentPadding.Z, stored.X, stored.Z,
            anchors.Margin.X, anchors.Margin.Z, anchors.Minimum.X, maximum.X, anchors.Horizontal);
        var vertical = Axis(parentSize.Y, parentPadding.Y, parentPadding.W, stored.Y, stored.W,
            anchors.Margin.Y, anchors.Margin.W, anchors.Minimum.Y, maximum.Y, anchors.Vertical);
        return new(horizontal.Position, vertical.Position, horizontal.Size, vertical.Size);
    }

    private static (float Position, float Size) Axis(float parent, float paddingStart, float paddingEnd,
        float position, float size, float marginStart, float marginEnd, float minimum, float maximum, CuiAnchor anchor)
    {
        float limit = Math.Max(minimum, maximum - paddingStart - paddingEnd);
        size = Math.Clamp(size, minimum, limit);
        if (paddingStart != 0 || paddingEnd != 0 || marginStart != 0 || marginEnd != 0)
            position = Math.Clamp(position, paddingStart + marginStart,
                Math.Max(paddingStart + marginStart, parent - paddingEnd - marginEnd));
        switch (anchor)
        {
            case CuiAnchor.Start: position = paddingStart + marginStart; break;
            case CuiAnchor.Center: position = (parent - size) * .5f; break;
            case CuiAnchor.End: position = parent - size - marginEnd - paddingEnd; break;
            case CuiAnchor.Stretch:
                position = paddingStart + marginStart;
                size = Math.Clamp(parent - paddingStart - paddingEnd - marginStart - marginEnd,
                    minimum, Math.Max(minimum, maximum));
                break;
        }
        return (MathF.Round(position), MathF.Round(size));
    }
}
