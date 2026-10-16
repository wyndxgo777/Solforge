using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Rendering;

namespace SunriseStudio.Viewport;

/** Where the player is in game, in map space: feet position, yaw in degrees (atan2 of the facing) and the breadcrumb trail. */
public sealed record PlayerMarker(Vector3 Position, float? Yaw, IReadOnlyList<Vector3> Trail, string Source);

/** The live player marker: a ring at the feet, a pole, a facing arrow and a fading trail, drawn through scene depth. */
public sealed partial class AssetViewport
{
    /** Set every frame by the editor; null hides the marker and the follow toggle. */
    public PlayerMarker? PlayerMarker { get; set; }
    /** Keeps the orbit centre on the player while a marker is shown. */
    public bool FollowPlayer { get; set; }

    private void DrawPlayerToggle(Action<float> next, float button)
    {
        if (PlayerMarker is null && !FollowPlayer) return;
        next(button);
        bool follow = FollowPlayer;
        if (ImGuiIcons.Toggle("follow-player", StudioIcon.Player, PlayerMarker is { } marker
                ? $"Follow the player ({marker.Source}); the camera keeps its angle and distance" : "Follow the player (no position now)", ref follow))
            FollowPlayer = follow;
    }

    private void DrawPlayer(Matrix4x4 view, int height)
    {
        if (PlayerMarker is not { } marker) return;
        const int ringSegments = 24;
        // At least half a metre, else a fixed pixel size at the orbit distance.
        float radius = MathF.Max(.5f, Camera.ViewHeight / Math.Max(1, height) * 10);
        var color = new Vector4(1, .55f, .1f, 1);
        var feet = marker.Position;
        overlayLines.Clear();
        for (int segment = 0; segment < ringSegments; segment++)
        {
            overlayLines.Add(new(feet + Ring(segment), color));
            overlayLines.Add(new(feet + Ring(segment + 1), color));
        }
        overlayLines.Add(new(feet, color));
        overlayLines.Add(new(feet + new Vector3(0, 0, MathF.Max(2, radius * 3)), color));
        if (marker.Yaw is { } yaw)
        {
            var facing = new Vector3(MathF.Cos(yaw * MathF.PI / 180), MathF.Sin(yaw * MathF.PI / 180), 0);
            var side = new Vector3(-facing.Y, facing.X, 0);
            var tip = feet + facing * radius * 2.5f;
            var white = new Vector4(1, 1, 1, 1);
            overlayLines.Add(new(feet, white)); overlayLines.Add(new(tip, white));
            overlayLines.Add(new(tip, white)); overlayLines.Add(new(tip - facing * radius * .8f + side * radius * .5f, white));
            overlayLines.Add(new(tip, white)); overlayLines.Add(new(tip - facing * radius * .8f - side * radius * .5f, white));
        }
        var trail = marker.Trail;
        for (int i = 1; i < trail.Count; i++)
        {
            // Older points fade toward the scene.
            float age = (float)i / trail.Count;
            var tint = new Vector4(1, .8f, .3f, 1) * new Vector4(.35f + .65f * age, .35f + .65f * age, .35f + .65f * age, 1);
            overlayLines.Add(new(trail[i - 1] + new Vector3(0, 0, .1f), tint));
            overlayLines.Add(new(trail[i] + new Vector3(0, 0, .1f), tint));
        }
        DrawOverlayLines(LineStream.Player, CollectionsMarshal.AsSpan(overlayLines), view, false);

        Vector3 Ring(int segment)
        {
            float angle = MathF.Tau * segment / ringSegments;
            return new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, .05f);
        }
    }
}
