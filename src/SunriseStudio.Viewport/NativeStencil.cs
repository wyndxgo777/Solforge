using System.Runtime.CompilerServices;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using Vortice.Direct3D11;

namespace SunriseStudio.Viewport;

/** A draw's stencil state and reference; both null keep the stencil-free state. */
public sealed record NativeStencilDraw(NativeRenderStateDescriptions? States, uint? Reference);

/** The game draw path of a part, which decides its stencil reference. */
public enum NativeStencilPath { Entity, Decorator, Static, Terrain }

/** The G-buffer stencil as the first-person and environment views write it in camera mode 0. */
public static class NativeStencil
{
    // The G-buffer clear writes 4: bit 2 marks pixels the first-person view did not draw.
    public const byte ClearValue = 4;
    // Stage 0 pass byte 1: 0x84 (state 4) for the first-person view, 0x88 (state 8) otherwise.
    private const int FirstPersonState = 4, EnvironmentState = 8;
    private const byte BitTwo = 0x04;
    // The stage 0 submit sets reference 0x23 for the first-person view and 4 otherwise.
    private const uint FirstPersonBase = 0x23, EnvironmentBase = 4;
    // Entities set bits 3, 5 and 7 to 0x20 and static instances to 0.
    // Decorators set bits 0, 1, 3, 5 and 7 to 0x20.
    private const uint ObjectMask = 0xA8, DecoratorMask = 0xAB, EntityValue = 0x20, StaticValue = 0;
    public const uint EntityReference = (FirstPersonBase & ~ObjectMask) | EntityValue;
    public const uint EnvironmentEntityReference = (EnvironmentBase & ~ObjectMask) | EntityValue;
    public const uint EnvironmentDecoratorReference = (EnvironmentBase & ~DecoratorMask) | EntityValue;
    public const uint EnvironmentStaticReference = (EnvironmentBase & ~ObjectMask) | StaticValue;
    // Terrain sets no reference; any draw before it leaves bit 2 at 1 and bits 3 and 4 at 0.
    public const uint EnvironmentTerrainReference = EnvironmentBase;
    // The decal submit sets reference 4; state 64 tests bit 2 only.
    public const uint DecalReference = EnvironmentBase;
    // The shading pass ends with reference 0xFF; stages 6 and 7 inherit it for their bit 2 test.
    public const uint TransparentReference = 0xFF;
    // Known written bits per path; the others depend on per-object data or on the draw before.
    public const byte KnownBits = 0x2C;
    public const byte EnvironmentEntityBits = 0x28, EnvironmentDecoratorBits = 0xA8, EnvironmentStaticBits = 0xA8,
        EnvironmentTerrainBits = 0x18;

    // Extern 3 resource 15: target 26, stencil_alias_read, the stencil copied after stage 0.
    public static readonly TfxResource SceneStencil = new("scene stencil", Vortice.Direct3D.ShaderResourceViewDimension.Texture2D);

    private static readonly ConditionalWeakTable<SceneDocument, object> FirstPersonScenes = new();

    /** Marks a scene drawn as the first-person player view. */
    public static void SetFirstPersonView(SceneDocument scene) => FirstPersonScenes.AddOrUpdate(scene, scene);

    /** True when the scene was marked as a first-person view. */
    public static bool IsFirstPersonView(SceneDocument scene) => FirstPersonScenes.TryGetValue(scene, out _);

    /** Decorators are entity models with 16-byte records, one or a ground cell's; static meshes are instances. */
    public static NativeStencilPath PathOf(MeshInstance instance) => instance.Mesh.Native?.Kind switch
    {
        NativeMeshKind.Terrain => NativeStencilPath.Terrain,
        NativeMeshKind.Static => NativeStencilPath.Static,
        _ => instance.NativeInstanceData.Length != 0 && instance.NativeInstanceData.Length % DecoratorRecords.Size == 0
            ? NativeStencilPath.Decorator : NativeStencilPath.Entity
    };

    /**
     * The stencil state and reference the game uses for one draw path.
     * @return States null keeps the stencil-free state.
     */
    public static NativeStencilDraw Resolve(NativeRenderStateCatalog states, uint overrides, int renderStage,
        NativeStencilPath path, bool firstPerson)
    {
        var (pass, forced) = NativeStagePassStates.Get(renderStage);
        if (firstPerson)
            return renderStage == 0 && path == NativeStencilPath.Entity
                ? Writes(states, pass, overrides, forced, FirstPersonState, KnownBits, EntityReference)
                : new(null, null);
        switch (renderStage)
        {
            case 0:
                uint environmentPass = (pass & ~0xFF00u) | ((0x80u | EnvironmentState) << 8);
                var (bits, reference) = path switch
                {
                    NativeStencilPath.Entity => (EnvironmentEntityBits, EnvironmentEntityReference),
                    NativeStencilPath.Decorator => (EnvironmentDecoratorBits, EnvironmentDecoratorReference),
                    NativeStencilPath.Static => (EnvironmentStaticBits, EnvironmentStaticReference),
                    _ => (EnvironmentTerrainBits, EnvironmentTerrainReference)
                };
                return Writes(states, environmentPass, overrides, forced, EnvironmentState, bits, reference);
            case 1 or 2 or 6 or 7:
                var tested = states.Resolve(pass, overrides, forced, NativeDepthTable.Alternate);
                // State 64 and the water overrides test bit 2 and write nothing.
                return tested.DepthStencil is { StencilReadMask: BitTwo, StencilWriteMask: 0 } && tested.DepthStencil.StencilEnable
                    ? new(tested, renderStage <= 2 ? DecalReference : TransparentReference)
                    : new(null, null);
            default:
                return new(null, null);
        }
    }

    /** A replace state with writes limited to the bits whose values are known. */
    private static NativeStencilDraw Writes(NativeRenderStateCatalog states, uint pass, uint overrides, uint forced,
        int state, byte knownBits, uint reference)
    {
        var resolved = states.Resolve(pass, overrides, forced, NativeDepthTable.Alternate);
        if (resolved.Selection.DepthStencil != state) return new(null, null);
        DepthStencilDescription depth = resolved.DepthStencil;
        if (!depth.StencilEnable || depth.FrontFace.StencilPassOp != StencilOperation.Replace)
            throw new NotSupportedException($"Stage 0 stencil state {state} changed.");
        depth.StencilWriteMask &= knownBits;
        return new(resolved with { DepthStencil = depth }, reference);
    }
}
