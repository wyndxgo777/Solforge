using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Scene.Collision;
using static Checks;

/** Custom primitives, writer, encoder and vertex moves on the owned install. */
internal static class MeshOwnedChecks
{
    private const ushort LeviathanPackage = 0x36E, SabrePackage = 0x3D5;

    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(PackageCatalog catalog)
    {
        // Reference hashes over tag, section, primitive, kind, codec, vertices and radius.
        CustomReference(catalog, 0x36E, 2631, "96ac385984080e912b4d021f1852522e2a23172b");
        CustomReference(catalog, 0x357, 10361, "fac739d49d2e2e17719918242776ad35352f2115");
        CustomReference(catalog, 0x3BF, 57272, "e6590e722dd6c56d16f13046f23c95fd474b5eac");
        CustomReference(catalog, 0x3B8, 18890, "efc23754859945ec370fbf0589230209195772e7");
        foreach (ushort package in new[] { LeviathanPackage, SabrePackage }) Package(catalog, package);
    }

    /** Hashes every custom primitive of a package the way the reference decoder does. */
    private static void CustomReference(PackageCatalog catalog, ushort package, int expectedCount, string expectedHash)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        int count = 0;
        var forms = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var header = new byte[20];
        foreach (var entry in catalog.Entries.Where(e => e.PackageId == package && e.Type == 26 && e.SubType == 7).OrderBy(e => e.EntryIndex))
        {
            byte[] bytes = catalog.ReadOriginal(entry.TagHash);
            if (!HavokPackfile.IsPackfile(bytes)) continue;
            var file = HavokPackfile.Parse(bytes);
            if (file.RootClass != CompressedMesh.ClassName) continue;
            var mesh = CompressedMesh.Read(file, file.RootOffset);
            for (int s = 0; s < mesh.Sections.Count; s++)
                for (int p = 0; p < mesh.Sections[s].PrimitiveCount; p++)
                {
                    if (CompressedMesh.TypeOf(mesh.Primitive(mesh.Sections[s], p)) != CompressedPrimitiveType.Custom) continue;
                    var piece = mesh.DecodeCustom(s, p) ?? throw new InvalidDataException($"{entry.TagHash:X8} custom {s}/{p} does not decode.");
                    count++;
                    Add(forms, $"kind{piece.Kind}-codec{piece.Codec}{(piece.HasRadius ? "-radius" : "")}");
                    BinaryPrimitives.WriteUInt32LittleEndian(header, entry.TagHash);
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), s);
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), p);
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), piece.Kind);
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), piece.Codec);
                    sha.AppendData(header);
                    var buffer = new byte[4];
                    foreach (var v in piece.Vertices)
                        foreach (float f in new[] { v.X, v.Y, v.Z })
                        {
                            BinaryPrimitives.WriteSingleLittleEndian(buffer, f);
                            sha.AppendData(buffer);
                        }
                    if (piece.HasRadius)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(buffer, piece.Radius);
                        sha.AppendData(buffer);
                    }
                }
        }
        string hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        Check(count == expectedCount && hash == expectedHash, $"Package {package:X4} customs differ: count={count} hash={hash}");
        Console.WriteLine($"PASS collision-custom-reference package={package:X4} custom={count} forms=[{string.Join(",", forms.Select(x => $"{x.Key}={x.Value}"))}] hash=reference");
    }

    /** Each mesh and convex child of a package's compounds: decode, rewrite, encode, move. */
    private static void Package(PackageCatalog catalog, ushort package)
    {
        var context = new ObjectEditContext(catalog, catalog.ReadOriginal);
        var index = new StaticCollisionRule.CollisionIndex(context, package);
        var tags = index.Entities.SelectMany(e => e.Owner.ImportTags).Where(t => catalog.TryGetEntry(t, out _)).Distinct().Order().ToArray();
        int meshes = 0, convex = 0, same = 0, convexSame = 0, custom = 0, customDecoded = 0, installedClean = 0, encoded = 0;
        int moved = 0, planeCountSame = 0, convexMoved = 0, bevels = 0, rebuiltSubset = 0;
        long sourceTriangles = 0, encodedTriangles = 0;
        double encodeRatio = 0, moveRatio = 0, planeAngle = 0, planeOffset = 0;
        var problems = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var rewriteMismatch = new List<string>();
        var convexFailures = new List<string>();
        foreach (uint tag in tags)
        {
            byte[] bytes = catalog.ReadOriginal(tag);
            var file = HavokPackfile.Parse(bytes);
            if (file.RootClass == CollisionShapes.ConvexVertices)
            {
                convex++;
                var (sameBytes, countSame, angle, offset, movedOk, missing, subset) = ConvexChild(file, bytes);
                bevels += missing;
                rebuiltSubset += subset ? 1 : 0;
                convexSame += sameBytes ? 1 : 0;
                planeCountSame += countSame ? 1 : 0;
                planeAngle = Math.Max(planeAngle, angle);
                planeOffset = Math.Max(planeOffset, offset);
                if (movedOk is null) convexMoved++;
                else if (convexFailures.Count < 5) convexFailures.Add($"{tag:X8} {movedOk}");
                continue;
            }
            if (file.RootClass != CompressedMesh.ClassName) continue;
            meshes++;
            var mesh = CompressedMesh.Read(file, file.RootOffset);
            byte[] rewritten = CollisionPackfileWriter.WriteCompressedMesh(mesh);
            if (rewritten.SequenceEqual(bytes)) same++;
            else if (rewriteMismatch.Count < 3)
                rewriteMismatch.Add($"{tag:X8} length {rewritten.Length}/{bytes.Length} first difference 0x{FirstDifference(rewritten, bytes):X}");
            var found = mesh.Check();
            if (found.Count == 0) installedClean++;
            foreach (var problem in found) Add(problems, System.Text.RegularExpressions.Regex.Replace(problem, @"\d+", "N"));
            var shape = CollisionShapes.Decode(file, file.RootOffset);
            custom += shape.CustomPrimitives;
            customDecoded += shape.CustomPrimitives - shape.UndecodedCustomPrimitives;

            // Re-encode every decoded triangle, custom hulls too, with its run's palette indices.
            var runs = shape.TriangleKeys.Select(k => mesh.RunOf(mesh.Sections[(int)(k >> 8)], (int)(k >> 1) & 0x7F)).ToArray();
            var source = new CompressedMeshSource(shape.Vertices, shape.Indices)
            {
                FilterIndices = runs.Select(r => (byte)r.FilterIndex).ToArray(), UserDataIndices = runs.Select(r => (byte)r.UserDataIndex).ToArray(),
                FilterPalette = mesh.FilterPalette, UserDataPalette = mesh.UserDataPalette, ConvexRadius = mesh.ConvexRadius, Prefix = mesh.Prefix
            };
            var (roundTrip, _, worst) = MeshSyntheticChecks.RoundTrip(source, $"{tag:X8}");
            encoded++;
            sourceTriangles += shape.TriangleCount;
            encodedTriangles += roundTrip.Keys().Count();
            encodeRatio = Math.Max(encodeRatio, worst / MeshSyntheticChecks.Tolerance(roundTrip));

            if (shape.CustomPrimitives == shape.UndecodedCustomPrimitives || shape.UndecodedCustomPrimitives == 0)
            {
                moveRatio = Math.Max(moveRatio, Move(mesh, shape, bytes.Length, tag));
                moved++;
            }
        }
        string problemText = problems.Count == 0 ? "none" : string.Join("; ", problems.Select(x => $"{x.Value}x {x.Key}"));
        Console.WriteLine($"PASS collision-mesh-package package={package:X4} compounds={index.Entities.Count} children={tags.Length} "
            + $"meshes={meshes} custom={custom} custom-decoded={customDecoded} rewrite-same-bytes={same}/{meshes} "
            + $"installed-check-clean={installedClean}/{meshes} encoded={encoded} triangles={sourceTriangles}->{encodedTriangles} "
            + $"encode-error/tolerance<={encodeRatio:0.###} moved={moved} move-error/tolerance<={moveRatio:0.###} "
            + $"convex={convex} convex-rewrite-same-bytes={convexSame}/{convex} rebuilt-plane-count-same={planeCountSame}/{convex} "
            + $"rebuilt-planes-among-stored={rebuiltSubset}/{convex} stored-planes-not-rebuilt={bevels} "
            + $"matched-plane-angle<={planeAngle:0.#####} matched-plane-offset<={planeOffset:0.#####} convex-moved={convexMoved}/{convex}");
        Console.WriteLine($"  installed check problems: {problemText}");
        foreach (var line in rewriteMismatch) Console.WriteLine($"  rewrite differs: {line}");
        foreach (var line in convexFailures) Console.WriteLine($"  convex move not closed: {line}");
        Check(same == meshes && convexSame == convex, "A rewritten installed packfile differs from its bytes.");
        Check(customDecoded == custom, "A custom primitive did not decode.");
    }

    /**
     * Moves every vertex by a scale, rotation and translation; checks size, invariants, positions.
     * @return The largest vertex error divided by its tolerance.
     */
    private static double Move(CompressedMesh mesh, CollisionShape shape, int length, uint tag)
    {
        var turn = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(-2, 1, 4)), 0.9f);
        var shift = new Vector3(-31, 12.5f, 4);
        Vector3 Transform(Vector3 v) => Vector3.Transform(v * 1.25f, turn) + shift;
        var result = CollisionShapeEdits.MoveVertices(mesh, shape.Vertices.Select(Transform).ToArray());
        byte[] written = CollisionPackfileWriter.WriteCompressedMesh(result);
        Check(written.Length == length, $"{tag:X8}: moving vertices changed the file size.");
        var file = HavokPackfile.Parse(written);
        var reread = CompressedMesh.Read(file, file.RootOffset);
        Check(!reread.Check().Except(mesh.Check()).Any(), $"{tag:X8}: a moved mesh breaks an invariant.");
        var movedShape = CollisionShapes.Decode(file, file.RootOffset);
        double meshTolerance = MeshSyntheticChecks.Tolerance(reread), worst = 0;
        // Custom vertices are coded over a leaf box, at most the section domain, in 10 bits.
        double customTolerance = meshTolerance + reread.Sections.Max(s => MeshSyntheticChecks.MaxAxis(s.DomainMax, s.DomainMin)) / 1023 * 1.5;
        int meshIndices = 3 * shape.MeshTriangleCount;
        for (int i = 0; i < shape.Indices.Length; i++)
        {
            int v = shape.Indices[i];
            double error = MeshSyntheticChecks.MaxAxis(movedShape.Vertices[v], Transform(shape.Vertices[v]));
            double tolerance = i < meshIndices ? meshTolerance : customTolerance;
            Check(error <= tolerance, $"{tag:X8}: moved vertex {v} is {error} off, above {tolerance}.");
            worst = Math.Max(worst, error / tolerance);
        }
        return worst;
    }

    /** Rewrites a convex child with its own planes, compares rebuilt planes, and moves it. */
    private static (bool Same, bool CountSame, double Angle, double Offset, string? Moved, int Missing, bool Subset) ConvexChild(HavokPackfile file, byte[] bytes)
    {
        int root = file.RootOffset;
        var shape = CollisionShapes.Decode(file, root);
        var (planeCount, planeData) = file.Array(root + 0x68);
        var stored = Enumerable.Range(0, planeCount).Select(i => new Vector4(file.V3(planeData + 16 * i), file.F32(planeData + 16 * i + 12))).ToArray();
        var prefix = file.Bytes.AsSpan(file.Absolute(root), 0x30).ToArray();
        bool same = CollisionPackfileWriter.WriteConvexVertices(shape.Vertices, stored, file.F32(root + 0x20), prefix).SequenceEqual(bytes);
        var rebuilt = ConvexHull.Planes(shape.Vertices);
        float extent = Math.Max(1, (shape.BoundsMax - shape.BoundsMin).Length());
        // A stored plane with no rebuilt one within 1e-3 rad and 1e-3 of the size is a bevel.
        bool Near(Vector4 a, Vector4 b) => Vector3.Dot(new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z)) > MathF.Cos(1e-3f)
            && MathF.Abs(a.W - b.W) <= 1e-3f * extent;
        int missing = stored.Count(plane => !rebuilt.Any(r => Near(r, plane)));
        bool subset = rebuilt.All(r => stored.Any(plane => Near(r, plane)));
        double angle = 0, offset = 0;
        foreach (var plane in stored.Where(plane => rebuilt.Any(r => Near(r, plane))))
        {
            var normal = new Vector3(plane.X, plane.Y, plane.Z);
            var match = rebuilt.MaxBy(r => Vector3.Dot(new Vector3(r.X, r.Y, r.Z), normal));
            angle = Math.Max(angle, Math.Acos(Math.Clamp(Vector3.Dot(new Vector3(match.X, match.Y, match.Z), normal), -1, 1)));
            offset = Math.Max(offset, Math.Abs(match.W - plane.W));
        }
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f);
        var movedVertices = shape.Vertices.Select(v => Vector3.Transform(v * 2, turn) + new Vector3(3, 4, 5)).ToArray();
        var movedFile = HavokPackfile.Parse(CollisionShapeEdits.MoveConvexVertices(file, movedVertices));
        var (movedCount, movedPlanes) = movedFile.Array(movedFile.RootOffset + 0x68);
        float size = Math.Max(1, (movedVertices.Aggregate(Vector3.Max) - movedVertices.Aggregate(Vector3.Min)).Length());
        double outside = Enumerable.Range(0, movedCount).Max(i => movedVertices.Max(v =>
            Vector3.Dot(movedFile.V3(movedPlanes + 16 * i), v) + movedFile.F32(movedPlanes + 16 * i + 12)));
        string? problem = outside > 1e-5 * size ? $"vertex outside a plane by {outside}"
            : movedCount < 4 ? $"only {movedCount} planes for {shape.Vertices.Length} vertices, {stored.Length} stored" : null;
        return (same, rebuilt.Length == planeCount, angle, offset, problem, missing, subset);
    }

    private static int FirstDifference(byte[] a, byte[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) if (a[i] != b[i]) return i;
        return Math.Min(a.Length, b.Length);
    }

    private static void Add(SortedDictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
}
