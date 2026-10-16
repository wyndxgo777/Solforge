using Vortice.Direct3D11;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace SunriseStudio.Rendering;

/**
 * Continuous back-buffer capture for the MCP live viewport without stalling the frame: GrabCycle copies the back
 * buffer into one of four staging textures (a GPU-side copy, queued like any draw) and reads a copy back only once
 * it is three frames old, when the GPU has long finished it, so Map does not wait. Call it after the frame is drawn
 * and before Present (flip-discard does not keep presented buffers), on the render thread.
 */
public sealed unsafe partial class Dx11Renderer
{
    /** Top-down BGRA rows of the cropped region, Width * 4 bytes each. */
    public sealed record GrabbedPixels(byte[] Bgra, int Width, int Height);

    private const int GrabSlots = 4, GrabLatency = 3;
    private readonly ID3D11Texture2D?[] grabTextures = new ID3D11Texture2D?[GrabSlots];
    private readonly (long Frame, System.Drawing.Rectangle Crop, bool Busy)[] grabState = new (long, System.Drawing.Rectangle, bool)[GrabSlots];
    private long grabFrame;
    private int grabNext;

    /**
     * One step per drawn frame: queue a copy of crop (back-buffer pixels) when given, and return the oldest
     * finished copy, if any. Pass null on frames that should not be captured; pending copies still drain.
     */
    public GrabbedPixels? GrabCycle(System.Drawing.Rectangle? crop)
    {
        if (disposed) return null;
        grabFrame++;
        GrabbedPixels? ready = null;
        int oldest = -1;
        for (int slot = 0; slot < GrabSlots; slot++)
            if (grabState[slot].Busy && grabFrame - grabState[slot].Frame >= GrabLatency
                && (oldest < 0 || grabState[slot].Frame < grabState[oldest].Frame))
                oldest = slot;
        if (oldest >= 0)
        {
            ready = ReadGrab(oldest);
            grabState[oldest].Busy = false;
        }
        if (crop is { } wanted && !grabState[grabNext].Busy)
        {
            var area = System.Drawing.Rectangle.Intersect(wanted, new System.Drawing.Rectangle(0, 0, Width, Height));
            if (area.Width >= 2 && area.Height >= 2 && EnsureGrabTexture(grabNext) is { } staging)
            {
                Context.CopyResource(staging, color);
                grabState[grabNext] = (grabFrame, area, true);
                grabNext = (grabNext + 1) % GrabSlots;
            }
        }
        return ready;
    }

    /** Frees the staging textures (capture idle, resize or shutdown). */
    public void ReleaseGrabs()
    {
        for (int slot = 0; slot < GrabSlots; slot++)
        {
            grabTextures[slot]?.Dispose();
            grabTextures[slot] = null;
            grabState[slot] = default;
        }
    }

    private ID3D11Texture2D? EnsureGrabTexture(int slot)
    {
        var description = color.Description;
        if (description.SampleDescription.Count != 1) return null;
        if (grabTextures[slot] is { } existing)
        {
            var have = existing.Description;
            if (have.Width == description.Width && have.Height == description.Height && have.Format == description.Format) return existing;
            existing.Dispose();
            grabTextures[slot] = null;
        }
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.MiscFlags = ResourceOptionFlags.None;
        return grabTextures[slot] = Device.CreateTexture2D(description);
    }

    private GrabbedPixels? ReadGrab(int slot)
    {
        if (grabTextures[slot] is not { } staging) return null;
        var crop = grabState[slot].Crop;
        var size = staging.Description;
        // A resize since the copy: the crop may no longer fit the texture.
        if (crop.Right > (int)size.Width || crop.Bottom > (int)size.Height) return null;
        var mapped = Context.Map(staging, 0, MapMode.Read, MapFlags.None);
        try
        {
            var pixels = new byte[crop.Width * crop.Height * 4];
            fixed (byte* target = pixels)
                for (int y = 0; y < crop.Height; y++)
                    new ReadOnlySpan<byte>((byte*)mapped.DataPointer + (long)(crop.Y + y) * mapped.RowPitch + crop.X * 4L, crop.Width * 4)
                        .CopyTo(new Span<byte>(target + (long)y * crop.Width * 4, crop.Width * 4));
            return new GrabbedPixels(pixels, crop.Width, crop.Height);
        }
        finally { Context.Unmap(staging, 0); }
    }
}
