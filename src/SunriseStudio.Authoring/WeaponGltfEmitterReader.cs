using System.Numerics;
using System.Text.Json;
using SunriseStudio.Animation;
using SunriseStudio.Formats;

namespace SunriseStudio.Authoring;

/** One particle emitter marker read from a weapon glTF. */
public sealed record WeaponSourceEmitter(string Object, string System, string Event,
    int Count, float StartSeconds, float LifetimeSeconds, float Speed,
    WeaponAttachmentInput Attachment);

/** Point bursts take their native frame from the authored emitter surface and bone parent. */
public static class WeaponGltfEmitterReader
{
    public const string EjectEvent = "eject";

    /** Muzzle and eject bursts play on every shot from the firing row, not from a clip. */
    public static bool FiresEveryShot(string eventName) => eventName is "muzzle" or EjectEvent;

    /** The reload burst sits in the firing row; its clip gives only its start time. */
    public const string ReloadEvent = "reload";

    /** Muzzle, Eject and Reload emitters; any other event is a clip burst and is skipped unchecked. */
    public static IReadOnlyList<WeaponSourceEmitter> Read(string path,
        IReadOnlyDictionary<string, int> controls, SkeletonRig mechanicalRig, string name)
    {
        string stem = Path.ChangeExtension(Path.GetFullPath(path), null)!;
        using var authored = JsonDocument.Parse(File.ReadAllText(stem + WeaponOriginalAssetImporter.SidecarSuffix));
        using var original = JsonDocument.Parse(File.ReadAllText(stem + OriginalAssetImporter.SidecarSuffix));
        using var gltf = GltfInterchange.ReadJson(path);
        float fps = authored.RootElement.GetProperty("framesPerSecond").GetSingle();
        float units = authored.RootElement.GetProperty("metresPerUnit").GetSingle();
        if (!float.IsFinite(fps) || fps <= 0 || !float.IsFinite(units) || units <= 0)
            throw new InvalidDataException("Weapon emitter units and frame rate must be finite and positive.");
        var effects = authored.RootElement.GetProperty("effects").EnumerateArray().ToArray();
        var sources = original.RootElement.GetProperty("emitters").EnumerateArray().ToArray();
        var geometry = GltfInterchange.Read(path, true);
        var nodes = gltf.RootElement.GetProperty("nodes");
        var parents = Enumerable.Repeat(-1, nodes.GetArrayLength()).ToArray();
        for (int index = 0; index < parents.Length; index++)
            if (nodes[index].TryGetProperty("children", out var children))
                foreach (var child in children.EnumerateArray()) parents[child.GetInt32()] = index;
        var conversion = Matrix4x4.CreateRotationZ(-MathF.PI / 2) * Matrix4x4.CreateScale(units);
        var result = new List<WeaponSourceEmitter>();
        int skipped = 0;
        foreach (var source in sources)
        {
            string obj = source.GetProperty("object").GetString()!, system = source.GetProperty("system").GetString()!;
            var matching = effects.Where(effect => effect.GetProperty("object").GetString() == obj
                && effect.GetProperty("system").GetString() == system).ToArray();
            if (matching.Length != 1 || source.GetProperty("type").GetString() != "EMITTER")
                throw new NotSupportedException("Each weapon particle system needs one emitter settings row.");
            var effect = matching[0];
            string eventName = effect.GetProperty("event").GetString()!;
            if (!FiresEveryShot(eventName) && eventName != ReloadEvent) { skipped++; continue; }
            bool firing = FiresEveryShot(eventName);
            float actionStart = 0, actionEnd = 0;
            if (!firing)
            {
                var clips = authored.RootElement.GetProperty("clips").EnumerateArray()
                    .Where(clip => clip.GetProperty("event").GetString() == eventName).ToArray();
                if (clips.Length != 1)
                    throw new InvalidDataException("Each weapon emitter needs one source action binding.");
                actionStart = clips[0].GetProperty("sourceStartFrame").GetSingle();
                actionEnd = clips[0].GetProperty("sourceEndFrame").GetSingle();
                float actionDuration = WeaponGltfAnimationReader.Read(path,
                    clips[0].GetProperty("action").GetString()!, controls, mechanicalRig, 2, units).SourceDuration;
                if (Math.Abs((actionEnd - actionStart) / fps - actionDuration) > .0001f)
                    throw new InvalidDataException("Particle action frame range differs from its exported animation duration.");
            }
            float start = source.GetProperty("frameStart").GetSingle(), end = source.GetProperty("frameEnd").GetSingle();
            int count = source.GetProperty("count").GetInt32();
            float lifetime = source.GetProperty("lifetime").GetSingle() / fps;
            float speed = effect.GetProperty("normalSpeed").GetSingle() * units;
            if (count is < 1 or > 4096 || !float.IsFinite(start) || end != start
                || !firing && (!float.IsFinite(actionStart) || !float.IsFinite(actionEnd) || actionEnd <= actionStart
                    || start < actionStart || start >= actionEnd)
                || !float.IsFinite(lifetime) || lifetime <= 0 || !float.IsFinite(speed) || speed < 0
                || effect.GetProperty("randomSpeed").GetSingle() != 0
                || effect.GetProperty("lifetimeRandom").GetSingle() != 0
                || effect.GetProperty("gravity").GetSingle() != 0
                || effect.GetProperty("objectVelocity").EnumerateArray().Any(value => value.GetSingle() != 0))
                throw new NotSupportedException("The weapon particle profile needs a deterministic point burst without gravity.");
            if (Math.Abs(effect.GetProperty("particleSize").GetSingle() - .035f) > .000001f
                || effect.GetProperty("sizeRandom").GetSingle() != 0)
                throw new NotSupportedException("The fixed native sprite profile requires Blender preview size 0.035 and size randomness zero.");
            var meshes = geometry.Where(mesh => mesh.Name == obj).ToArray();
            if (meshes.Length == 0) throw new InvalidDataException("Weapon emitter has no exported surface.");
            Vector3 centre = Vector3.Zero, normal = Vector3.Zero;
            float area = 0;
            foreach (var mesh in meshes)
                for (int triangle = 0; triangle < mesh.Indices.Length; triangle += 3)
                {
                    Vector3 a = Vector3.Transform(mesh.Positions[mesh.Indices[triangle]], conversion);
                    Vector3 b = Vector3.Transform(mesh.Positions[mesh.Indices[triangle + 1]], conversion);
                    Vector3 c = Vector3.Transform(mesh.Positions[mesh.Indices[triangle + 2]], conversion);
                    Vector3 cross = Vector3.Cross(b - a, c - a);
                    float weight = cross.Length();
                    centre += (a + b + c) * (weight / 3);
                    normal += cross; area += weight;
                }
            if (!float.IsFinite(area) || area <= 0 || normal.LengthSquared() < area * area * .99f)
                throw new NotSupportedException("Weapon point emitters need one flat surface with a shared normal.");
            centre /= area; normal = Vector3.Normalize(normal);
            Vector3 up = Math.Abs(Vector3.Dot(normal, Vector3.UnitZ)) > .99f ? Vector3.UnitY : Vector3.UnitZ;
            Vector3 side = Vector3.Normalize(Vector3.Cross(up, normal));
            up = Vector3.Cross(normal, side);
            var frame = new Matrix4x4(normal.X, normal.Y, normal.Z, 0,
                side.X, side.Y, side.Z, 0, up.X, up.Y, up.Z, 0, centre.X, centre.Y, centre.Z, 1);
            int node = Enumerable.Range(0, parents.Length).Single(index =>
                nodes[index].TryGetProperty("name", out var nodeName) && nodeName.GetString() == obj);
            int bone = 0;
            for (int current = parents[node], depth = 0; current >= 0; current = parents[current])
            {
                if (++depth > parents.Length) throw new InvalidDataException("Weapon emitter parent chain has a cycle.");
                if (nodes[current].TryGetProperty("name", out var nodeName)
                    && controls.TryGetValue(nodeName.GetString()!, out bone)) break;
                bone = 0;
            }
            if (bone < 0 || bone >= mechanicalRig.Bones.Count)
                throw new InvalidDataException("Weapon emitter parent is outside the mechanical rig.");
            var local = BoneTransform.FromMatrix(frame * mechanicalRig.Bones[bone].InverseBindObject.Matrix);
            // Points are named after their event, so they keep one name across exports.
            result.Add(new(obj, system, eventName, count,
                firing ? 0 : (start - actionStart) / fps, lifetime, speed,
                new(name + "." + eventName, bone, local.Rotation, local.Translation, local.Scale)));
        }
        if (effects.Length != result.Count + skipped)
            throw new InvalidDataException("Weapon particle settings contain an unlisted source system.");
        return result;
    }
}
