using Vortice.DXGI;

namespace SunriseStudio.Rendering;

/** Solforge Phase 10: a running triangle count for the viewport statistics overlay; Phase 14: video memory in use. */
public sealed partial class Dx11Renderer
{
    /** Triangles submitted by indexed mesh draws since the renderer was created; callers take differences. */
    public long Triangles { get; private set; }

    /** Local video memory this process has committed on the adapter (DXGI 1.4); -1 when the adapter cannot say. */
    public long VideoMemoryUsage
    {
        get
        {
            using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var adapter3 = adapter.QueryInterfaceOrNull<IDXGIAdapter3>();
            return adapter3 is null ? -1 : (long)adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).CurrentUsage;
        }
    }

    private void CountTriangles(long indexCount, long instances = 1) => Triangles += indexCount / 3 * Math.Max(0, instances);
}
