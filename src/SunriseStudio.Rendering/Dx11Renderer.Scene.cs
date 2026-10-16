using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace SunriseStudio.Rendering;

/** One placed copy of a mesh or a marker box; World maps local space to world space. */
[StructLayout(LayoutKind.Sequential)]
public struct SceneInstance(Matrix4x4 world, Vector4 tint)
{
    public Matrix4x4 World = world;
    public Vector4 Tint = tint;
}

/** Frame instance stream: upload once per frame, then draw ranges of it. */
public sealed unsafe partial class Dx11Renderer
{
    private ID3D11VertexShader? sceneVertexShader, boxVertexShader;
    private ID3D11PixelShader? scenePixelShader, boxPixelShader;
    private ID3D11InputLayout? sceneLayout, boxLayout;
    private ID3D11Buffer? sceneInstances, sceneConstants, boxVertices;
    private int sceneCapacity;

    [StructLayout(LayoutKind.Sequential)]
    private struct SceneConstants
    {
        public Matrix4x4 ViewProjection;
        public Vector4 LightDirection;
    }

    /** Replaces the frame's instance stream; later draws index into it. */
    public void UploadSceneInstances(ReadOnlySpan<SceneInstance> instances)
    {
        EnsureSceneResources();
        if (instances.IsEmpty) return;
        if (sceneCapacity < instances.Length)
        {
            sceneInstances?.Dispose();
            sceneCapacity = Math.Max(instances.Length * 3 / 2, 4096);
            sceneInstances = Device.CreateBuffer((uint)(sceneCapacity * sizeof(SceneInstance)),
                BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        }
        var mapped = Context.Map(sceneInstances!, 0, MapMode.WriteDiscard, MapFlags.None);
        instances.CopyTo(new Span<SceneInstance>((void*)mapped.DataPointer, instances.Length));
        Context.Unmap(sceneInstances!, 0);
    }

    /** Sets the view-projection matrix and light direction used by scene instance draws. */
    public void SetSceneView(Matrix4x4 viewProjection, Vector3 lightDirection)
    {
        EnsureSceneResources();
        var data = new SceneConstants { ViewProjection = viewProjection, LightDirection = new(lightDirection, 0) };
        Context.UpdateSubresource(in data, sceneConstants!);
    }

    /** Binds the shared scene state; call before a run of DrawSceneInstances. */
    public void BeginSceneDraws()
    {
        Context.IASetInputLayout(sceneLayout);
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.VSSetShader(sceneVertexShader);
        Context.VSSetConstantBuffer(0, sceneConstants);
        Context.PSSetShader(scenePixelShader);
        Context.PSSetConstantBuffer(0, sceneConstants);
        Context.PSSetSampler(0, sampler);
        Context.OMSetBlendState(null);
        if (sceneInstances is not null) Context.IASetVertexBuffer(1, sceneInstances, (uint)sizeof(SceneInstance));
        lastSceneMesh = null; lastSceneTexture = null; lastSceneState = -1;
    }

    private GpuMesh? lastSceneMesh;
    private GpuTexture? lastSceneTexture;
    private int lastSceneState;

    /** Draws instances [first, first + count) of the uploaded stream; binds only what changed. */
    public void DrawSceneInstances(GpuMesh mesh, int first, int count, GpuTexture? texture = null,
        bool wireframe = false, bool skybox = false)
    {
        if (count <= 0) return;
        if (!ReferenceEquals(mesh, lastSceneMesh))
        {
            Context.IASetVertexBuffer(0, mesh.Vertices, (uint)sizeof(RenderVertex));
            Context.IASetIndexBuffer(mesh.Indices, Format.R32_UInt, 0);
            lastSceneMesh = mesh;
        }
        texture ??= white;
        if (!ReferenceEquals(texture, lastSceneTexture)) { Context.PSSetShaderResource(0, texture.View); lastSceneTexture = texture; }
        int state = (wireframe ? 1 : 0) | (skybox ? 2 : 0);
        if (state != lastSceneState)
        {
            Context.RSSetState(wireframe ? wireState : solidState);
            Context.OMSetDepthStencilState(skybox ? depthReadState : depthState);
            lastSceneState = state;
        }
        Context.DrawIndexedInstanced(mesh.IndexCount, (uint)count, 0, 0, (uint)first);
        DrawCalls++;
        CountTriangles(mesh.IndexCount, count);
    }

    /** Draws wire boxes; each World maps the cube [-1, 1] to one box. */
    public void DrawWireBoxes(int first, int count, bool depthTest = true)
    {
        if (count <= 0) return;
        Context.IASetInputLayout(boxLayout);
        Context.IASetPrimitiveTopology(PrimitiveTopology.LineList);
        Context.IASetVertexBuffer(0, boxVertices!, 12);
        Context.IASetVertexBuffer(1, sceneInstances!, (uint)sizeof(SceneInstance));
        Context.VSSetShader(boxVertexShader);
        Context.VSSetConstantBuffer(0, sceneConstants);
        Context.PSSetShader(boxPixelShader);
        Context.RSSetState(solidState);
        Context.OMSetDepthStencilState(depthTest ? depthReadState : noDepthState);
        Context.OMSetBlendState(null);
        Context.DrawInstanced(24, (uint)count, 0, (uint)first);
        DrawCalls++;
    }

    private void EnsureSceneResources()
    {
        if (sceneVertexShader is not null) return;
        var vs = Compile(SceneShader, "VSMain", "vs_5_0");
        sceneVertexShader = Device.CreateVertexShader(vs.Span);
        scenePixelShader = Device.CreatePixelShader(Compile(SceneShader, "PSMain", "ps_5_0").Span);
        var elements = new List<InputElementDescription>
        {
            new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new("TEXCOORD", 0, Format.R32G32_Float, 24, 0)
        };
        AddInstanceElements(elements);
        sceneLayout = Device.CreateInputLayout(elements.ToArray(), vs.Span);
        var boxVs = Compile(SceneShader, "VSBox", "vs_5_0");
        boxVertexShader = Device.CreateVertexShader(boxVs.Span);
        boxPixelShader = Device.CreatePixelShader(Compile(SceneShader, "PSBox", "ps_5_0").Span);
        var boxElements = new List<InputElementDescription> { new("POSITION", 0, Format.R32G32B32_Float, 0, 0) };
        AddInstanceElements(boxElements);
        boxLayout = Device.CreateInputLayout(boxElements.ToArray(), boxVs.Span);
        var corners = new Vector3[24];
        int n = 0;
        for (int i = 0; i < 8; i++)
            for (int bit = 1; bit <= 4; bit *= 2)
                if ((i & bit) == 0) { corners[n++] = Corner(i); corners[n++] = Corner(i | bit); }
        boxVertices = Device.CreateBuffer(corners.AsSpan(), BindFlags.VertexBuffer);
        sceneConstants = Device.CreateBuffer((uint)sizeof(SceneConstants), BindFlags.ConstantBuffer);
        static Vector3 Corner(int i) => new((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1);
    }

    private static void AddInstanceElements(List<InputElementDescription> elements)
    {
        for (uint row = 0; row < 4; row++)
            elements.Add(new("WORLD", row, Format.R32G32B32A32_Float, row * 16, 1, InputClassification.PerInstanceData, 1));
        elements.Add(new("TINT", 0, Format.R32G32B32A32_Float, 64, 1, InputClassification.PerInstanceData, 1));
    }

    private void DisposeSceneResources()
    {
        sceneInstances?.Dispose(); sceneConstants?.Dispose(); boxVertices?.Dispose();
        sceneLayout?.Dispose(); boxLayout?.Dispose();
        sceneVertexShader?.Dispose(); boxVertexShader?.Dispose();
        scenePixelShader?.Dispose(); boxPixelShader?.Dispose();
    }

    private const string SceneShader = """
        cbuffer SceneData : register(b0) {
            row_major float4x4 ViewProjection;
            float4 LightDirection;
        };
        Texture2D ColorTexture : register(t0);
        SamplerState LinearSampler : register(s0);
        struct Instance {
            float4 w0 : WORLD0; float4 w1 : WORLD1; float4 w2 : WORLD2; float4 w3 : WORLD3;
            float4 tint : TINT0;
        };
        struct PSInput { float4 Position : SV_POSITION; float3 Normal : NORMAL; float2 UV : TEXCOORD0; float4 Color : COLOR0; };
        PSInput VSMain(float3 position : POSITION, float3 normal : NORMAL, float2 uv : TEXCOORD0, Instance instance) {
            float4x4 world = float4x4(instance.w0, instance.w1, instance.w2, instance.w3);
            PSInput output;
            output.Position = mul(mul(float4(position, 1), world), ViewProjection);
            output.Normal = mul(normal, (float3x3)world);
            output.UV = uv;
            output.Color = instance.tint;
            return output;
        }
        float4 PSMain(PSInput input) : SV_TARGET {
            float3 normal = normalize(input.Normal + float3(0, 0, 0.000001));
            float light = 0.3 + 0.7 * abs(dot(normal, LightDirection.xyz));
            float4 color = ColorTexture.Sample(LinearSampler, input.UV) * input.Color;
            return float4(color.rgb * light, color.a);
        }
        struct BoxOutput { float4 Position : SV_POSITION; float4 Color : COLOR0; };
        BoxOutput VSBox(float3 position : POSITION, Instance instance) {
            float4x4 world = float4x4(instance.w0, instance.w1, instance.w2, instance.w3);
            BoxOutput output;
            output.Position = mul(mul(float4(position, 1), world), ViewProjection);
            output.Color = instance.tint;
            return output;
        }
        float4 PSBox(BoxOutput input) : SV_TARGET { return input.Color; }
        """;
}
