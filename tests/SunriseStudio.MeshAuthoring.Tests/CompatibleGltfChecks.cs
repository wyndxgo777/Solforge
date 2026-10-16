using System.Numerics;
using SunriseStudio.Animation;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

/** Checks preparing a compatible glTF: native bone identity, parents and skin profile are preserved in package order. */
internal static class CompatibleGltfChecks
{
    /** A file round trip must retain the installed rig before any native replacement is prepared. */
    public static void Run(string gameRoot, string path)
    {
        const uint entity = 0x80B82FE3, model = 0x80C4424F;
        using var catalog = PackageCatalog.Open(gameRoot);
        var file = GltfInterchange.ReadSkinned(path);
        var rig = new SkeletonReader(catalog).Read(entity);
        float bindError = 0, inverseError = 0;
        foreach (var joint in file.Joints)
        {
            if (joint.SourceEntityTag != entity || joint.SourceBoneIndex is not int index || (uint)index >= rig.Bones.Count)
                throw new InvalidDataException("The owned file lost its native bone identity.");
            int parent = joint.ParentIndex < 0 ? -1 : file.Joints[joint.ParentIndex].SourceBoneIndex ?? -2;
            if (parent != rig.Bones[index].ParentIndex) throw new InvalidDataException("The owned file changed a bone parent.");
            bindError = MathF.Max(bindError, Difference(joint.BindObject, rig.Bones[index].BindObject.Matrix));
            inverseError = MathF.Max(inverseError, Difference(joint.InverseBindObject, rig.Bones[index].InverseBindObject.Matrix));
        }
        Console.WriteLine($"compatible_gltf file={Path.GetFileName(path)} bones={file.Joints.Count} vertices={file.Geometry.Positions.Length} bind_error={bindError:G9} inverse_error={inverseError:G9}");
        ushort package = Enumerable.Range(0x100, 0xC00).Select(value => (ushort)value)
            .First(value => !catalog.ContainsInstalledPackage(value));
        var prepared = CompatibleSkinnedMesh.PrepareGltf(catalog, path,
            new(entity, model, 0x300, "Owned glTF skin", package, 0));
        if (bindError > .0002f || inverseError > .0002f || prepared.ConsumedBones != 63
            || prepared.VertexCount != file.Geometry.Positions.Length || prepared.Additions.Count != 6)
            throw new InvalidDataException("The file did not preserve the admitted native skin profile.");
        Console.WriteLine("compatible_gltf prepare=pass joints=package-order mesh_node=ignored source=unchanged");
        CompatibleSkinGpuChecks.Run(catalog, prepared, file);
    }

    private static float Difference(Matrix4x4 a, Matrix4x4 b)
    {
        float result = 0;
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++) result = MathF.Max(result, MathF.Abs(a[row, column] - b[row, column]));
        return result;
    }
}
