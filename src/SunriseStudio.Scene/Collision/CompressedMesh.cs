using System.Numerics;

namespace SunriseStudio.Scene.Collision;

/** A compressed mesh section: at most 128 primitives, packed vertices coded over its box. */
public sealed class CompressedMeshSection
{
    /** 4-byte section tree nodes. */
    public byte[] Nodes { get; set; } = [];
    public Vector3 DomainMin { get; set; }
    public Vector3 DomainMax { get; set; }
    /** The W lanes of the domain. The client does not read them; installed meshes hold 0 or 1. */
    public float DomainMinW { get; set; }
    public float DomainMaxW { get; set; }
    /** Packed vertex offset in [0..2] and step in [3..5]. */
    public float[] Codec { get; set; } = new float[6];
    public int FirstPackedVertex { get; set; }
    /** +0x4C: first shared index slot, and the packed vertex count in the low byte. */
    public int SharedIndexStart { get; set; }
    public int PackedCount { get; set; }
    public int FirstPrimitive { get; set; }
    public int PrimitiveCount { get; set; }
    public int FirstRun { get; set; }
    public int RunCount { get; set; }
    /** +0x58 and +0x59; the client reads the packed count at +0x4C instead. */
    public int NumPackedVertices { get; set; }
    public int NumSharedIndices { get; set; }
    /** +0x5A: this section's leaf node in the top tree. */
    public int LeafIndex { get; set; }
    /** +0x5C: adds Page * 65536 to every shared vertex number of the section. */
    public int Page { get; set; }
    /** +0x5D: 1 when the section holds a custom primitive, 0 otherwise. */
    public int Flags { get; set; }
    public int LayerData { get; set; }
    public int Unused { get; set; }

    public CompoundBox Domain => CompoundBox.From(DomainMin, DomainMax);
}

/** A data run: palette indices for primitives First to First + Count - 1 of its section. */
public readonly record struct CompressedMeshRun(uint Value, byte First, byte Count)
{
    public int FilterIndex => (int)(Value & 0xFF);
    public int UserDataIndex => (int)((Value >> 8) & 0xFF);
    public int Welding => (int)(Value >> 16);
}

/** Primitive kind from a section's primitive word. */
public enum CompressedPrimitiveType { Empty, Triangle, Quad, Custom }

/**
 * A decoded custom primitive. Kind 0 is a box expanded from 4 corners, 1 to 3 a convex vertex set,
 * 4 a cylinder between two points. Vertices are in shape space.
 */
public sealed record CustomPrimitive(int Section, int Primitive, int Kind, int Codec, bool HasRadius, float Radius,
    float CylinderRadius, Vector3[] Vertices, int HeaderSlot, int DataStart);

/**
 * An hkpBvCompressedMeshShape read into arrays. The encoder fills the same arrays, so a written
 * mesh decodes through the same code as an installed one.
 */
public sealed class CompressedMesh
{
    public const string ClassName = "hkpBvCompressedMeshShape";
    // Object, tree and section sizes come from the hkClass layouts in the image.
    public const int ObjectSize = 0x110, TreeOffset = 0x70, SectionSize = 0x60, PrefixSize = 0x70;
    public const uint EmptyPrimitive = 0xADDEADDE;
    // Havok's no-welding type; every installed mesh uses it.
    public const byte NoWelding = 6;

    /** Object bytes before the tree. The writer keeps them and rewrites the fields below. */
    public byte[] Prefix { get; set; } = new byte[PrefixSize];
    public float ConvexRadius { get; set; }
    public byte WeldingType { get; set; } = NoWelding;
    public bool HasPerPrimitiveCollisionFilterInfo { get; set; }
    public bool HasPerPrimitiveUserData { get; set; }
    public uint[] FilterPalette { get; set; } = [];
    public uint[] UserDataPalette { get; set; } = [];
    /** Count of user strings; no installed mesh has one and the writer refuses them. */
    public int UserStringCount { get; set; }

    /** 5-byte top tree nodes over the sections. */
    public byte[] TopNodes { get; set; } = [];
    public Vector3 DomainMin { get; set; }
    public Vector3 DomainMax { get; set; }
    public float DomainMinW { get; set; }
    public float DomainMaxW { get; set; }
    public int NumPrimitiveKeys { get; set; }
    public int BitsPerKey { get; set; }
    public uint MaxKeyValue { get; set; }
    public List<CompressedMeshSection> Sections { get; set; } = [];
    public uint[] Primitives { get; set; } = [];
    public ushort[] SharedIndices { get; set; } = [];
    public uint[] PackedVertices { get; set; } = [];
    public ulong[] SharedVertices { get; set; } = [];
    public CompressedMeshRun[] Runs { get; set; } = [];

    public CompoundBox Domain => CompoundBox.From(DomainMin, DomainMax);

    /** Reads the mesh object at a data offset. */
    public static CompressedMesh Read(HavokPackfile file, int offset)
    {
        if (file.ClassAt(offset) != ClassName) throw new InvalidDataException("Object is not a compressed mesh.");
        int tree = offset + TreeOffset;
        var mesh = new CompressedMesh
        {
            Prefix = file.Bytes.AsSpan(file.Absolute(offset), PrefixSize).ToArray(),
            ConvexRadius = file.F32(offset + 0x30), WeldingType = file.U8(offset + 0x34),
            HasPerPrimitiveCollisionFilterInfo = file.U8(offset + 0x35) != 0, HasPerPrimitiveUserData = file.U8(offset + 0x36) != 0,
            FilterPalette = U32s(file, offset + 0x38), UserDataPalette = U32s(file, offset + 0x48),
            UserStringCount = file.I32(offset + 0x60),
            TopNodes = Bytes(file, tree, MeshTree.TopNodeSize),
            DomainMin = file.V3(tree + 0x10), DomainMinW = file.F32(tree + 0x1C),
            DomainMax = file.V3(tree + 0x20), DomainMaxW = file.F32(tree + 0x2C),
            NumPrimitiveKeys = file.I32(tree + 0x30), BitsPerKey = file.I32(tree + 0x34), MaxKeyValue = file.U32(tree + 0x38),
            Primitives = U32s(file, tree + 0x50),
            PackedVertices = U32s(file, tree + 0x70)
        };
        var (indexCount, indexData) = file.Array(tree + 0x60);
        mesh.SharedIndices = Enumerable.Range(0, indexCount).Select(i => file.U16(indexData + 2 * i)).ToArray();
        var (sharedCount, shared) = file.Array(tree + 0x80);
        mesh.SharedVertices = Enumerable.Range(0, sharedCount).Select(i => file.U64(shared + 8 * i)).ToArray();
        var (runCount, runs) = file.Array(tree + 0x90);
        mesh.Runs = Enumerable.Range(0, runCount)
            .Select(i => new CompressedMeshRun(file.U32(runs + 8 * i), file.U8(runs + 8 * i + 4), file.U8(runs + 8 * i + 5))).ToArray();
        var (sectionCount, sections) = file.Array(tree + 0x40);
        for (int s = 0; s < sectionCount; s++)
        {
            int at = sections + SectionSize * s;
            uint shareRange = file.U32(at + 0x4C), primitiveRange = file.U32(at + 0x50), runRange = file.U32(at + 0x54);
            mesh.Sections.Add(new CompressedMeshSection
            {
                Nodes = Bytes(file, at, MeshTree.SectionNodeSize),
                DomainMin = file.V3(at + 0x10), DomainMinW = file.F32(at + 0x1C),
                DomainMax = file.V3(at + 0x20), DomainMaxW = file.F32(at + 0x2C),
                Codec = Enumerable.Range(0, 6).Select(i => file.F32(at + 0x30 + 4 * i)).ToArray(),
                FirstPackedVertex = file.I32(at + 0x48),
                SharedIndexStart = (int)(shareRange >> 8), PackedCount = (int)(shareRange & 0xFF),
                FirstPrimitive = (int)(primitiveRange >> 8), PrimitiveCount = (int)(primitiveRange & 0xFF),
                FirstRun = (int)(runRange >> 8), RunCount = (int)(runRange & 0xFF),
                NumPackedVertices = file.U8(at + 0x58), NumSharedIndices = file.U8(at + 0x59), LeafIndex = file.U16(at + 0x5A),
                Page = file.U8(at + 0x5C), Flags = file.U8(at + 0x5D), LayerData = file.U8(at + 0x5E), Unused = file.U8(at + 0x5F)
            });
        }
        return mesh;
    }

    /** Shared vertex i: 21, 21 and 22 bits over the mesh domain, in double math. */
    public Vector3 SharedVertex(int index) => DecodeShared(SharedVertices[index]);

    /** Decodes a shared vertex word to a position over the mesh domain. */
    public Vector3 DecodeShared(ulong v)
    {
        double extentX = (double)DomainMax.X - DomainMin.X, extentY = (double)DomainMax.Y - DomainMin.Y;
        double extentZ = (double)DomainMax.Z - DomainMin.Z;
        return new((float)((v & 0x1FFFFF) * (extentX / 0x1FFFFF) + DomainMin.X),
            (float)(((v >> 21) & 0x1FFFFF) * (extentY / 0x1FFFFF) + DomainMin.Y), (float)((v >> 42) * (extentZ / 0x3FFFFF) + DomainMin.Z));
    }

    /** Packed vertex k of a section: 11, 11 and 10 bits, offset codec[0..2], step codec[3..5]. */
    public Vector3 PackedVertex(CompressedMeshSection section, int k) =>
        DecodePacked(section.Codec, PackedVertices[section.FirstPackedVertex + k]);

    /** Decodes a packed vertex word using the section codec. */
    public static Vector3 DecodePacked(float[] c, uint v) => new((float)(c[0] + (double)(v & 0x7FF) * c[3]),
        (float)(c[1] + (double)((v >> 11) & 0x7FF) * c[4]), (float)(c[2] + (double)(v >> 22) * c[5]));

    /** Shared vertex number of a primitive index at or above the section's packed count. */
    public int SharedNumber(CompressedMeshSection section, int k) =>
        section.Page * 65536 + SharedIndices[section.SharedIndexStart + k - section.PackedCount];

    /** A primitive index as a position: packed below the packed count, shared above it. */
    public Vector3 Vertex(CompressedMeshSection section, int k) =>
        k < section.PackedCount ? PackedVertex(section, k) : SharedVertex(SharedNumber(section, k));

    /** The primitive word p of the section. */
    public uint Primitive(CompressedMeshSection section, int p) => Primitives[section.FirstPrimitive + p];

    /** Classifies a primitive word: empty, triangle, quad or custom (hull) primitive. */
    public static CompressedPrimitiveType TypeOf(uint primitive)
    {
        if (primitive == EmptyPrimitive) return CompressedPrimitiveType.Empty;
        int b = (int)(primitive >> 8) & 0xFF, c = (int)(primitive >> 16) & 0xFF, d = (int)(primitive >> 24);
        return b == c && c == d ? CompressedPrimitiveType.Custom : c == d ? CompressedPrimitiveType.Triangle : CompressedPrimitiveType.Quad;
    }

    /** The data run that covers primitive p of a section, as the client's search finds it. */
    public CompressedMeshRun RunOf(CompressedMeshSection section, int p)
    {
        for (int i = 0; i < section.RunCount; i++)
        {
            var run = Runs[section.FirstRun + i];
            if (p >= run.First && p < run.First + run.Count) return run;
        }
        throw new InvalidDataException("No data run covers the primitive.");
    }

    /**
     * Decodes a custom primitive the way getChildShape does.
     * @return Null for a vertex codec or kind the client does not implement.
     */
    public CustomPrimitive? DecodeCustom(int sectionIndex, int p)
    {
        var section = Sections[sectionIndex];
        uint primitive = Primitive(section, p);
        int i0 = (int)(primitive & 0xFF), leaf = (int)(primitive >> 8) & 0xFF;
        // The header is the three u16 values from i0's shared index slot on.
        int slot = section.SharedIndexStart + i0 - section.PackedCount;
        // Words 2 and 3 exist only when the radius flag or the cylinder kind reads them.
        if (slot < 0 || slot + 2 > SharedIndices.Length)
            throw new InvalidDataException($"Custom primitive {sectionIndex}/{p} header slot {slot} leaves the shared index array.");
        ushort h0 = SharedIndices[slot], h1 = SharedIndices[slot + 1];
        int kind = h0 & 15, codec = (h0 >> 4) & 3, count = h0 >> 8;
        bool hasRadius = (h0 & 0xC0) != 0;
        if (codec == 3 || kind > 4) return null;
        float radius = kind == 4 || hasRadius ? BitConverter.Int32BitsToSingle(HeaderWord(slot + 2) << 16) : ConvexRadius;
        float cylinder = kind == 4 ? BitConverter.Int32BitsToSingle(HeaderWord(slot + 3) << 16) : 0;
        int start = section.Page * 65536 + h1;
        int words = codec switch { 0 => count, 1 => (count + 1) / 2, _ => (count + 3) / 4 };
        if (start + words > SharedVertices.Length)
            throw new InvalidDataException($"Custom primitive {sectionIndex}/{p} data {start}+{words} leaves the shared vertex array of {SharedVertices.Length}.");
        var vertices = new Vector3[count];
        var box = codec == 0 ? default : MeshTree.SectionNodeBox(section.Nodes, section.Domain, leaf);
        for (int j = 0; j < count; j++) vertices[j] = CustomVertex(codec, box, start, j);
        if (kind == 0) vertices = ExpandBox(vertices);
        return new(sectionIndex, p, kind, codec, hasRadius, radius, cylinder, vertices, slot, start);
    }

    private int HeaderWord(int slot) => slot < SharedIndices.Length
        ? SharedIndices[slot] : throw new InvalidDataException("Custom primitive header leaves the shared index array.");

    /** Custom vertex j of a data start, in the given vertex codec over the leaf box. */
    public Vector3 CustomVertex(int codec, CompoundBox box, int start, int j)
    {
        switch (codec)
        {
            case 0: return SharedVertex(start + j);
            case 1:
            {
                // u32 values in swapped pairs over the u64 array: vertex j is u32 j ^ 1.
                ulong pair = SharedVertices[start + (j >> 1)];
                uint v = (j & 1) == 0 ? (uint)(pair >> 32) : (uint)pair;
                return OverBox(box, v & 0x7FF, (v >> 11) & 0x7FF, v >> 22, 2047, 2047, 1023);
            }
            case 2:
            {
                // u16 values reversed in each group of four: vertex j is u16 j ^ 3.
                ulong quad = SharedVertices[start + (j >> 2)];
                uint v = (uint)(quad >> (16 * (3 - (j & 3)))) & 0xFFFF;
                return OverBox(box, v & 0x1F, (v >> 5) & 0x1F, v >> 10, 31, 31, 63);
            }
            default: throw new InvalidDataException("Custom primitive vertex codec 3 is not implemented by the client.");
        }
    }

    private static Vector3 OverBox(CompoundBox box, uint x, uint y, uint z, double sx, double sy, double sz) => new(
        (float)(box.MinX + x * ((box.MaxX - box.MinX) / sx)), (float)(box.MinY + y * ((box.MaxY - box.MinY) / sy)),
        (float)(box.MinZ + z * ((box.MaxZ - box.MinZ) / sz)));

    /** Corner 0 and its three edge neighbours; corners 4 to 7 are added as the client adds them. */
    private static Vector3[] ExpandBox(Vector3[] c)
    {
        if (c.Length < 4) throw new InvalidDataException("A box primitive needs four corners.");
        var d = c.Select(v => (X: (double)v.X, Y: (double)v.Y, Z: (double)v.Z)).ToArray();
        var e3 = Sub(d[3], d[0]);
        var c4 = Add(Sub(d[2], d[0]), d[1]);
        var c5 = Add(d[2], e3);
        var c6 = Add(Sub(d[1], d[0]), d[3]);
        var c7 = Add(c4, e3);
        return [c[0], c[1], c[2], c[3], F(c4), F(c5), F(c6), F(c7)];

        static (double X, double Y, double Z) Sub((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
            (a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        static (double X, double Y, double Z) Add((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
            (a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        static Vector3 F((double X, double Y, double Z) v) => new((float)v.X, (float)v.Y, (float)v.Z);
    }

    /** Pre-order walk of the top tree; leaf values are section numbers. */
    public MeshTreeNode[] TopTree() => MeshTree.Walk(MeshTreeKind.Top, TopNodes, Domain, Sections.Count);

    /** Pre-order walk of one section tree; leaf values are primitive numbers. */
    public MeshTreeNode[] SectionTree(int section) =>
        MeshTree.Walk(MeshTreeKind.Section, Sections[section].Nodes, Sections[section].Domain, Sections[section].PrimitiveCount);

    /**
     * Shape keys (section << 8 | primitive << 1 | half) in decode order: one per triangle or
     * custom primitive, two per quad.
     */
    public IEnumerable<uint> Keys()
    {
        for (int s = 0; s < Sections.Count; s++)
            for (int p = 0; p < Sections[s].PrimitiveCount; p++)
            {
                var type = TypeOf(Primitive(Sections[s], p));
                if (type == CompressedPrimitiveType.Empty) continue;
                yield return (uint)(s << 8 | p << 1);
                if (type == CompressedPrimitiveType.Quad) yield return (uint)(s << 8 | p << 1 | 1);
            }
    }

    /**
     * Checks the invariants the client relies on when it walks and decodes the mesh.
     * @param tolerance Slack for box enclosure, as a fraction of the mesh domain's largest extent.
     * @return One line per broken invariant; empty when the mesh holds them all.
     */
    public List<string> Check(double tolerance = 1e-6)
    {
        var problems = new List<string>();
        double slack = tolerance * Math.Max(1e-6, new[] { DomainMax.X - DomainMin.X, DomainMax.Y - DomainMin.Y, DomainMax.Z - DomainMin.Z }.Max());
        MeshTreeNode[] top;
        try { top = TopTree(); }
        catch (InvalidDataException error) { return [$"top tree: {error.Message}"]; }
        var leafOf = new Dictionary<int, MeshTreeNode>();
        foreach (var node in top.Where(n => n.Right < 0))
            if (!leafOf.TryAdd(node.Leaf, node)) problems.Add($"top tree reaches section {node.Leaf} twice");
        if (TopNodes.Length != (2 * Sections.Count - 1) * MeshTree.TopNodeSize) problems.Add("top tree node count is not 2n - 1");
        if (Sections.Count > MeshTree.MaxSections) problems.Add("more sections than a top leaf can name");
        int keys = 0;
        uint maxKey = 0;
        for (int s = 0; s < Sections.Count; s++)
        {
            var section = Sections[s];
            string at = $"section {s}";
            if (!leafOf.TryGetValue(s, out var leaf)) { problems.Add($"{at}: no top leaf"); continue; }
            if (!leaf.Box.Contains(section.Domain, slack)) problems.Add($"{at}: top leaf box does not enclose the section domain");
            if (section.LeafIndex != leaf.Index) problems.Add($"{at}: leaf index {section.LeafIndex} is not top node {leaf.Index}");
            if (section.PackedCount != section.NumPackedVertices) problems.Add($"{at}: packed counts at +0x4C and +0x58 differ");
            if (section.PrimitiveCount is 0 or > MeshTree.MaxPrimitives) problems.Add($"{at}: primitive count out of range");
            if (section.FirstPrimitive + section.PrimitiveCount > Primitives.Length
                || section.FirstPackedVertex + section.NumPackedVertices > PackedVertices.Length
                || section.SharedIndexStart + section.NumSharedIndices > SharedIndices.Length
                || section.FirstRun + section.RunCount > Runs.Length)
            {
                problems.Add($"{at}: a range leaves its array");
                continue;
            }
            MeshTreeNode[] tree;
            try { tree = SectionTree(s); }
            catch (InvalidDataException error) { problems.Add($"{at} tree: {error.Message}"); continue; }
            if (section.Nodes.Length != (2 * section.PrimitiveCount - 1) * MeshTree.SectionNodeSize) problems.Add($"{at}: tree node count is not 2p - 1");
            var primitiveLeaf = new Dictionary<int, MeshTreeNode>();
            foreach (var node in tree.Where(n => n.Right < 0))
                if (!primitiveLeaf.TryAdd(node.Leaf, node)) problems.Add($"{at}: tree reaches primitive {node.Leaf} twice");
            var covered = new int[section.PrimitiveCount];
            int previousEnd = 0;
            for (int r = 0; r < section.RunCount; r++)
            {
                var run = Runs[section.FirstRun + r];
                if (run.First < previousEnd) problems.Add($"{at}: data runs overlap or are unsorted");
                previousEnd = run.First + run.Count;
                for (int p = run.First; p < run.First + run.Count && p < covered.Length; p++) covered[p]++;
                if (FilterPalette.Length > 0 && run.FilterIndex >= FilterPalette.Length) problems.Add($"{at}: run filter index leaves the palette");
                if (UserDataPalette.Length > 0 && run.UserDataIndex >= UserDataPalette.Length) problems.Add($"{at}: run user data index leaves the palette");
            }
            if (covered.Any(c => c != 1)) problems.Add($"{at}: data runs do not cover each primitive once");
            bool hasCustom = false;
            for (int p = 0; p < section.PrimitiveCount; p++)
            {
                uint primitive = Primitive(section, p);
                var type = TypeOf(primitive);
                if (type == CompressedPrimitiveType.Empty) continue;
                keys += type == CompressedPrimitiveType.Quad ? 2 : 1;
                maxKey = Math.Max(maxKey, (uint)(s << 8 | p << 1 | (type == CompressedPrimitiveType.Quad ? 1 : 0)));
                if (!primitiveLeaf.TryGetValue(p, out var node)) { problems.Add($"{at}: primitive {p} has no leaf"); continue; }
                Vector3[] points;
                if (type == CompressedPrimitiveType.Custom)
                {
                    hasCustom = true;
                    if ((primitive >> 8 & 0xFF) != node.Index) problems.Add($"{at}: custom primitive {p} does not name its own leaf");
                    var custom = DecodeCustom(s, p);
                    if (custom is null) { problems.Add($"{at}: custom primitive {p} does not decode"); continue; }
                    points = custom.Kind == 4 ? custom.Vertices[..2] : custom.Vertices;
                }
                else
                {
                    int count = type == CompressedPrimitiveType.Quad ? 4 : 3;
                    var slots = Enumerable.Range(0, count).Select(i => (int)(primitive >> (8 * i)) & 0xFF).ToArray();
                    if (slots.Any(k => k >= section.PackedCount + section.NumSharedIndices
                        || (k >= section.PackedCount && SharedNumber(section, k) >= SharedVertices.Length)))
                    {
                        problems.Add($"{at}: primitive {p} names a missing vertex");
                        continue;
                    }
                    points = slots.Select(k => Vertex(section, k)).ToArray();
                }
                foreach (var point in points)
                {
                    var box = CompoundBox.From(point, point);
                    if (!node.Box.Contains(box, slack)) { problems.Add($"{at}: leaf of primitive {p} does not enclose its vertices"); break; }
                    if (!Domain.Contains(box, slack)) { problems.Add($"{at}: primitive {p} leaves the mesh domain"); break; }
                }
            }
            if (section.Flags != (hasCustom ? 1 : 0)) problems.Add($"{at}: flags {section.Flags} do not say whether it holds a custom primitive");
        }
        // Installed meshes store the key count or more, never fewer; the reader was not traced.
        if (NumPrimitiveKeys < keys) problems.Add($"key count {NumPrimitiveKeys} is below {keys}");
        if (MaxKeyValue != maxKey) problems.Add($"max key 0x{MaxKeyValue:X} is not 0x{maxKey:X}");
        if (BitsPerKey != BitLength(maxKey + 1)) problems.Add($"bits per key {BitsPerKey} is not {BitLength(maxKey + 1)}");
        return problems;
    }

    /** Bits needed for a value; the key width is the width of the largest key plus one. */
    public static int BitLength(uint value) => 32 - BitOperations.LeadingZeroCount(value);

    private static uint[] U32s(HavokPackfile file, int field)
    {
        var (count, data) = file.Array(field);
        return Enumerable.Range(0, count).Select(i => file.U32(data + 4 * i)).ToArray();
    }

    private static byte[] Bytes(HavokPackfile file, int field, int elementSize)
    {
        var (count, data) = file.Array(field);
        return count == 0 ? [] : file.Bytes.AsSpan(file.Absolute(data), count * elementSize).ToArray();
    }
}
