using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace SunriseStudio.Rendering;

/** The caller owns one line stream; camera movement changes only its draw matrix. */
public sealed class GpuLineMesh : IDisposable
{
    internal Dx11Renderer Owner { get; }
    internal ID3D11Buffer? Buffer;
    internal byte[] Snapshot = [];
    internal int UploadedCount;
    internal int Capacity;
    internal bool Disposed;
    public int Count { get; internal set; }
    public int Uploads { get; internal set; }
    internal GpuLineMesh(Dx11Renderer owner) => Owner = owner;

    /** Releases the line vertex buffer. */
    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        Buffer?.Dispose(); Buffer = null; Snapshot = [];
        Count = UploadedCount = Capacity = 0;
    }
}

/** Line-mesh part of Dx11Renderer. */
public sealed unsafe partial class Dx11Renderer
{
    /** Creates an empty line mesh owned by this renderer. */
    public GpuLineMesh CreateLineMesh() => new(this);

    /** Reuse requires equal vertex bytes and order; depth and camera state are bound on every draw. */
    public void DrawLines(GpuLineMesh mesh, ReadOnlySpan<LineVertex> vertices,
        Matrix4x4 viewProjection, bool depthTest = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ObjectDisposedException.ThrowIf(mesh.Disposed, mesh);
        if (!ReferenceEquals(mesh.Owner, this)) throw new ArgumentException("The line mesh belongs to another renderer.", nameof(mesh));
        if (vertices.Length % 2 != 0) throw new ArgumentException("Lines need vertex pairs.");
        if (vertices.IsEmpty) { mesh.Count = 0; return; }
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(vertices);
        if (mesh.UploadedCount != vertices.Length || !bytes.SequenceEqual(mesh.Snapshot.AsSpan(0, bytes.Length)))
        {
            ID3D11Buffer? next = null;
            int capacity = mesh.Capacity;
            if (capacity < vertices.Length)
            {
                // Grows by half again, so a slowly growing stream reallocates rarely.
                capacity = checked(Math.Max(vertices.Length + 256, capacity + capacity / 2));
                next = Device.CreateBuffer(checked((uint)(capacity * sizeof(LineVertex))),
                    BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
            }
            byte[] snapshot = mesh.Snapshot.Length < bytes.Length ? new byte[checked(capacity * sizeof(LineVertex))] : mesh.Snapshot;
            try { UploadLineVertices(next ?? mesh.Buffer!, capacity, vertices); }
            catch { next?.Dispose(); throw; }
            if (next is not null) { mesh.Buffer?.Dispose(); mesh.Buffer = next; mesh.Capacity = capacity; }
            bytes.CopyTo(snapshot); mesh.Snapshot = snapshot; mesh.UploadedCount = vertices.Length; mesh.Uploads++;
        }
        mesh.Count = vertices.Length;
        DrawLineBuffer(mesh.Buffer!, mesh.Count, viewProjection, depthTest);
    }
}
