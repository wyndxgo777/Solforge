using System.Runtime.CompilerServices;
using SunriseStudio.Rendering;

namespace SunriseStudio.Viewport;

/** Memory half of AssetViewport: GPU byte accounting and releasing the native scene. */
public sealed partial class AssetViewport
{
    /** Uploaded stand-in meshes: count and GPU bytes (vertex and index buffers). */
    public (int Meshes, long Bytes) FallbackGeometry
    {
        get
        {
            long bytes = 0;
            foreach (var mesh in meshes.Values)
                bytes += (long)mesh.VertexCount * Unsafe.SizeOf<RenderVertex>() + (long)mesh.IndexCount * sizeof(uint);
            return (meshes.Count, bytes);
        }
    }

    /**
     * Frees the game-shader renderer and its GPU textures and buffers; the scene keeps drawing with the stand-in path,
     * as it does below High quality. Call on the graphics thread; SetNativeScene restores it.
     */
    public void ReleaseNativeScene()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (nativeScene is null) return;
        nativeScene.ClearLivePalettes();
        if (Scene is not null) ReleaseRuntimeClasses();
        LivePrototypeRevision++;
        nativeScene.Dispose();
        nativeScene = null;
        atmosphereBubble = 0;
        nativeHandles.Clear();
        nativeObjectHandles.Clear();
        nativeFallback.Clear();
        NativeFallbackPartCount = 0;
        NativeError = null;
        nativePoseError = null;
        cullReusable = false;
        renderIndexDirty = true;
    }
}
