using System.Numerics;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** Extern 86 vector 0 is the low bit of the part's dye slot, splatted; 0x7FF742E71450 writes it. */
public static class NativeGearDye
{
    public static readonly TfxExternKey Key = new(86, 0);
    // Entity part row byte +0x1A is the dye slot: pair index times two plus the bit.
    private const int PartSlotByte = 0x1A;

    /** The extern starts at zero, so a part that is not an entity part row reads bit 0; the cloth draw 0x7FF742E78D60 writes 0. */
    public static int PartBit(MeshInstance instance) =>
        !instance.RequiresClothSimulation && instance.Mesh.Native is { Kind: NativeMeshKind.Entity, SourceHeader: { } model }
            && instance.Mesh.PartIndex >= 0 && instance.Mesh.PartIndex + PartSlotByte < model.Length
            ? model[instance.Mesh.PartIndex + PartSlotByte] & 1 : 0;

    /** True when either stage reads the gear-dye extern. */
    public static bool Reads(MaterialExecutionData execution) =>
        new[] { execution.Vertex, execution.Pixel }.Any(stage => stage.TfxBytecode.Length != 0
            && TfxProgramInspection.Read(stage.TfxBytecode).Dependencies.Any(d => d.Extern == Key.Extern));

    /** Sets the dye bit the shaders read. */
    public static void Apply(TfxInputs inputs, int bit) => inputs.Vectors[Key] = new Vector4(bit);
}
