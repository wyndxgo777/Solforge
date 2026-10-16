using System.Numerics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SunriseStudio.Rendering;

/** A swap chain for another window on the renderer's device. Draw to it only inside a window frame. */
public sealed class WindowTarget : IDisposable
{
    internal IDXGISwapChain1 SwapChain { get; }
    internal ID3D11Texture2D Color { get; set; } = null!;
    internal ID3D11RenderTargetView ColorView { get; set; } = null!;
    internal ID3D11Texture2D Depth { get; set; } = null!;
    internal ID3D11DepthStencilView DepthView { get; set; } = null!;
    public int Width { get; internal set; }
    public int Height { get; internal set; }

    internal WindowTarget(IDXGISwapChain1 swapChain) => SwapChain = swapChain;

    /** Drops the colour and depth views so a resize can recreate them. */
    internal void ReleaseViews()
    {
        ColorView?.Dispose(); Color?.Dispose(); DepthView?.Dispose(); Depth?.Dispose();
    }

    /** Releases the views and the swap chain. */
    public void Dispose()
    {
        ReleaseViews();
        SwapChain.Dispose();
    }
}

/** Secondary-window part of Dx11Renderer: swap chains and frames for extra windows on the shared device. */
public sealed unsafe partial class Dx11Renderer
{
    private (ID3D11Texture2D Color, ID3D11RenderTargetView ColorView, ID3D11Texture2D Depth,
        ID3D11DepthStencilView DepthView, int Width, int Height)? mainTarget;

    /** Creates a swap chain for a second window; the caller disposes it before the renderer. */
    public WindowTarget CreateWindowTarget(IntPtr windowHandle, int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        var swap = factory.CreateSwapChainForHwnd(Device, windowHandle, new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = SampleDescription.Default,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore
        });
        factory.MakeWindowAssociation(windowHandle, WindowAssociationFlags.IgnoreAltEnter);
        var target = new WindowTarget(swap) { Width = width, Height = height };
        CreateWindowViews(target);
        return target;
    }

    /**
     * Makes the window the render target until EndWindowFrame. Offscreen passes restore to it, so
     * views drawn in this frame land in that window.
     */
    public void BeginWindowFrame(WindowTarget target, int width, int height, Vector4 clear)
    {
        if (mainTarget is not null) throw new InvalidOperationException("End the open window frame first.");
        width = Math.Max(1, width); height = Math.Max(1, height);
        if (width != target.Width || height != target.Height)
        {
            Context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            target.ReleaseViews();
            target.SwapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
            target.Width = width; target.Height = height;
            CreateWindowViews(target);
        }
        mainTarget = (color, colorView, depth, depthView, Width, Height);
        (color, colorView, depth, depthView) = (target.Color, target.ColorView, target.Depth, target.DepthView);
        Width = target.Width; Height = target.Height;
        BeginFrame(clear);
    }

    /** Presents without waiting for vertical sync, so several windows do not divide the frame rate. */
    public void EndWindowFrame(WindowTarget target)
    {
        if (mainTarget is not { } main) throw new InvalidOperationException("No window frame is open.");
        try { target.SwapChain.Present(0, PresentFlags.None).CheckError(); }
        finally
        {
            (color, colorView, depth, depthView, Width, Height) = main;
            mainTarget = null;
            RestoreMainTarget();
        }
    }

    private void CreateWindowViews(WindowTarget target)
    {
        target.Color = target.SwapChain.GetBuffer<ID3D11Texture2D>(0);
        target.ColorView = Device.CreateRenderTargetView(target.Color);
        target.Depth = Device.CreateTexture2D(Format.D32_Float, (uint)target.Width, (uint)target.Height,
            mipLevels: 1, bindFlags: BindFlags.DepthStencil);
        target.DepthView = Device.CreateDepthStencilView(target.Depth);
    }
}
