using SunriseStudio.Formats;

namespace SunriseStudio.Authoring;

/** One mesh primitive with the bone palette its vertices index. */
public sealed record WeaponSourcePrimitive(InterchangeMesh Mesh, IReadOnlyList<short> PaletteIndices);

/** A rigid weapon vertex keeps one authored joint; mixed weights need another native profile. */
public static class WeaponGltfGeometryReader
{
    /** Reads the visual mesh primitives of a weapon glTF. */
    public static IReadOnlyList<WeaponSourcePrimitive> Read(string path,
        IReadOnlyDictionary<string, int> controls, IReadOnlySet<string> visualMeshNames)
    {
        using var file = new WeaponGltfAnimationReader.Document(path);
        var document = file.Root;
        var nodes = document.GetProperty("nodes");
        var meshes = document.GetProperty("meshes");
        var geometry = GltfInterchange.Read(path, true).Where(mesh => visualMeshNames.Contains(mesh.Name))
            .GroupBy(mesh => (mesh.Name, Material: mesh.MaterialIndex ?? -1))
            .ToDictionary(group => group.Key, group => new Queue<InterchangeMesh>(group));
        var result = new List<WeaponSourcePrimitive>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("mesh", out var meshIndex) || !node.TryGetProperty("name", out var name)
                || !visualMeshNames.Contains(name.GetString()!)) continue;
            int[]? joints = node.TryGetProperty("skin", out var skinIndex)
                ? document.GetProperty("skins")[skinIndex.GetInt32()].GetProperty("joints")
                    .EnumerateArray().Select(value => value.GetInt32()).ToArray() : null;
            int[]? palette = joints?.Select(index =>
            {
                string joint = nodes[index].GetProperty("name").GetString()!;
                return controls.TryGetValue(joint, out int mapped) ? mapped
                    : throw new InvalidDataException("A weapon joint has no native control assignment.");
            }).ToArray();
            foreach (var primitive in meshes[meshIndex.GetInt32()].GetProperty("primitives").EnumerateArray())
            {
                if (primitive.TryGetProperty("targets", out _) || primitive.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4)
                    throw new NotSupportedException("Weapon geometry needs triangle primitives without morph targets.");
                int material = primitive.TryGetProperty("material", out var assigned) ? assigned.GetInt32() : -1;
                var key = (name.GetString()!, material);
                if (!geometry.TryGetValue(key, out var available) || !available.TryDequeue(out var mesh))
                    throw new InvalidDataException("Weapon primitive identities disagree with the exported scene.");
                var indices = new short[mesh.Positions.Length];
                if (palette is not null)
                {
                    var attributes = primitive.GetProperty("attributes");
                    if (attributes.EnumerateObject().Any(attribute => attribute.Name.StartsWith("JOINTS_", StringComparison.Ordinal)
                        && attribute.Name != "JOINTS_0" || attribute.Name.StartsWith("WEIGHTS_", StringComparison.Ordinal)
                        && attribute.Name != "WEIGHTS_0"))
                        throw new NotSupportedException("Weapon geometry has extra skin streams.");
                    var jointValues = file.SkinValues(attributes.GetProperty("JOINTS_0").GetInt32(), false);
                    var weights = file.SkinValues(attributes.GetProperty("WEIGHTS_0").GetInt32(), true);
                    if (jointValues.Length != indices.Length || weights.Length != indices.Length)
                        throw new InvalidDataException("Weapon skin and position counts disagree.");
                    for (int vertex = 0; vertex < indices.Length; vertex++)
                    {
                        int[] lanes = Enumerable.Range(0, 4).Where(lane => weights[vertex][lane] > .000001f).ToArray();
                        if (lanes.Length != 1 || Math.Abs(weights[vertex].Sum() - 1) > .00001f
                            || weights[vertex].Any(value => value < 0 || value > 1))
                            throw new NotSupportedException("The rigid weapon profile needs one full bone weight per vertex.");
                        int joint = checked((int)jointValues[vertex][lanes[0]]);
                        if (joint < 0 || joint >= palette.Length)
                            throw new InvalidDataException("Weapon skin joint leaves its exported skeleton.");
                        indices[vertex] = checked((short)palette[joint]);
                    }
                }
                result.Add(new(mesh, indices));
            }
        }
        if (geometry.Values.Any(queue => queue.Count != 0) || result.Count == 0)
            throw new InvalidDataException("Weapon scene contains an unconsumed visual primitive.");
        return result;
    }
}
