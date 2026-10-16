using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace SunriseStudio.Rendering;

/**
 * Records validated native draws, uploads all instance vectors once, then executes them in order.
 * Pipelines and bindings must have passed NativeMaterialPipeline.Validate before they are recorded.
 */
public sealed unsafe class NativeDrawList : IDisposable
{
    private readonly record struct Item(NativeMaterialPipeline Pipeline, NativeGpuMesh Mesh,
        NativeDrawBindings Bindings, ID3D11ShaderResourceView? VertexResource0, int FirstVector, uint Instances,
        bool OverridePixel14, ID3D11ShaderResourceView? Pixel14, uint Window, int InstanceStreams, int Stride, int StreamBase);

    // The instance slot is CB11; a draw window is at least 64 vectors, 16 static instances of four vectors each.
    private const uint InstanceSlot = 11, WindowVectors = 64;
    // A skinned palette widens its window up to the D3D11 constant buffer limit.
    private const uint MaximumWindowVectors = 4096;
    // Constant-buffer offsets are counted in vectors and must be multiples of 16.
    private const int OffsetAlignment = 16;
    private readonly Dx11Renderer renderer;
    private readonly ID3D11DeviceContext1? context1;
    private readonly List<Item>[] passes;
    private Vector4[] vectors = new Vector4[4096];
    private int used;
    private ID3D11Buffer? buffer;
    private int capacity;
    // Instance streams read the same vectors through a vertex buffer, one vector per stream.
    private ID3D11Buffer? streamBuffer;
    private int streamCapacity;
    private bool hasStreams;
    private readonly NativeConstantBuffer fallback;
    private bool disposed;

    /** True when the device binds a window of one large buffer; otherwise each draw updates a small one. */
    public bool UsesOffsets => context1 is not null;
    public int DrawCount { get; private set; }
    public long IndexCount { get; private set; }

    public NativeDrawList(Dx11Renderer renderer, int passCount)
    {
        this.renderer = renderer;
        passes = Enumerable.Range(0, passCount).Select(_ => new List<Item>()).ToArray();
        var options = renderer.Device.CheckFeatureOptions();
        if (options.ConstantBufferOffsetting) context1 = renderer.Context.QueryInterfaceOrNull<ID3D11DeviceContext1>();
        fallback = renderer.CreateNativeConstantBuffer(new byte[MaximumWindowVectors * 16]);
    }

    /** Empties every pass and resets the instance-vector reservation. */
    public void Clear()
    {
        foreach (var pass in passes) pass.Clear();
        used = 0;
        hasStreams = false;
    }

    /**
     * Records one draw; the CB11 window holds vectorsPerInstance vectors per instance.
     * @param pixel14 Replaces the material's pixel t14 when overridePixel14 is set; null unbinds it.
     * @param instanceStreams Vertex slots after the mesh streams that read vector k of each instance.
     * @param streamBase Vectors before the first instance; the CB11 window reads them as its first rows.
     * @return The span to fill with the base and instance vectors.
     */
    public Span<Vector4> Add(int pass, NativeMaterialPipeline pipeline, NativeGpuMesh mesh,
        NativeDrawBindings bindings, ID3D11ShaderResourceView? vertexResource0, uint instances,
        int vectorsPerInstance = 4, bool overridePixel14 = false, ID3D11ShaderResourceView? pixel14 = null,
        int instanceStreams = 0, int streamBase = 0)
    {
        int first = Reserve(pass, pipeline, mesh, bindings, vertexResource0, instances, vectorsPerInstance, overridePixel14,
            pixel14, instanceStreams, streamBase);
        int count = (int)(streamBase + instances * vectorsPerInstance);
        var span = vectors.AsSpan(first, count);
        span.Clear();
        return span;
    }

    /**
     * Records one draw like Add and zeroes its padding; the caller writes all its vectors through Vectors.
     * @return The first vector of the draw.
     */
    public int Reserve(int pass, NativeMaterialPipeline pipeline, NativeGpuMesh mesh,
        NativeDrawBindings bindings, ID3D11ShaderResourceView? vertexResource0, uint instances,
        int vectorsPerInstance = 4, bool overridePixel14 = false, ID3D11ShaderResourceView? pixel14 = null,
        int instanceStreams = 0, int streamBase = 0)
    {
        if (instanceStreams > vectorsPerInstance || streamBase < 0) throw new ArgumentOutOfRangeException(nameof(instanceStreams));
        hasStreams |= instanceStreams != 0;
        if (instances == 0 || streamBase + instances * vectorsPerInstance > MaximumWindowVectors) throw new ArgumentOutOfRangeException(nameof(instances));
        int count = (int)(streamBase + instances * vectorsPerInstance);
        int reserved = (count + OffsetAlignment - 1) / OffsetAlignment * OffsetAlignment;
        uint window = Math.Max(WindowVectors, (uint)reserved);
        if (used + reserved + (int)MaximumWindowVectors > vectors.Length)
            Array.Resize(ref vectors, Math.Max(vectors.Length * 2, used + reserved + (int)MaximumWindowVectors));
        int first = used;
        used += reserved;
        passes[pass].Add(new(pipeline, mesh, bindings, vertexResource0, first, instances, overridePixel14, pixel14, window,
            instanceStreams, vectorsPerInstance, streamBase));
        vectors.AsSpan(first + count, reserved - count).Clear();
        return first;
    }

    /** Vectors of draws Reserve recorded; writers of different draws may run on different threads. */
    public Span<Vector4> Vectors(int first, int count) => vectors.AsSpan(first, count);

    /** Number of draws recorded for the pass. */
    public int ItemCount(int pass) => passes[pass].Count;

    /** One discard upload holds every recorded draw's instance vectors. */
    public void Upload()
    {
        if (hasStreams && used != 0)
        {
            if (streamBuffer is null || streamCapacity < used)
            {
                streamBuffer?.Dispose();
                streamCapacity = Math.Max(used * 3 / 2, 4096);
                streamBuffer = renderer.Device.CreateBuffer((uint)(streamCapacity * 16), BindFlags.VertexBuffer,
                    ResourceUsage.Dynamic, CpuAccessFlags.Write);
            }
            var streams = renderer.Context.Map(streamBuffer, 0, MapMode.WriteDiscard, MapFlags.None);
            try { vectors.AsSpan(0, used).CopyTo(new Span<Vector4>((void*)streams.DataPointer, used)); }
            finally { renderer.Context.Unmap(streamBuffer, 0); }
        }
        if (context1 is null || used == 0) return;
        int needed = used + (int)MaximumWindowVectors;
        if (buffer is null || capacity < needed)
        {
            buffer?.Dispose();
            capacity = Math.Max(needed * 3 / 2, 65536);
            buffer = renderer.Device.CreateBuffer((uint)(capacity * 16), BindFlags.ConstantBuffer,
                ResourceUsage.Dynamic, CpuAccessFlags.Write);
        }
        var mapped = renderer.Context.Map(buffer, 0, MapMode.WriteDiscard, MapFlags.None);
        try { vectors.AsSpan(0, used).CopyTo(new Span<Vector4>((void*)mapped.DataPointer, used)); }
        finally { renderer.Context.Unmap(buffer, 0); }
    }

    /**
     * State is bound only when it differs from the previous draw of this pass.
     * @param minDepth Viewport minimum depth; one puts every fragment at the far plane.
     */
    public void Execute(int pass, NativeRenderTargets targets, float minDepth = 0)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var context = renderer.Context;
        context.OMSetRenderTargets(targets.Views, targets.DepthView);
        context.RSSetViewport(new Viewport(0, 0, targets.Width, targets.Height, minDepth, 1));
        context.RSSetScissorRect(targets.Width, targets.Height);
        NativeMaterialPipeline? pipeline = null;
        NativeDrawBindings? bindings = null;
        NativeGpuGeometry? geometry = null;
        ID3D11ShaderResourceView? resource0 = null, pixel14 = null;
        ID3D11DepthStencilState? depth = null;
        uint stencilReference = 0;
        bool pixel14Set = false;
        Vortice.Direct3D.PrimitiveTopology topology = default;
        foreach (ref readonly var item in CollectionsMarshal.AsSpan(passes[pass]))
        {
            if (!ReferenceEquals(item.Pipeline, pipeline))
            {
                pipeline = item.Pipeline;
                context.RSSetState(pipeline.Rasterizer);
                context.OMSetBlendState(pipeline.Blend, new Color4(1, 1, 1, 1), uint.MaxValue);
                context.IASetInputLayout(pipeline.Layout);
                context.VSSetShader(pipeline.VertexShader);
                context.PSSetShader(pipeline.PixelShader);
            }
            if (!ReferenceEquals(item.Bindings, bindings))
            {
                bindings = item.Bindings;
                foreach (var entry in bindings.ConstantBuffers)
                    if (entry.Key.Stage == GameShaderStage.Vertex && entry.Key.Slot != InstanceSlot)
                        context.VSSetConstantBuffer(entry.Key.Slot, entry.Value.Buffer);
                    else if (entry.Key.Stage == GameShaderStage.Pixel) context.PSSetConstantBuffer(entry.Key.Slot, entry.Value.Buffer);
                foreach (var entry in bindings.Resources)
                    if (entry.Key.Stage == GameShaderStage.Vertex) context.VSSetShaderResource(entry.Key.Slot, entry.Value);
                    else if (entry.Key.Stage == GameShaderStage.Pixel) context.PSSetShaderResource(entry.Key.Slot, entry.Value);
                foreach (var entry in bindings.Samplers)
                    if (entry.Key.Stage == GameShaderStage.Vertex) context.VSSetSampler(entry.Key.Slot, entry.Value);
                    else if (entry.Key.Stage == GameShaderStage.Pixel) context.PSSetSampler(entry.Key.Slot, entry.Value);
                resource0 = null; pixel14Set = false;
            }
            // The reference comes from the draw bindings; the state from the pipeline.
            uint reference = item.Bindings.StencilReference ?? 0;
            if (!ReferenceEquals(item.Pipeline.Depth, depth) || reference != stencilReference)
            {
                context.OMSetDepthStencilState(item.Pipeline.Depth, reference);
                depth = item.Pipeline.Depth; stencilReference = reference;
            }
            if (item.OverridePixel14 && (!pixel14Set || !ReferenceEquals(item.Pixel14, pixel14)))
            {
                context.PSSetShaderResource(14, item.Pixel14!);
                pixel14 = item.Pixel14; pixel14Set = true;
            }
            if (item.VertexResource0 is { } view && !ReferenceEquals(view, resource0))
            {
                context.VSSetShaderResource(0, view);
                resource0 = view;
            }
            if (!ReferenceEquals(item.Mesh.Geometry, geometry))
            {
                geometry = item.Mesh.Geometry;
                for (uint i = 0; i < geometry.Streams.Length; i++)
                    context.IASetVertexBuffer(i, geometry.Streams[i], geometry.Strides[i]);
                context.IASetIndexBuffer(geometry.Indices, geometry.IndexFormat, 0);
            }
            // Stream k after the mesh streams reads vector k of each instance; the stride spans all vectors.
            for (int stream = 0; stream < item.InstanceStreams; stream++)
                context.IASetVertexBuffer((uint)(geometry.Streams.Length + stream), streamBuffer!,
                    (uint)(item.Stride * 16), (uint)((item.FirstVector + item.StreamBase + stream) * 16));
            if (item.Mesh.Topology != topology)
            {
                topology = item.Mesh.Topology;
                context.IASetPrimitiveTopology(topology);
            }
            if (context1 is not null)
            {
                nint native = buffer!.NativePointer;
                uint firstConstant = (uint)item.FirstVector, constants = item.Window;
                SetConstantWindow(context1, InstanceSlot, 1, &native, &firstConstant, &constants);
            }
            else
            {
                renderer.UpdateNativeConstantBuffer(fallback, MemoryMarshal.AsBytes(
                    vectors.AsSpan(item.FirstVector, (int)MaximumWindowVectors)));
                context.VSSetConstantBuffer(InstanceSlot, fallback.Buffer);
            }
            context.DrawIndexedInstanced(item.Mesh.IndexCount, item.Instances, item.Mesh.FirstIndex, 0, 0);
            DrawCount++;
            IndexCount += (long)item.Mesh.IndexCount * item.Instances;
        }
        for (uint slot = 0; slot < 16; slot++) context.PSSetShaderResource(slot, null!);
        context.VSSetShaderResource(0, null!);
    }

    /** Zeroes the draw and index counters, e.g. at the start of a frame. */
    public void ResetCounters() { DrawCount = 0; IndexCount = 0; }

    // The pointer form of VSSetConstantBuffers1; the public overloads marshal a buffer array on every draw.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "VSSetConstantBuffers1")]
    private static extern void SetConstantWindow(ID3D11DeviceContext1 context, uint startSlot, uint numBuffers,
        void* buffers, void* firstConstants, void* numConstants);

    /** Releases the constant buffers and the draw list's context interface. */
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        buffer?.Dispose(); streamBuffer?.Dispose(); fallback.Dispose(); context1?.Dispose();
    }
}
