using System.Numerics;
using SunriseStudio.Scene;

/** Every decoded value a placement update may touch, as exact text, so two loads compare line by line. */
internal static class WorldFingerprint
{
    /** Summarizes a world as comparable text lines. */
    internal static List<string> Of(WorldDocument world)
    {
        var lines = new List<string>();
        foreach (var (label, scene) in Scenes(world))
        {
            lines.Add($"{label} counts instances={scene.Instances.Count} markers={scene.Markers.Count} lights={scene.LightTables.Count} "
                + $"cubemaps={scene.Cubemaps.Count} errors={scene.DecodeErrorCount} unsupported={scene.UnsupportedCount} diagnostics={scene.Diagnostics.Count}");
            for (int index = 0; index < scene.Instances.Count; index++)
            {
                var item = scene.Instances[index];
                lines.Add($"{label} instance {index} {item.Mesh.TagHash:X8}:{item.Mesh.PartIndex}:{item.Mesh.LodLevel} {M(item.Transform)} "
                    + $"{Convert.ToHexString(item.NativeInstanceData.Span)} {M(item.ObjectPlacement?.WorldTransform)} {item.Name} {item.Layer} "
                    + $"{item.BubbleHash:X8} {item.InstanceSourceTag:X8}:{item.InstanceSourceIndex} {item.PlacementSourceTag:X8}:{item.PlacementSourceOffset}");
            }
            for (int index = 0; index < scene.Markers.Count; index++)
            {
                var item = scene.Markers[index];
                lines.Add($"{label} marker {index} {item.Name} {item.Semantic} {item.Layer} {item.SourceTag:X8}:{item.SourceOffset} "
                    + $"{V(item.Position)} {Q(item.Rotation)} {V(item.Extents)} {M(item.ObjectPlacement?.WorldTransform)} {M(item.Effect?.Transform)} "
                    + $"{item.AssociationIsAmbiguous}");
            }
            foreach (var table in scene.LightTables)
            {
                lines.Add($"{label} lights {table.Table.TagHash:X8} {table.PlacementSourceTag:X8}:{table.PlacementSourceOffset} {M(table.PlacementTransform)}");
                foreach (var row in table.Table.Rows)
                    lines.Add($"{label} light {table.Table.TagHash:X8}:{row.Index} {Q(row.Rotation)} {V(row.Translation)} {F(row.Scale)} "
                        + $"{M(row.Transform)} {M(row.VolumeWorldTransform)}");
            }
            foreach (var cubemap in scene.Cubemaps)
                lines.Add($"{label} cubemap {cubemap.PlacementSourceTag:X8}:{cubemap.AuxiliaryOffset} {M(cubemap.PlacementTransform)}");
        }
        foreach (var row in world.Objects)
            lines.Add($"object {row.Id} {row.Name} {row.Kind} {V(row.Position)} {V(row.Extents)} {row.AssetTag:X8} {row.BubbleHash:X8}");
        return lines;
    }

    /** Empty when both lists match; otherwise the first differing lines. */
    internal static string Compare(List<string> incremental, List<string> full)
    {
        if (incremental.Count != full.Count) return $"line counts {incremental.Count} and {full.Count} differ";
        var different = Enumerable.Range(0, full.Count).Where(i => incremental[i] != full[i]).Take(3)
            .Select(i => $"\n  incremental: {incremental[i]}\n  full:        {full[i]}").ToArray();
        return string.Concat(different);
    }

    private static IEnumerable<(string, SceneDocument)> Scenes(WorldDocument world)
    {
        yield return ("map", world.MapScene);
        if (world.MissionScene is { } mission) yield return ("mission", mission);
        yield return ("world", world.Scene);
    }

    private static string F(float value) => BitConverter.SingleToUInt32Bits(value).ToString("X8");
    private static string V(Vector3? value) => value is { } v ? $"{F(v.X)},{F(v.Y)},{F(v.Z)}" : "-";
    private static string Q(Quaternion value) => $"{F(value.X)},{F(value.Y)},{F(value.Z)},{F(value.W)}";
    private static string M(Matrix4x4? value) => value is not { } m ? "-" : string.Join(",", new[]
    {
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44
    }.Select(F));
}
