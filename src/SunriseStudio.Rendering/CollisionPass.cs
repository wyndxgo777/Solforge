using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace SunriseStudio.Rendering;

/** One drawn collision shape: its world matrix and colour. */
[StructLayout(LayoutKind.Sequential)]
public readonly record struct CollisionInstance(Matrix4x4 World, Vector4 Tint);

/** One shape's index range in the shared geometry, drawn for a run of instances. */
public readonly record struct CollisionRange(uint FirstIndex, uint IndexCount, int BaseVertex, uint FirstInstance, uint InstanceCount);

/** Positions and triangle indices of every distinct collision shape, in one buffer pair. */
public sealed class CollisionGeometry : IDisposable
{
    internal ID3D11Buffer Vertices { get; }
    internal ID3D11Buffer Indices { get; }
    public long VertexCount { get; }
    public long IndexCount { get; }

    internal CollisionGeometry(ID3D11Buffer vertices, ID3D11Buffer indices, long vertexCount, long indexCount) =>
        (Vertices, Indices, VertexCount, IndexCount) = (vertices, indices, vertexCount, indexCount);

    /** Releases the GPU vertex and index buffers. */
    public void Dispose()
    {
        Indices.Dispose();
        Vertices.Dispose();
    }
}

/**
 * Draws collision over a composited scene: translucent faces with a flat normal from screen
 * derivatives, optional wire edges, and wire boxes. It reads the bound depth and writes none,
 * unless solid is chosen. All calls run on the window thread except CreateGeometry.
 */
public sealed unsafe class CollisionPass : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FrameConstants
    {
        public Matrix4x4 ViewProjection;
    }

    private readonly Dx11Renderer renderer;
    private readonly ID3D11VertexShader vertexShader;
    private readonly ID3D11PixelShader fillShader, edgeShader;
    private readonly ID3D11InputLayout layout;
    private readonly ID3D11Buffer constants, boxVertices;
    private readonly ID3D11RasterizerState fill, wire;
    private readonly ID3D11DepthStencilState depthRead, depthWrite, noDepth;
    private readonly ID3D11BlendState blend, opaque;
    private ID3D11Buffer? instances;
    private int capacity;
    private bool disposed;

    public CollisionPass(Dx11Renderer renderer)
    {
        this.renderer = renderer;
        var device = renderer.Device;
        var code = Dx11Renderer.Compile(Shader, "VSMain", "vs_5_0");
        vertexShader = device.CreateVertexShader(code.Span);
        fillShader = device.CreatePixelShader(Dx11Renderer.Compile(Shader, "PSFill", "ps_5_0").Span);
        edgeShader = device.CreatePixelShader(Dx11Renderer.Compile(Shader, "PSEdge", "ps_5_0").Span);
        var elements = new List<InputElementDescription> { new("POSITION", 0, Format.R32G32B32_Float, 0, 0) };
        for (uint row = 0; row < 4; row++)
            elements.Add(new("WORLD", row, Format.R32G32B32A32_Float, row * 16, 1, InputClassification.PerInstanceData, 1));
        elements.Add(new("TINT", 0, Format.R32G32B32A32_Float, 64, 1, InputClassification.PerInstanceData, 1));
        layout = device.CreateInputLayout(elements.ToArray(), code.Span);
        constants = device.CreateBuffer((uint)sizeof(FrameConstants), BindFlags.ConstantBuffer);
        // A negative bias keeps faces that coincide with render geometry in front of it.
        fill = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid)
            { DepthBias = -64, SlopeScaledDepthBias = -2, DepthClipEnable = true });
        wire = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Wireframe)
            { DepthBias = -128, SlopeScaledDepthBias = -2, DepthClipEnable = true });
        depthRead = device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual));
        depthWrite = device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.LessEqual));
        noDepth = device.CreateDepthStencilState(new DepthStencilDescription(false, DepthWriteMask.Zero, ComparisonFunction.Always));
        blend = device.CreateBlendState(new BlendDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha));
        opaque = device.CreateBlendState(new BlendDescription(Blend.One, Blend.Zero));
        // The twelve edges of the unit cube, as a line list.
        var corners = new List<Vector3>();
        for (int corner = 0; corner < 8; corner++)
            for (int bit = 1; bit <= 4; bit <<= 1)
                if ((corner & bit) == 0) { corners.Add(Corner(corner)); corners.Add(Corner(corner | bit)); }
        boxVertices = device.CreateBuffer(CollectionsMarshal.AsSpan(corners), BindFlags.VertexBuffer);
        static Vector3 Corner(int index) => new(index & 1, (index >> 1) & 1, (index >> 2) & 1);
    }

    /** Uses only the device, which is free-threaded, so a worker can upload while the window draws. */
    public CollisionGeometry CreateGeometry(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (positions.IsEmpty || indices.IsEmpty || indices.Length % 3 != 0)
            throw new ArgumentException("Collision geometry needs positions and triangle indices.");
        var vertices = renderer.Device.CreateBuffer(positions, BindFlags.VertexBuffer);
        try { return new(vertices, renderer.Device.CreateBuffer(indices, BindFlags.IndexBuffer), positions.Length, indices.Length); }
        catch { vertices.Dispose(); throw; }
    }

    /**
     * Draws each range for its run of instances into the bound target.
     * @param faces Translucent faces, or opaque ones that write depth when solid is set.
     * @param edges Triangle edges in a darker tint.
     */
    public void Draw(CollisionGeometry geometry, ReadOnlySpan<CollisionInstance> instanceData, ReadOnlySpan<CollisionRange> ranges,
        Matrix4x4 viewProjection, bool faces, bool edges, bool solid)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (instanceData.IsEmpty || ranges.IsEmpty || !faces && !edges) return;
        Begin(instanceData, viewProjection, PrimitiveTopology.TriangleList);
        var context = renderer.Context;
        context.IASetVertexBuffer(0, geometry.Vertices, (uint)sizeof(Vector3));
        context.IASetIndexBuffer(geometry.Indices, Format.R32_UInt, 0);
        if (faces)
        {
            context.PSSetShader(fillShader);
            context.RSSetState(fill);
            context.OMSetDepthStencilState(solid ? depthWrite : depthRead);
            context.OMSetBlendState(solid ? opaque : blend);
            DrawRanges(ranges);
        }
        if (edges)
        {
            context.PSSetShader(edgeShader);
            context.RSSetState(wire);
            context.OMSetDepthStencilState(depthRead);
            context.OMSetBlendState(opaque);
            DrawRanges(ranges);
        }
        context.OMSetBlendState(null);
    }

    /** Each instance's World maps the unit cube onto one box. */
    public void DrawBoxes(ReadOnlySpan<CollisionInstance> boxes, Matrix4x4 viewProjection, bool depthTest = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (boxes.IsEmpty) return;
        Begin(boxes, viewProjection, PrimitiveTopology.LineList);
        var context = renderer.Context;
        context.IASetVertexBuffer(0, boxVertices, (uint)sizeof(Vector3));
        context.PSSetShader(edgeShader);
        context.RSSetState(fill);
        context.OMSetDepthStencilState(depthTest ? depthRead : noDepth);
        context.OMSetBlendState(opaque);
        context.DrawInstanced(24, (uint)boxes.Length, 0, 0);
        renderer.CountDraws(1);
        context.OMSetBlendState(null);
    }

    private void DrawRanges(ReadOnlySpan<CollisionRange> ranges)
    {
        foreach (var range in ranges)
            if (range.InstanceCount > 0 && range.IndexCount > 0)
                renderer.Context.DrawIndexedInstanced(range.IndexCount, range.InstanceCount, range.FirstIndex, range.BaseVertex, range.FirstInstance);
        renderer.CountDraws(ranges.Length);
    }

    /** Uploads the instances once per call and binds the shared state. */
    private void Begin(ReadOnlySpan<CollisionInstance> instanceData, Matrix4x4 viewProjection, PrimitiveTopology topology)
    {
        var context = renderer.Context;
        if (capacity < instanceData.Length)
        {
            instances?.Dispose();
            capacity = checked(instanceData.Length + instanceData.Length / 2 + 256);
            instances = renderer.Device.CreateBuffer((uint)(capacity * sizeof(CollisionInstance)), BindFlags.VertexBuffer,
                ResourceUsage.Dynamic, CpuAccessFlags.Write);
        }
        var mapped = context.Map(instances!, 0, MapMode.WriteDiscard, MapFlags.None);
        try { instanceData.CopyTo(new Span<CollisionInstance>((void*)mapped.DataPointer, instanceData.Length)); }
        finally { context.Unmap(instances!, 0); }
        var frame = new FrameConstants { ViewProjection = viewProjection };
        context.UpdateSubresource(in frame, constants);
        context.HSSetShader(null!); context.DSSetShader(null!); context.GSSetShader(null!);
        context.IASetInputLayout(layout);
        context.IASetPrimitiveTopology(topology);
        context.IASetVertexBuffer(1, instances!, (uint)sizeof(CollisionInstance));
        context.VSSetShader(vertexShader);
        context.VSSetConstantBuffer(0, constants);
    }

    /** Releases the GPU resources this pass owns. */
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        instances?.Dispose();
        boxVertices.Dispose(); opaque.Dispose(); blend.Dispose(); noDepth.Dispose(); depthWrite.Dispose(); depthRead.Dispose();
        wire.Dispose(); fill.Dispose(); constants.Dispose(); layout.Dispose(); edgeShader.Dispose(); fillShader.Dispose();
        vertexShader.Dispose();
    }

    private const string Shader = """
        cbuffer Frame : register(b0) { row_major float4x4 ViewProjection; };
        struct PSInput { float4 Position : SV_POSITION; float3 World : TEXCOORD0; float4 Tint : COLOR0; };
        PSInput VSMain(float3 position : POSITION, float4 w0 : WORLD0, float4 w1 : WORLD1, float4 w2 : WORLD2,
            float4 w3 : WORLD3, float4 tint : TINT0) {
            float4 world = mul(float4(position, 1), float4x4(w0, w1, w2, w3));
            PSInput output;
            output.Position = mul(world, ViewProjection);
            output.World = world.xyz;
            output.Tint = tint;
            return output;
        }
        float4 PSFill(PSInput input) : SV_TARGET {
            float3 normal = cross(ddx(input.World), ddy(input.World));
            normal = normal / max(length(normal), 1e-12);
            float light = 0.45 + 0.55 * abs(dot(normal, normalize(float3(0.35, 0.45, 0.8))));
            return float4(input.Tint.rgb * light, input.Tint.a);
        }
        float4 PSEdge(PSInput input) : SV_TARGET { return float4(input.Tint.rgb * 0.6, 1); }
        """;
}
