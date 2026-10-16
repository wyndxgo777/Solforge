using System.Globalization;
using System.Numerics;
using System.Text.Json;
using SunriseStudio.Animation;

namespace SunriseStudio.Formats;

/** Skinned-mesh glTF reading and writing. One skin joint: bind pose, inverse bind and the source package bone/entity it came from, if any. */
public sealed record InterchangeSkinJoint(string Name, int ParentIndex, Matrix4x4 BindObject,
    Matrix4x4 InverseBindObject, int? SourceBoneIndex, uint? SourceEntityTag);
/** A mesh with its skin joints and per-vertex influences. */
public sealed record InterchangeSkinnedMesh(InterchangeMesh Geometry,
    IReadOnlyList<InterchangeSkinJoint> Joints, IReadOnlyList<VertexSkinWeights> Influences);

/** Skinned-mesh part of the glTF interchange. */
public static partial class GltfInterchange
{
    /** Reads one four-weight primitive; joint transforms and inverse binds define its bind shape. */
    public static InterchangeSkinnedMesh ReadSkinned(string path)
    {
        var (json, binary) = Split(File.ReadAllBytes(path));
        using var parsed = JsonDocument.Parse(json);
        var document = parsed.RootElement;
        RequireVersion(document);
        var nodes = document.GetProperty("nodes");
        int count = nodes.GetArrayLength();
        if (count is < 1 or > 16384) throw new InvalidDataException("The skin scene has too many nodes.");
        var parents = Enumerable.Repeat(-1, count).ToArray();
        var world = new Matrix4x4[count];
        var included = new bool[count];
        var meshNodes = new List<int>();
        void Visit(int index, int parent, Matrix4x4 transform, int depth)
        {
            if ((uint)index >= count || included[index] || depth > 256)
                throw new InvalidDataException("The skin scene hierarchy is invalid.");
            included[index] = true; parents[index] = parent;
            var node = nodes[index];
            world[index] = NodeTransform(node) * transform;
            if (node.TryGetProperty("mesh", out _)) meshNodes.Add(index);
            if (node.TryGetProperty("children", out var children))
                foreach (var child in children.EnumerateArray()) Visit(child.GetInt32(), index, world[index], depth + 1);
        }
        foreach (var root in document.GetProperty("scenes")[OptionalInt(document, "scene")].GetProperty("nodes").EnumerateArray())
            Visit(root.GetInt32(), -1, Matrix4x4.Identity, 0);
        if (meshNodes.Count != 1 || !nodes[meshNodes[0]].TryGetProperty("skin", out var skinIndex))
            throw new NotSupportedException("Export one skinned mesh and its armature.");
        var meshNode = nodes[meshNodes[0]];
        var primitives = document.GetProperty("meshes")[meshNode.GetProperty("mesh").GetInt32()].GetProperty("primitives");
        if (primitives.GetArrayLength() != 1)
            throw new NotSupportedException("Compatible skin import currently needs one material primitive.");
        var primitive = primitives[0];
        if (primitive.TryGetProperty("targets", out _) || primitive.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4)
            throw new NotSupportedException("Compatible skin import needs triangles without morph targets.");
        var attributes = primitive.GetProperty("attributes");
        if (attributes.EnumerateObject().Any(attribute => attribute.Name is not
            ("POSITION" or "NORMAL" or "TEXCOORD_0" or "JOINTS_0" or "WEIGHTS_0")))
            throw new NotSupportedException("Extra UVs, colors, tangents and additional skin influences are not supported.");
        int jointAccessor = attributes.GetProperty("JOINTS_0").GetInt32();
        int weightAccessor = attributes.GetProperty("WEIGHTS_0").GetInt32();
        var accessorList = document.GetProperty("accessors");
        int jointType = accessorList[jointAccessor].GetProperty("componentType").GetInt32();
        var weightInfo = accessorList[weightAccessor];
        int weightType = weightInfo.GetProperty("componentType").GetInt32();
        bool normalized = weightInfo.TryGetProperty("normalized", out var normalization) && normalization.GetBoolean();
        if (jointType is not (5121 or 5123) || weightType is not (5121 or 5123 or 5126)
            || normalized != (weightType != 5126))
            throw new InvalidDataException("Skin accessor component types are invalid.");
        var skin = document.GetProperty("skins")[skinIndex.GetInt32()];
        int[] jointNodes = skin.GetProperty("joints").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        if (jointNodes.Length is < 1 or > 4096 || jointNodes.Distinct().Count() != jointNodes.Length
            || jointNodes.Any(index => (uint)index >= count || !included[index]))
            throw new InvalidDataException("Skin joints must belong to this scene and be unique.");
        int Root(int node) { while (parents[node] >= 0) node = parents[node]; return node; }
        if (jointNodes.Any(node => Root(node) != Root(jointNodes[0])))
            throw new InvalidDataException("Skin joints must have a common scene root.");
        if (skin.TryGetProperty("skeleton", out var skeleton))
        {
            int ancestor = skeleton.GetInt32();
            bool Contains(int node)
            {
                while (node >= 0 && node != ancestor) node = parents[node];
                return node == ancestor;
            }
            if ((uint)ancestor >= count || !included[ancestor] || jointNodes.Any(node => !Contains(node)))
                throw new InvalidDataException("The skin skeleton node is not an ancestor of every joint.");
        }
        if (!skin.TryGetProperty("inverseBindMatrices", out var inverseAccessor))
            throw new NotSupportedException("Export inverse bind matrices for the compatible rig.");
        var inverseInfo = accessorList[inverseAccessor.GetInt32()];
        if (inverseInfo.GetProperty("componentType").GetInt32() != 5126
            || inverseInfo.TryGetProperty("normalized", out var inverseNormalized) && inverseNormalized.GetBoolean()
            || inverseInfo.GetProperty("count").GetInt32() < jointNodes.Length
            || inverseInfo.GetProperty("count").GetInt32() > 4096)
            throw new InvalidDataException("Skin inverse bind matrices have an invalid format or count.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var buffers = ReadBuffers(document.GetProperty("buffers"), binary, directory);
        var data = new GltfAccessors(document, buffers);
        float[] inverseValues = data.Floats(inverseAccessor.GetInt32(), 16);
        var inverseBind = Enumerable.Range(0, jointNodes.Length).Select(index => SkinMatrix(inverseValues.AsSpan(index * 16, 16))).ToArray();
        var conversion = new Matrix4x4(1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1);
        var bindShape = inverseBind[0] * world[jointNodes[0]] * conversion;
        if (!Matrix4x4.Invert(bindShape, out var inverseShape)) throw new InvalidDataException("The skin bind shape is singular.");
        var jointLookup = jointNodes.Select((node, index) => (node, index)).ToDictionary(value => value.node, value => value.index);
        var joints = new InterchangeSkinJoint[jointNodes.Length];
        for (int index = 0; index < jointNodes.Length; index++)
        {
            int nodeIndex = jointNodes[index];
            var node = nodes[nodeIndex];
            var bind = world[nodeIndex] * conversion;
            if (!SameSkinMatrix(bindShape, inverseBind[index] * bind))
                throw new InvalidDataException("Joint transforms are not at the skin's common bind pose.");
            int parent = parents[nodeIndex];
            while (parent >= 0 && !jointLookup.ContainsKey(parent)) parent = parents[parent];
            string name = node.TryGetProperty("name", out var label) ? label.GetString() ?? "" : "";
            int? sourceBone = null; uint? sourceEntity = null;
            if (node.TryGetProperty("extras", out var extras))
            {
                if (extras.TryGetProperty("sunriseBoneIndex", out var ordinal)) sourceBone = ordinal.GetInt32();
                if (extras.TryGetProperty("sunriseEntityTag", out var entity) && entity.GetString() is { } text
                    && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint tag)) sourceEntity = tag;
            }
            joints[index] = new(name, parent < 0 ? -1 : jointLookup[parent], bind,
                inverseShape * inverseBind[index], sourceBone, sourceEntity);
        }
        var geometry = ReadMeshes(document, buffers, true, bindShape).Single();
        uint[] indices = data.Indices(jointAccessor, 4);
        float[] weights = data.Floats(weightAccessor, 4);
        if (indices.Length != geometry.Positions.Length * 4 || weights.Length != indices.Length
            || indices.Any(index => index >= jointNodes.Length))
            throw new InvalidDataException("Skin influences do not match the mesh or joint list.");
        var influences = new VertexSkinWeights[geometry.Positions.Length];
        for (int vertex = 0; vertex < influences.Length; vertex++)
        {
            int offset = vertex * 4;
            var values = new Vector4(weights[offset], weights[offset + 1], weights[offset + 2], weights[offset + 3]);
            if (values != Vector4.Clamp(values, Vector4.Zero, Vector4.One)
                || MathF.Abs(values.X + values.Y + values.Z + values.W - 1) > .00001f)
                throw new InvalidDataException("Skin weights must sum to one.");
            for (int first = 0; first < 4; first++)
                for (int second = first + 1; second < 4; second++)
                    if (weights[offset + first] > 0 && weights[offset + second] > 0
                        && indices[offset + first] == indices[offset + second])
                        throw new InvalidDataException("A glTF vertex repeats a weighted joint.");
            influences[vertex] = new((int)indices[offset], (int)indices[offset + 1], (int)indices[offset + 2], (int)indices[offset + 3], values);
        }
        return new(geometry, joints, influences);
    }

    private static Matrix4x4 SkinMatrix(ReadOnlySpan<float> values)
    {
        if (values[3] != 0 || values[7] != 0 || values[11] != 0 || values[15] != 1)
            throw new InvalidDataException("Skin inverse bind matrices must be affine.");
        return new(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7],
            values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15]);
    }

    /** All joints must apply the same bind shape before their bone transforms. */
    private static bool SameSkinMatrix(Matrix4x4 left, Matrix4x4 right)
    {
        float[] a = [left.M11,left.M12,left.M13,left.M14,left.M21,left.M22,left.M23,left.M24,left.M31,left.M32,left.M33,left.M34,left.M41,left.M42,left.M43,left.M44];
        float[] b = [right.M11,right.M12,right.M13,right.M14,right.M21,right.M22,right.M23,right.M24,right.M31,right.M32,right.M33,right.M34,right.M41,right.M42,right.M43,right.M44];
        return a.Zip(b).All(pair => float.IsFinite(pair.First) && float.IsFinite(pair.Second)
            && MathF.Abs(pair.First - pair.Second) <= .0002f);
    }
}
