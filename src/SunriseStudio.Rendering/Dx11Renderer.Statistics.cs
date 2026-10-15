namespace SunriseStudio.Rendering;

/** Solforge Phase 10: a running triangle count for the viewport statistics overlay. */
public sealed partial class Dx11Renderer
{
    /** Triangles submitted by indexed mesh draws since the renderer was created; callers take differences. */
    public long Triangles { get; private set; }

    private void CountTriangles(long indexCount, long instances = 1) => Triangles += indexCount / 3 * Math.Max(0, instances);
}
