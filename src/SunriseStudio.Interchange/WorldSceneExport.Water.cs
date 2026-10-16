using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Interchange;

/** Water part of world scene export (sea height). */
public static partial class WorldSceneExport
{
    /** Moves flat sea parts to the height of their map's wave settings, where the game draws them. */
    private sealed class OceanHeights(PackageCatalog catalog, uint mapTag)
    {
        // Extern 91 float 6 is the sea height; a vertex program that reads it ignores the model's height.
        private const byte SeaHeightElement = 6;
        // A part whose height spread is larger than this is not a flat sea.
        private const float FlatTolerance = .01f;
        private readonly GameMaterialReader reader = new(catalog);
        private readonly Dictionary<uint, bool> placesAtSea = [];
        private readonly NativeOceanSettings? settings = NativeOceanSettings.Resolve(catalog, mapTag, NativeScenePlan.RenderGlobalsTag);
        private int moved;
        private float largestShift;

        /** The part's transform, raised or lowered to the sea; unchanged for any other part. */
        public Matrix4x4 Place(MeshInstance instance)
        {
            if (settings is null || instance.Mesh.Vertices.Length == 0 || !PlacesAtSea(instance.Mesh.MaterialTag)) return instance.Transform;
            float low = float.MaxValue, high = float.MinValue;
            foreach (var vertex in instance.Mesh.Vertices)
            {
                float z = Vector3.Transform(vertex.Position, instance.Transform).Z;
                low = MathF.Min(low, z); high = MathF.Max(high, z);
            }
            if (high - low > FlatTolerance) return instance.Transform;
            float shift = settings.Floats[SeaHeightElement] - low;
            moved++; largestShift = MathF.Max(largestShift, MathF.Abs(shift));
            return instance.Transform * Matrix4x4.CreateTranslation(0, 0, shift);
        }

        /** One notice naming the settings and the parts moved. */
        public void Report(List<BundleNotice> notices)
        {
            if (moved == 0 || settings is null) return;
            notices.Add(new("Geometry", "Sea height", $"{moved} sea parts moved by up to {largestShift:0.##} m to height "
                + $"{settings.Floats[SeaHeightElement]:0.##} from wave settings 0x{settings.Tag:X8}; the waves are flat.", settings.Tag));
        }

        private bool PlacesAtSea(uint material)
        {
            if (placesAtSea.TryGetValue(material, out bool known)) return known;
            bool reads;
            try
            {
                reads = TfxProgramInspection.Read(reader.ReadExecution(material).Vertex.TfxBytecode).Dependencies
                    .Any(d => d.Kind == "Float" && d.Extern == NativeOcean.Extern && d.Element == SeaHeightElement);
            }
            catch (Exception error) when (Recoverable(error)) { reads = false; }
            placesAtSea.Add(material, reads);
            return reads;
        }
    }
}
