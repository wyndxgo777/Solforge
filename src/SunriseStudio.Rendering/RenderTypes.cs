using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace SunriseStudio.Rendering;

/** Interleaved vertex of lit meshes. This file also holds the GPU mesh and texture handles. */
[StructLayout(LayoutKind.Sequential)]
public readonly record struct RenderVertex(Vector3 Position, Vector3 Normal, Vector2 TexCoord);

/** Vertex of line meshes: position and RGBA colour. */
[StructLayout(LayoutKind.Sequential)]
public readonly record struct LineVertex(Vector3 Position, Vector4 Color);

/** The caller owns mesh buffers and must dispose them before the renderer. */
public sealed class GpuMesh : IDisposable
{
    internal ID3D11Buffer Vertices { get; }
    internal ID3D11Buffer Indices { get; }
    public uint IndexCount { get; }
    public uint VertexCount { get; }

    internal GpuMesh(ID3D11Buffer vertices, ID3D11Buffer indices, uint vertexCount, uint indexCount)
    {
        Vertices = vertices;
        Indices = indices;
        IndexCount = indexCount;
        VertexCount = vertexCount;
    }

    /** Releases the vertex and index buffers. */
    public void Dispose()
    {
        Indices.Dispose();
        Vertices.Dispose();
    }
}

/** The shader view stays valid until this texture is disposed. */
public sealed class GpuTexture : IDisposable
{
    internal ID3D11Resource Resource { get; }
    public ID3D11ShaderResourceView View { get; }
    public IntPtr ImGuiId => Dimension == ShaderResourceViewDimension.Texture2D ? View.NativePointer
        : throw new NotSupportedException("ImGui images need a selected two-dimensional texture slice.");
    public ShaderResourceViewDimension Dimension { get; }
    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }
    public int ArraySize { get; }
    public int MipLevels { get; }

    internal GpuTexture(ID3D11Resource resource, ID3D11ShaderResourceView view, int width, int height,
        int depth = 1, int arraySize = 1, int mipLevels = 1)
    {
        Resource = resource;
        View = view;
        Width = width;
        Height = height;
        Depth = depth;
        ArraySize = arraySize;
        MipLevels = mipLevels;
        Dimension = view.Description.ViewDimension;
    }

    /** Releases the texture, its resource and its view. */
    public void Dispose()
    {
        View.Dispose();
        Resource.Dispose();
    }
}
