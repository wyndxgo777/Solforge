using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Interchange;

/** One distinct collision shape in its own space; placements share it, as Blender objects share one mesh. */
public sealed record BundleCollisionPiece(string Id, uint Tag, int Offset, string Class, string Kind, float[][] Positions, int[] Indices);

/**
 * One placed shape. Transform is the world matrix, row by row. Editable is false for a shape placed
 * alone and for a shape array, whose placement is not written.
 */
public sealed record BundleCollisionPlacement(string Id, string Piece, string Name, float[] Transform, string Source, uint Entity,
    uint CompoundTag, int CompoundOffset, int Instance, bool Editable);

/** The collision section of an asset bundle. Sources are the SHA-256 of each tag at export time. */
public sealed record BundleCollision(string Format, int Version, uint RootTag, IReadOnlyDictionary<string, string> Sources,
    IReadOnlyList<BundleCollisionPiece> Pieces, IReadOnlyList<BundleCollisionPlacement> Placements, IReadOnlyList<string> Notes)
{
    /** Each compound's owner material tags, by compound tag in hex; a new shape may name one. */
    public IReadOnlyDictionary<string, string[]>? Materials { get; init; }
}

/** Edit of a collision piece's placement transform. */
public sealed record BundleCollisionPlacementEdit(string Id, float[] Transform);
/** Edit of a collision piece's triangle mesh. */
public sealed record BundleCollisionShapeEdit(string Piece, float[][] Positions, int[] Indices);

/**
 * A collision object the export did not hold. Without positions it is another instance of placement
 * Source. With positions it is a new shape: Kind "Convex" or a mesh, in compound Compound or the
 * nearest one, with material tag Material or each triangle's nearest material. Tags are hex.
 */
public sealed record BundleCollisionAddition(string? Source, string Name, float[] Transform, float[][]? Positions = null,
    int[]? Indices = null, string? Kind = null, string? Compound = null, string? Material = null);

/** Only the placements and shapes the Blender add-on found changed, and the collision objects it found added. */
public sealed record BundleCollisionEdits(IReadOnlyList<BundleCollisionPlacementEdit> Placements, IReadOnlyList<BundleCollisionShapeEdit> Shapes)
{
    public IReadOnlyList<BundleCollisionAddition>? Added { get; init; }
}

/** Writes the collision section of a bundle and turns its edits into collision import input. */
public static class BundleCollisionCodec
{
    public const string Format = "sunrise-collision", PathName = "collision/collision.json";

    /** Writes the section for the scene's collision. @return Its bundle path, or null when there is none. */
    public static string? Export(PackageCatalog catalog, SceneDocument scene, string root, List<BundleNotice> notices, CancellationToken token)
    {
        var collision = new CollisionSceneLoader(catalog).Load(scene, token);
        if (collision.IsEmpty) return null;
        var pieces = collision.Pieces.Select(piece => new BundleCollisionPiece($"piece_{piece.Index}", piece.Tag, piece.Offset, piece.Shape.Class,
            piece.Kind, piece.Shape.Vertices.Select(v => new[] { v.X, v.Y, v.Z }).ToArray(), piece.Shape.Indices)).ToArray();
        var placements = collision.Placements.Select((placement, index) =>
        {
            var entity = collision.Entities[placement.Entity];
            bool editable = placement.Instance >= 0 && entity.Source != CollisionSource.ShapeArray;
            return new BundleCollisionPlacement($"placement_{index}", $"piece_{placement.Piece}", CollisionInterchange.NodeName(collision, placement),
                Values(placement.World), entity.Source.ToString(), entity.Tag, placement.CompoundTag, placement.CompoundOffset, placement.Instance, editable);
        }).ToArray();
        var sources = CollisionInterchange.Sources(collision, catalog).ToDictionary(pair => CollisionInterchange.Hex(pair.Key), pair => pair.Value);
        BundlePaths.Write(BundlePaths.Resolve(root, PathName), new BundleCollision(Format, 1, scene.TagHash, sources, pieces, placements, collision.Notes)
            { Materials = CollisionInterchange.Materials(collision, catalog) });
        notices.Add(new("Collision", "Instances and shapes",
            $"{placements.Length:N0} collision shapes in the Collision collection. Moves, vertex edits, new mesh topology, copies "
            + "and new objects import; removing collision does not."));
        foreach (var (kind, count) in collision.Undecoded) notices.Add(new("Collision", "No triangles", $"{kind}: {count:N0}"));
        return PathName;
    }

    /** Every exported placement and shape with the edited ones replaced, then the added ones; transforms are game coordinates. */
    public static CollisionImportInput Input(BundleCollision document, BundleCollisionEdits edits)
    {
        if (document.Format != Format || document.Version != 1) throw new InvalidDataException("Unsupported bundle collision section.");
        var moved = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var edit in edits.Placements ?? [])
            if (edit is null || !moved.TryAdd(edit.Id, edit.Transform)) throw new InvalidDataException("A collision placement edit repeats.");
        var reshaped = new Dictionary<string, BundleCollisionShapeEdit>(StringComparer.Ordinal);
        foreach (var edit in edits.Shapes ?? [])
            if (edit is null || !reshaped.TryAdd(edit.Piece, edit)) throw new InvalidDataException("A collision shape edit repeats.");
        var pieces = document.Pieces.ToDictionary(piece => piece.Id, StringComparer.Ordinal);
        // The first placement with an id wins, as a linear search would.
        var placementsById = new Dictionary<string, BundleCollisionPlacement>(StringComparer.Ordinal);
        foreach (var placement in document.Placements) placementsById.TryAdd(placement.Id, placement);
        if (moved.Keys.Any(id => !placementsById.ContainsKey(id)) || reshaped.Keys.Any(id => !pieces.ContainsKey(id)))
            throw new NotSupportedException("A collision edit names a placement or shape that was not exported.");
        var placements = document.Placements.Select(p =>
        {
            var piece = pieces[p.Piece];
            return new CollisionPlacementInput(p.CompoundTag, p.Instance, piece.Tag, piece.Offset,
                Matrix(moved.TryGetValue(p.Id, out var transform) ? transform : p.Transform));
        }).ToArray();
        var shapes = document.Pieces.Select(piece => reshaped.TryGetValue(piece.Id, out var edit)
            ? new CollisionShapeInput(piece.Tag, piece.Offset, edit.Positions.Select(Vector).ToArray(), edit.Indices)
            : new CollisionShapeInput(piece.Tag, piece.Offset, piece.Positions.Select(Vector).ToArray(), piece.Indices)).ToArray();
        var added = (edits.Added ?? []).Select(addition => Addition(placementsById, pieces, addition));
        return new([.. placements, .. added], shapes, document.Sources.ToDictionary(pair => CollisionInterchange.ParseHex(pair.Key), pair => pair.Value));
    }

    /** A copy of an exported placement, or a new shape in game coordinates. */
    private static CollisionPlacementInput Addition(IReadOnlyDictionary<string, BundleCollisionPlacement> placements,
        IReadOnlyDictionary<string, BundleCollisionPiece> pieces, BundleCollisionAddition addition)
    {
        if (addition is null) throw new InvalidDataException("A collision addition is empty.");
        var source = addition.Source is null ? null : placements.GetValueOrDefault(addition.Source)
            ?? throw new InvalidDataException("A collision addition names a placement that was not exported.");
        var world = Matrix(addition.Transform);
        if (addition.Positions is null)
        {
            if (source is not { Editable: true })
                throw new NotSupportedException($"{addition.Name} copies a shape that is not a compound instance, so it cannot be placed again.");
            var piece = pieces[source.Piece];
            return new(source.CompoundTag, source.Instance, piece.Tag, piece.Offset, world) { Added = true };
        }
        uint compound = addition.Compound is { Length: > 0 } text ? CollisionInterchange.ParseHex(text) : source?.CompoundTag ?? 0;
        return new(compound, -1, 0, 0, world)
        {
            Added = true,
            NewShape = new(addition.Name, addition.Positions.Select(Vector).ToArray(), addition.Indices ?? [], addition.Kind == "Convex",
                addition.Material is { Length: > 0 } material ? CollisionInterchange.ParseHex(material) : null)
        };
    }

    private static float[] Values(Matrix4x4 m) => [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];

    private static Matrix4x4 Matrix(float[] v)
    {
        if (v is not { Length: 16 } || v.Any(value => !float.IsFinite(value))) throw new InvalidDataException("A collision transform is invalid.");
        return new(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
    }

    private static Vector3 Vector(float[] v) => v is { Length: 3 } && v.All(float.IsFinite)
        ? new(v[0], v[1], v[2]) : throw new InvalidDataException("A collision vertex is invalid.");
}
