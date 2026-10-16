using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace SunriseStudio.Rendering;

/** All calls run on the window thread that owns the immediate context. */
public sealed unsafe partial class Dx11Renderer : IDisposable
{
    private readonly IDXGIFactory2 factory;
    private readonly IDXGISwapChain1? swapChain;
    private ID3D11Texture2D color = null!;
    private ID3D11RenderTargetView colorView = null!;
    private ID3D11Texture2D depth = null!;
    private ID3D11DepthStencilView depthView = null!;
    private readonly ID3D11VertexShader meshVertexShader;
    private readonly ID3D11PixelShader meshPixelShader;
    private readonly ID3D11PixelShader meshCutoutPixelShader;
    private readonly ID3D11InputLayout meshLayout;
    private readonly ID3D11VertexShader instanceVertexShader;
    private readonly ID3D11InputLayout instanceLayout;
    private ID3D11Buffer? instanceBuffer;
    private int instanceCapacity;
    private readonly ID3D11VertexShader lineVertexShader;
    private readonly ID3D11PixelShader linePixelShader;
    private readonly ID3D11InputLayout lineLayout;
    private readonly ID3D11Buffer constants;
    private ID3D11Buffer? lineBuffer;
    private int lineCapacity;
    private readonly ID3D11RasterizerState solidState;
    private readonly ID3D11RasterizerState wireState;
    private readonly ID3D11DepthStencilState depthState;
    private readonly ID3D11DepthStencilState depthReadState;
    private readonly ID3D11DepthStencilState noDepthState;
    private readonly ID3D11SamplerState sampler;
    private readonly GpuTexture white;
    private bool disposed;

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public string AdapterName { get; }
    public bool IsWarp { get; }
    public FeatureLevel FeatureLevel { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int DrawCalls { get; private set; }
    public bool MeasureDiagnosticUploads { get; set; }
    public long DiagnosticLineMappedVertices { get; private set; }
    public long DiagnosticLineUploadedVertices { get; private set; }
    public double DiagnosticLineMapMilliseconds { get; private set; }
    public double DiagnosticLineUploadMilliseconds { get; private set; }
    public double DiagnosticLineConstantsMilliseconds { get; private set; }
    public double DiagnosticMeshConstantsMilliseconds { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DrawConstants
    {
        public Matrix4x4 WorldViewProjection;
        public Matrix4x4 NormalTransform;
        public Vector4 Tint;
    }

    /** A zero window handle selects an offscreen render target. */
    public Dx11Renderer(IntPtr windowHandle, int width, int height, bool forceWarp = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        factory = CreateDXGIFactory1<IDXGIFactory2>();
        FeatureLevel[] levels = [FeatureLevel.Level_11_0];
        var driver = forceWarp ? DriverType.Warp : DriverType.Hardware;
        var result = D3D11CreateDevice(IntPtr.Zero, driver, DeviceCreationFlags.BgraSupport,
            levels, out ID3D11Device device, out FeatureLevel level, out ID3D11DeviceContext context);
        if (result.Failure && !forceWarp)
        {
            driver = DriverType.Warp;
            result = D3D11CreateDevice(IntPtr.Zero, driver, DeviceCreationFlags.BgraSupport,
                levels, out device, out level, out context);
        }
        result.CheckError();
        Device = device;
        Context = context;
        FeatureLevel = level;
        IsWarp = driver == DriverType.Warp;
        using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgiDevice.GetAdapter())
            AdapterName = adapter.Description.Description;

        if (windowHandle != IntPtr.Zero)
        {
            swapChain = factory.CreateSwapChainForHwnd(device, windowHandle,
                new SwapChainDescription1
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
        }
        Width = width;
        Height = height;
        CreateTargets();

        var meshVs = Compile(MeshShader, "VSMain", "vs_5_0");
        meshVertexShader = device.CreateVertexShader(meshVs.Span);
        meshPixelShader = device.CreatePixelShader(Compile(MeshShader, "PSMain", "ps_5_0").Span);
        meshCutoutPixelShader = device.CreatePixelShader(Compile(MeshShader, "PSCutout", "ps_5_0").Span);
        meshLayout = device.CreateInputLayout(
        [
            new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new("TEXCOORD", 0, Format.R32G32_Float, 24, 0)
        ], meshVs.Span);
        var instanceVs = Compile(MeshShader, "VSInstance", "vs_5_0");
        instanceVertexShader = device.CreateVertexShader(instanceVs.Span);
        var instanceElements = new List<InputElementDescription>
        {
            new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new("TEXCOORD", 0, Format.R32G32_Float, 24, 0)
        };
        for (uint row = 0; row < 4; row++)
        {
            instanceElements.Add(new("TRANSFORM", row, Format.R32G32B32A32_Float,
                row * 16, 1, InputClassification.PerInstanceData, 1));
            instanceElements.Add(new("NORMALMATRIX", row, Format.R32G32B32A32_Float,
                64 + row * 16, 1, InputClassification.PerInstanceData, 1));
        }
        instanceElements.Add(new("TINT", 0, Format.R32G32B32A32_Float,
            128, 1, InputClassification.PerInstanceData, 1));
        instanceLayout = device.CreateInputLayout(instanceElements.ToArray(), instanceVs.Span);
        var lineVs = Compile(LineShader, "VSMain", "vs_5_0");
        lineVertexShader = device.CreateVertexShader(lineVs.Span);
        linePixelShader = device.CreatePixelShader(Compile(LineShader, "PSMain", "ps_5_0").Span);
        lineLayout = device.CreateInputLayout(
        [
            new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new("COLOR", 0, Format.R32G32B32A32_Float, 12, 0)
        ], lineVs.Span);
        constants = device.CreateBuffer((uint)sizeof(DrawConstants), BindFlags.ConstantBuffer);
        solidState = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        wireState = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Wireframe));
        depthState = device.CreateDepthStencilState(new DepthStencilDescription(true,
            DepthWriteMask.All, ComparisonFunction.LessEqual));
        depthReadState = device.CreateDepthStencilState(new DepthStencilDescription(true,
            DepthWriteMask.Zero, ComparisonFunction.LessEqual));
        noDepthState = device.CreateDepthStencilState(new DepthStencilDescription(false,
            DepthWriteMask.Zero, ComparisonFunction.Always));
        sampler = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear,
            TextureAddressMode.Wrap));
        white = CreateTexture(1, 1, [255, 255, 255, 255]);
    }

    private static readonly ConcurrentDictionary<(string Source, string Entry, string Profile), ReadOnlyMemory<byte>> compiled = new();

    // Bytecode depends only on the key, so each shader compiles once per process.
    internal static ReadOnlyMemory<byte> Compile(string source, string entry, string profile) =>
        compiled.GetOrAdd((source, entry, profile), key =>
            Compiler.Compile(key.Source, key.Entry, "Studio", key.Profile, ShaderFlags.OptimizationLevel3));

    /** Swap-chain buffers must be released before DXGI can resize them. */
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width == Width && height == Height)) return;
        Context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        colorView.Dispose();
        color.Dispose();
        depthView.Dispose();
        depth.Dispose();
        swapChain?.ResizeBuffers(2, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None)
            .CheckError();
        Width = width;
        Height = height;
        CreateTargets();
    }

    /** Color and depth targets always share one extent and sample count. */
    private void CreateTargets()
    {
        color = swapChain?.GetBuffer<ID3D11Texture2D>(0) ?? Device.CreateTexture2D(
            Format.B8G8R8A8_UNorm, (uint)Width, (uint)Height, mipLevels: 1,
            bindFlags: BindFlags.RenderTarget);
        colorView = Device.CreateRenderTargetView(color);
        depth = Device.CreateTexture2D(Format.D32_Float, (uint)Width, (uint)Height,
            mipLevels: 1, bindFlags: BindFlags.DepthStencil);
        depthView = Device.CreateDepthStencilView(depth);
    }

    /** Begin a new frame before issuing scene or ImGui draws. */
    public void BeginFrame(Vector4 clear)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Context.OMSetRenderTargets(colorView, depthView);
        Context.ClearRenderTargetView(colorView, new Color4(clear.X, clear.Y, clear.Z, clear.W));
        Context.ClearDepthStencilView(depthView, DepthStencilClearFlags.Depth, 1, 0);
        Context.RSSetViewport(new Viewport(Width, Height));
        Context.RSSetScissorRect(Width, Height);
        DrawCalls = 0;
    }

    /** Indices must form complete triangles and reference the supplied vertices. */
    public GpuMesh CreateMesh(ReadOnlySpan<RenderVertex> vertices, ReadOnlySpan<uint> indices)
    {
        if (vertices.IsEmpty || indices.IsEmpty || indices.Length % 3 != 0)
            throw new ArgumentException("A mesh needs vertices and triangle indices.");
        foreach (uint index in indices)
            if (index >= vertices.Length) throw new ArgumentOutOfRangeException(nameof(indices));
        var vertexBuffer = Device.CreateBuffer(vertices, BindFlags.VertexBuffer);
        try
        {
            return new GpuMesh(vertexBuffer, Device.CreateBuffer(indices, BindFlags.IndexBuffer),
                (uint)vertices.Length, (uint)indices.Length);
        }
        catch
        {
            vertexBuffer.Dispose();
            throw;
        }
    }

    /** Pose updates retain the vertex count, index order, and existing GPU allocation. */
    public void UpdateMeshVertices(GpuMesh mesh, ReadOnlySpan<RenderVertex> vertices)
    {
        var description = mesh.Vertices.Description;
        if (vertices.Length != mesh.VertexCount
            || checked((uint)(vertices.Length * sizeof(RenderVertex))) != description.ByteWidth)
            throw new ArgumentException("A mesh update must retain its original vertex count.");
        if (description.Usage != ResourceUsage.Default)
            throw new InvalidOperationException("Mesh updates require a default-usage vertex buffer.");
        Context.UpdateSubresource(vertices, mesh.Vertices);
    }

    public GpuTexture CreateTexture(int width, int height, ReadOnlySpan<byte> rgba) =>
        CreateTexture(width, height, Format.R8G8B8A8_UNorm, rgba, checked(width * 4));

    /** RGBA8 with a full mip chain; level n is max(1, size >> n) on each side. */
    public GpuTexture CreateMippedTexture(int width, int height, IReadOnlyList<byte[]> levels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var handles = new List<GCHandle>();
        try
        {
            var data = new SubresourceData[levels.Count];
            for (int level = 0; level < levels.Count; level++)
            {
                int w = Math.Max(1, width >> level), h = Math.Max(1, height >> level);
                ValidateTextureData(w, h, Format.R8G8B8A8_UNorm, levels[level].Length, w * 4);
                var handle = GCHandle.Alloc(levels[level], GCHandleType.Pinned);
                handles.Add(handle);
                data[level] = new SubresourceData(handle.AddrOfPinnedObject(), (uint)(w * 4));
            }
            var texture = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = (uint)levels.Count, ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm, SampleDescription = SampleDescription.Default,
                Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource
            }, data);
            try { return new GpuTexture(texture, Device.CreateShaderResourceView(texture), width, height); }
            catch { texture.Dispose(); throw; }
        }
        finally { foreach (var handle in handles) handle.Free(); }
    }

    /** Compressed textures use a pitch measured in bytes per block row. */
    public GpuTexture CreateTexture(int width, int height, Format format,
        ReadOnlySpan<byte> bytes, int rowPitch)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowPitch);
        ValidateTextureData(width, height, format, bytes.Length, rowPitch);
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = SampleDescription.Default,
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource
        };
        fixed (byte* pointer = bytes)
        {
            var texture = Device.CreateTexture2D(description,
                [new SubresourceData((IntPtr)pointer, (uint)rowPitch)]);
            try
            {
                return new GpuTexture(texture, Device.CreateShaderResourceView(texture), width, height);
            }
            catch
            {
                texture.Dispose();
                throw;
            }
        }
    }

    /** Reject short buffers before a native driver reads their memory. */
    private static void ValidateTextureData(int width, int height, Format format,
        int byteLength, int rowPitch)
    {
        int blockBytes = format switch
        {
            Format.BC1_UNorm or Format.BC1_UNorm_SRgb or Format.BC4_UNorm or Format.BC4_SNorm => 8,
            Format.BC2_UNorm or Format.BC2_UNorm_SRgb or Format.BC3_UNorm or Format.BC3_UNorm_SRgb
                or Format.BC5_UNorm or Format.BC5_SNorm or Format.BC6H_Uf16 or Format.BC6H_Sf16
                or Format.BC7_UNorm or Format.BC7_UNorm_SRgb => 16,
            _ => 0
        };
        int rows = height;
        int minimumPitch;
        if (blockBytes != 0)
        {
            minimumPitch = checked(Math.Max(1, (width + 3) / 4) * blockBytes);
            rows = Math.Max(1, (height + 3) / 4);
        }
        else
        {
            int pixelBytes = format switch
            {
                Format.R8_UNorm or Format.R8_SNorm or Format.A8_UNorm => 1,
                Format.R8G8_UNorm or Format.R8G8_SNorm or Format.R16_UNorm or Format.R16_Float => 2,
                Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb or Format.B8G8R8A8_UNorm
                    or Format.B8G8R8A8_UNorm_SRgb or Format.B8G8R8X8_UNorm
                    or Format.B8G8R8X8_UNorm_SRgb or Format.R10G10B10A2_UNorm
                    or Format.R11G11B10_Float or Format.R16G16_Float or Format.R16G16_UNorm
                    or Format.R16G16_SNorm or Format.R32_Float => 4,
                Format.R16G16B16A16_Float or Format.R16G16B16A16_UNorm or Format.R32G32_Float => 8,
                Format.R32G32B32A32_Float => 16,
                _ => throw new NotSupportedException($"Texture format {format} needs a typed decoder.")
            };
            minimumPitch = checked(width * pixelBytes);
        }
        if (rowPitch < minimumPitch || byteLength < checked((rows - 1) * rowPitch + minimumPitch))
            throw new ArgumentException("Texture data or pitch is shorter than the requested surface.");
    }

    /** This inspection pass uses a directional light and one optional color texture. */
    public void DrawMesh(GpuMesh mesh, Matrix4x4 world, Matrix4x4 viewProjection,
        Vector4 tint, GpuTexture? texture = null, bool wireframe = false, bool skybox = false,
        bool depthTest = true)
    {
        Matrix4x4.Invert(world, out var inverse);
        var data = new DrawConstants
        {
            WorldViewProjection = world * viewProjection,
            NormalTransform = Matrix4x4.Transpose(inverse),
            Tint = tint
        };
        long uploaded = MeasureDiagnosticUploads ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        Context.UpdateSubresource(in data, constants);
        if (MeasureDiagnosticUploads)
            DiagnosticMeshConstantsMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(uploaded).TotalMilliseconds;
        Context.IASetInputLayout(meshLayout);
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.IASetVertexBuffer(0, mesh.Vertices, (uint)sizeof(RenderVertex));
        Context.IASetIndexBuffer(mesh.Indices, Format.R32_UInt, 0);
        Context.VSSetShader(meshVertexShader);
        Context.VSSetConstantBuffer(0, constants);
        Context.PSSetShader(meshPixelShader);
        Context.PSSetShaderResource(0, (texture ?? white).View);
        Context.PSSetSampler(0, sampler);
        Context.RSSetState(wireframe ? wireState : solidState);
        Context.OMSetDepthStencilState(!depthTest ? noDepthState : skybox ? depthReadState : depthState);
        Context.OMSetBlendState(null);
        Context.DrawIndexed(mesh.IndexCount, 0, 0);
        DrawCalls++;
        CountTriangles(mesh.IndexCount);
    }

    /**
     * One draw shares geometry, texture, and tint across all instance transforms.
     * @param alphaCutout Discards pixels whose sampled alpha times tint alpha is below one half.
     */
    public void DrawMeshInstanced(GpuMesh mesh, ReadOnlySpan<Matrix4x4> transforms,
        Matrix4x4 viewProjection, Vector4 tint, GpuTexture? texture = null,
        bool wireframe = false, bool skybox = false, bool alphaCutout = false)
    {
        if (transforms.IsEmpty) return;
        if (instanceCapacity < transforms.Length)
        {
            instanceBuffer?.Dispose();
            instanceCapacity = checked(transforms.Length + 256);
            instanceBuffer = Device.CreateBuffer((uint)(instanceCapacity * sizeof(DrawConstants)),
                BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        }
        var mapped = Context.Map(instanceBuffer!, 0, MapMode.WriteDiscard, MapFlags.None);
        try
        {
            var destination = new Span<DrawConstants>((void*)mapped.DataPointer, transforms.Length);
            for (int i = 0; i < transforms.Length; i++)
            {
                Matrix4x4.Invert(transforms[i], out var inverse);
                destination[i] = new DrawConstants
                {
                    WorldViewProjection = transforms[i] * viewProjection,
                    NormalTransform = Matrix4x4.Transpose(inverse),
                    Tint = tint
                };
            }
        }
        finally { Context.Unmap(instanceBuffer!, 0); }
        Context.IASetInputLayout(instanceLayout);
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.IASetVertexBuffer(0, mesh.Vertices, (uint)sizeof(RenderVertex));
        Context.IASetVertexBuffer(1, instanceBuffer!, (uint)sizeof(DrawConstants));
        Context.IASetIndexBuffer(mesh.Indices, Format.R32_UInt, 0);
        Context.VSSetShader(instanceVertexShader);
        Context.PSSetShader(alphaCutout ? meshCutoutPixelShader : meshPixelShader);
        Context.PSSetShaderResource(0, (texture ?? white).View);
        Context.PSSetSampler(0, sampler);
        Context.RSSetState(wireframe ? wireState : solidState);
        Context.OMSetDepthStencilState(skybox ? depthReadState : depthState);
        Context.OMSetBlendState(null);
        Context.DrawIndexedInstanced(mesh.IndexCount, (uint)transforms.Length, 0, 0, 0);
        DrawCalls++;
        CountTriangles(mesh.IndexCount, transforms.Length);
    }

    /** Lines arrive as pairs and can either respect depth or form a visible overlay. */
    public void DrawLines(ReadOnlySpan<LineVertex> vertices, Matrix4x4 viewProjection,
        bool depthTest = true)
    {
        if (vertices.IsEmpty) return;
        if (vertices.Length % 2 != 0) throw new ArgumentException("Lines need vertex pairs.");
        if (lineCapacity < vertices.Length)
        {
            var next = Device.CreateBuffer(checked((uint)((vertices.Length + 1024) * sizeof(LineVertex))),
                BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
            lineBuffer?.Dispose(); lineBuffer = next; lineCapacity = checked(vertices.Length + 1024);
        }
        UploadLineVertices(lineBuffer!, lineCapacity, vertices);
        DrawLineBuffer(lineBuffer!, vertices.Length, viewProjection, depthTest);
    }

    private void DrawLineBuffer(ID3D11Buffer buffer, int count, Matrix4x4 viewProjection, bool depthTest)
    {
        long uploaded = MeasureDiagnosticUploads ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var data = new DrawConstants { WorldViewProjection = viewProjection, Tint = Vector4.One };
        Context.UpdateSubresource(in data, constants);
        if (MeasureDiagnosticUploads)
            DiagnosticLineConstantsMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(uploaded).TotalMilliseconds;
        Context.IASetInputLayout(lineLayout);
        Context.IASetPrimitiveTopology(PrimitiveTopology.LineList);
        Context.IASetVertexBuffer(0, buffer, (uint)sizeof(LineVertex));
        Context.VSSetShader(lineVertexShader);
        Context.VSSetConstantBuffer(0, constants);
        Context.PSSetShader(linePixelShader);
        Context.RSSetState(solidState);
        Context.OMSetDepthStencilState(depthTest ? depthReadState : noDepthState);
        Context.OMSetBlendState(null);
        Context.Draw((uint)count, 0);
        DrawCalls++;
    }

    private void UploadLineVertices(ID3D11Buffer buffer, int capacity, ReadOnlySpan<LineVertex> vertices)
    {
        long began = MeasureDiagnosticUploads ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var mapped = Context.Map(buffer, 0, MapMode.WriteDiscard, MapFlags.None);
        if (MeasureDiagnosticUploads)
        {
            DiagnosticLineMapMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            DiagnosticLineMappedVertices += capacity;
            DiagnosticLineUploadedVertices += vertices.Length;
        }
        try { vertices.CopyTo(new Span<LineVertex>((void*)mapped.DataPointer, vertices.Length)); }
        finally { Context.Unmap(buffer, 0); }
        if (MeasureDiagnosticUploads)
            DiagnosticLineUploadMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds;
    }

    public void EndFrame(bool vsync = true) =>
        swapChain?.Present(vsync ? 1u : 0u, PresentFlags.None).CheckError();

    /** Capture before Present because flip-discard does not preserve presented buffers. */
    public void CapturePng(string path)
    {
        var description = color.Description;
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = Device.CreateTexture2D(description);
        Context.CopyResource(staging, color);
        var mapped = Context.Map(staging, 0, MapMode.Read, MapFlags.None);
        try
        {
            using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            var bits = bitmap.LockBits(new Rectangle(0, 0, Width, Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < Height; y++)
                    new ReadOnlySpan<byte>((byte*)mapped.DataPointer + y * mapped.RowPitch, Width * 4)
                        .CopyTo(new Span<byte>((byte*)bits.Scan0 + y * bits.Stride, Width * 4));
            }
            finally { bitmap.UnlockBits(bits); }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            bitmap.Save(path, ImageFormat.Png);
        }
        finally { Context.Unmap(staging, 0); }
    }

    /** Call after disposing all uploaded meshes, textures, and ImGui state. */
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Context.ClearState();
        Context.Flush();
        DisposeSceneResources();
        ReleaseGrabs();
        white.Dispose();
        sampler.Dispose();
        noDepthState.Dispose();
        depthReadState.Dispose();
        depthState.Dispose();
        wireState.Dispose();
        solidState.Dispose();
        lineBuffer?.Dispose();
        instanceBuffer?.Dispose();
        instanceLayout.Dispose();
        instanceVertexShader.Dispose();
        constants.Dispose();
        lineLayout.Dispose();
        linePixelShader.Dispose();
        lineVertexShader.Dispose();
        meshLayout.Dispose();
        meshCutoutPixelShader.Dispose();
        meshPixelShader.Dispose();
        meshVertexShader.Dispose();
        depthView.Dispose();
        depth.Dispose();
        colorView.Dispose();
        color.Dispose();
        swapChain?.Dispose();
        Context.Dispose();
        Device.Dispose();
        factory.Dispose();
    }

    private const string MeshShader = """
        cbuffer DrawData : register(b0) {
            row_major float4x4 WorldViewProjection;
            row_major float4x4 NormalTransform;
            float4 Tint;
        };
        Texture2D ColorTexture : register(t0);
        SamplerState LinearSampler : register(s0);
        struct VSInput { float3 Position : POSITION; float3 Normal : NORMAL; float2 UV : TEXCOORD0; };
        struct PSInput { float4 Position : SV_POSITION; float3 Normal : NORMAL; float2 UV : TEXCOORD0; float4 Color : COLOR0; };
        PSInput VSMain(VSInput input) {
            PSInput output;
            output.Position = mul(float4(input.Position, 1), WorldViewProjection);
            output.Normal = mul(float4(input.Normal, 0), NormalTransform).xyz;
            output.UV = input.UV;
            output.Color = Tint;
            return output;
        }
        PSInput VSInstance(VSInput input,
            float4 transform0 : TRANSFORM0, float4 transform1 : TRANSFORM1,
            float4 transform2 : TRANSFORM2, float4 transform3 : TRANSFORM3,
            float4 normal0 : NORMALMATRIX0, float4 normal1 : NORMALMATRIX1,
            float4 normal2 : NORMALMATRIX2, float4 normal3 : NORMALMATRIX3,
            float4 tint : TINT0) {
            float4x4 transform = float4x4(transform0, transform1, transform2, transform3);
            float4x4 normals = float4x4(normal0, normal1, normal2, normal3);
            PSInput output;
            output.Position = mul(float4(input.Position, 1), transform);
            output.Normal = mul(float4(input.Normal, 0), normals).xyz;
            output.UV = input.UV;
            output.Color = tint;
            return output;
        }
        float4 PSMain(PSInput input) : SV_TARGET {
            float3 normal = normalize(input.Normal + float3(0, 0, 0.000001));
            float light = 0.3 + 0.7 * abs(dot(normal, normalize(float3(0.35, 0.8, 0.45))));
            float4 color = ColorTexture.Sample(LinearSampler, input.UV) * input.Color;
            return float4(color.rgb * light, color.a);
        }
        // Viewer cutoff for alpha-tested stand-ins; the material's own test value is not read.
        static const float CutoutThreshold = 0.5;
        float4 PSCutout(PSInput input) : SV_TARGET {
            float4 color = PSMain(input);
            clip(color.a - CutoutThreshold);
            return color;
        }
        """;

    private const string LineShader = """
        cbuffer DrawData : register(b0) { row_major float4x4 WorldViewProjection; };
        struct PSInput { float4 Position : SV_POSITION; float4 Color : COLOR0; };
        PSInput VSMain(float3 position : POSITION, float4 color : COLOR0) {
            PSInput output;
            output.Position = mul(float4(position, 1), WorldViewProjection);
            output.Color = color;
            return output;
        }
        float4 PSMain(PSInput input) : SV_TARGET { return input.Color; }
        """;
}
